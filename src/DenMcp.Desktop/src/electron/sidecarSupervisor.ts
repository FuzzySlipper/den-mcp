import { parseReadySentinelLine, type SidecarReadySentinel } from './sidecarProtocol.ts';

export type SidecarLifecycleState = 'idle' | 'starting' | 'ready' | 'reconnecting' | 'stopping' | 'stopped' | 'crashed';

export interface SidecarLaunchConfig {
  command: string;
  args: string[];
  env: Record<string, string>;
}

export interface SidecarProcessHandle {
  readonly pid?: number;
  on(event: 'exit', callback: (code: number | null, signal: string | null) => void): void;
  on(event: 'error', callback: (error: Error) => void): void;
  stdout?: SidecarReadable;
  stderr?: SidecarReadable;
  kill(signal?: string): boolean;
}

export interface SidecarReadable {
  on(event: 'data', callback: (chunk: string | Uint8Array) => void): void;
}

export interface SidecarProcessLauncher {
  launch(config: SidecarLaunchConfig): SidecarProcessHandle;
}

export interface SidecarBridgeConnector<TConnection = unknown> {
  connect(sentinel: SidecarReadySentinel): Promise<TConnection>;
  close?(connection: TConnection): Promise<void> | void;
}

export interface SidecarSupervisorOptions<TConnection = unknown> {
  launchConfig: SidecarLaunchConfig;
  launcher: SidecarProcessLauncher;
  connector?: SidecarBridgeConnector<TConnection>;
  restartOnCrash?: boolean;
  reconnectDelaysMs?: readonly number[];
  now?: () => string;
}

export interface SidecarSupervisorSnapshot {
  state: SidecarLifecycleState;
  pid?: number;
  ready?: SidecarReadySentinel;
  started_at: string | null;
  last_exit: { code: number | null; signal: string | null } | null;
  last_error: string | null;
  reconnect_attempt: number;
}

type StateListener = (snapshot: SidecarSupervisorSnapshot) => void;

const defaultReconnectDelays = [250, 500, 1000, 2000] as const;

export class SidecarSupervisor<TConnection = unknown> {
  private readonly options: SidecarSupervisorOptions<TConnection>;
  private readonly listeners = new Set<StateListener>();
  private process: SidecarProcessHandle | null = null;
  private connection: TConnection | null = null;
  private state: SidecarLifecycleState = 'idle';
  private startedAt: string | null = null;
  private ready: SidecarReadySentinel | undefined;
  private lastExit: { code: number | null; signal: string | null } | null = null;
  private lastError: string | null = null;
  private reconnectAttempt = 0;
  private restartTimer: ReturnType<typeof setTimeout> | null = null;
  private stdoutLineBuffer = '';
  private stopping = false;

  constructor(options: SidecarSupervisorOptions<TConnection>) {
    this.options = options;
  }

  snapshot(): SidecarSupervisorSnapshot {
    return {
      state: this.state,
      pid: this.process?.pid,
      ready: this.ready,
      started_at: this.startedAt,
      last_exit: this.lastExit,
      last_error: this.lastError,
      reconnect_attempt: this.reconnectAttempt,
    };
  }

  subscribe(listener: StateListener): () => void {
    this.listeners.add(listener);
    listener(this.snapshot());
    return () => this.listeners.delete(listener);
  }

  start(): SidecarSupervisorSnapshot {
    if (this.process) {
      return this.snapshot();
    }

    this.clearRestartTimer();
    this.stopping = false;
    this.state = 'starting';
    this.startedAt = this.now();
    this.lastExit = null;
    this.lastError = null;
    this.ready = undefined;
    this.reconnectAttempt = 0;
    this.resetStdoutLineBuffer();
    const child = this.options.launcher.launch(this.options.launchConfig);
    this.process = child;
    child.stdout?.on('data', (chunk) => this.handleStdout(chunk));
    child.stderr?.on('data', (chunk) => this.handleStderr(chunk));
    child.on('error', (error) => this.handleProcessError(error));
    child.on('exit', (code, signal) => this.handleExit(code, signal));
    this.emit();
    return this.snapshot();
  }

  async reconnect(): Promise<SidecarSupervisorSnapshot> {
    if (!this.ready) {
      throw new Error('Cannot reconnect to Den Desktop sidecar before the ready sentinel is received.');
    }

    if (!this.options.connector) {
      return this.snapshot();
    }

    this.state = 'reconnecting';
    this.reconnectAttempt += 1;
    this.emit();
    await this.replaceConnection(await this.options.connector.connect(this.ready));
    this.state = 'ready';
    this.emit();
    return this.snapshot();
  }

  async stop(signal = 'SIGTERM'): Promise<SidecarSupervisorSnapshot> {
    this.stopping = true;
    this.clearRestartTimer();
    this.resetStdoutLineBuffer();
    this.state = 'stopping';
    this.emit();
    if (this.connection && this.options.connector?.close) {
      await this.options.connector.close(this.connection);
    }

    this.connection = null;
    this.process?.kill(signal);
    if (!this.process) {
      this.state = 'stopped';
      this.emit();
    }

    return this.snapshot();
  }

  private handleStdout(chunk: string | Uint8Array): void {
    this.stdoutLineBuffer += decodeChunk(chunk);
    const lines = this.stdoutLineBuffer.split(/\r?\n/);
    this.stdoutLineBuffer = lines.pop() ?? '';

    for (const line of lines) {
      this.handleStdoutLine(line);
    }
  }

  private handleStdoutLine(line: string): void {
    const trimmed = line.trim();
    if (!trimmed) {
      return;
    }

    const sentinel = parseReadySentinelLine(trimmed);
    if (sentinel) {
      void this.markReady(sentinel);
    }
  }

  private async markReady(sentinel: SidecarReadySentinel): Promise<void> {
    this.ready = sentinel;
    this.reconnectAttempt = 0;
    if (this.options.connector) {
      await this.replaceConnection(await this.options.connector.connect(sentinel));
    }

    this.state = 'ready';
    this.emit();
  }

  private async replaceConnection(connection: TConnection): Promise<void> {
    if (this.connection && this.options.connector?.close) {
      await this.options.connector.close(this.connection);
    }

    this.connection = connection;
  }

  private handleStderr(chunk: string | Uint8Array): void {
    const text = decodeChunk(chunk).trim();
    if (text) {
      this.lastError = text;
      this.emit();
    }
  }

  private handleProcessError(error: Error): void {
    this.resetStdoutLineBuffer();
    this.lastError = error.message;
    this.state = this.stopping ? 'stopped' : 'crashed';
    this.emit();
  }

  private handleExit(code: number | null, signal: string | null): void {
    this.lastExit = { code, signal };
    this.process = null;
    this.connection = null;
    this.resetStdoutLineBuffer();
    if (this.stopping) {
      this.state = 'stopped';
      this.emit();
      return;
    }

    this.state = 'crashed';
    this.emit();
    if (this.options.restartOnCrash) {
      this.scheduleRestart();
    }
  }

  private scheduleRestart(): void {
    const delays = this.options.reconnectDelaysMs ?? defaultReconnectDelays;
    const delay = delays[Math.min(this.reconnectAttempt, delays.length - 1)] ?? 0;
    this.reconnectAttempt += 1;
    this.restartTimer = setTimeout(() => {
      this.restartTimer = null;
      this.start();
    }, delay);
  }

  private clearRestartTimer(): void {
    if (this.restartTimer) {
      clearTimeout(this.restartTimer);
      this.restartTimer = null;
    }
  }

  private resetStdoutLineBuffer(): void {
    this.stdoutLineBuffer = '';
  }

  private emit(): void {
    const snapshot = this.snapshot();
    for (const listener of this.listeners) {
      listener(snapshot);
    }
  }

  private now(): string {
    return this.options.now?.() ?? new Date().toISOString();
  }
}

interface CommonSidecarLaunchOptions {
  appId?: string;
  appVersion?: string;
  configPath: string;
  logPath?: string;
  authToken: string;
  port?: number;
  endpointPath?: string;
}

function buildSidecarAppArgs(options: CommonSidecarLaunchOptions): string[] {
  const args = [
    '--app-id',
    options.appId ?? 'den-desktop',
    '--app-version',
    options.appVersion ?? '0.1.0-dev',
    '--config-path',
    options.configPath,
    '--port',
    String(options.port ?? 0),
    '--endpoint-path',
    options.endpointPath ?? '/bridge',
  ];
  if (options.logPath) {
    args.push('--log-path', options.logPath);
  }

  return args;
}

export function buildDevSidecarLaunchConfig(options: CommonSidecarLaunchOptions & {
  dotnet?: string;
  projectPath: string;
}): SidecarLaunchConfig {
  return {
    command: options.dotnet ?? 'dotnet',
    args: [
      'run',
      '--project',
      options.projectPath,
      '--',
      ...buildSidecarAppArgs(options),
    ],
    env: {
      DEN_DESKTOP_BRIDGE_TOKEN: options.authToken,
    },
  };
}

export function buildPublishedSidecarLaunchConfig(options: CommonSidecarLaunchOptions & {
  dotnet?: string;
  sidecarPath: string;
}): SidecarLaunchConfig {
  const runsViaDotnet = options.sidecarPath.endsWith('.dll');
  return {
    command: runsViaDotnet ? (options.dotnet ?? 'dotnet') : options.sidecarPath,
    args: [
      ...(runsViaDotnet ? [options.sidecarPath] : []),
      ...buildSidecarAppArgs(options),
    ],
    env: {
      DEN_DESKTOP_BRIDGE_TOKEN: options.authToken,
    },
  };
}

function decodeChunk(chunk: string | Uint8Array): string {
  return typeof chunk === 'string' ? chunk : new TextDecoder().decode(chunk);
}
