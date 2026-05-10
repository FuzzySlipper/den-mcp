/**
 * Electron main-process entry for Den Desktop.
 *
 * Responsibilities:
 * - App lifecycle (ready, window-all-closed, activate)
 * - BrowserWindow creation with secure webPreferences
 * - Auth token generation and sidecar process launch via SidecarSupervisor
 * - WebSocket bridge connection lifecycle
 * - IPC bridge: exposes only allow-listed sidecar API to the renderer
 * - Loads built UI by default, or Vite dev server URL when hot mode is requested
 *
 * Security boundary: the renderer receives no raw token, endpoint URL,
 * Node APIs, shell access, or generic dispatch. All communication flows
 * through typed IPC channels that mirror the DenDesktopSidecarApi contract.
 */

import { app, BrowserWindow, ipcMain, Menu } from 'electron';
import * as path from 'node:path';
import * as crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { readFileSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { SidecarSupervisor, buildDevSidecarLaunchConfig, buildPublishedSidecarLaunchConfig } from './sidecarSupervisor.ts';
import {
  assertBridgeSchemaBundle,
  createCheckedBridgeClient,
  type BridgeEventFrame,
  type JsonValue,
} from '../bridge/contract.ts';
import { sidecarCommands, sidecarEvents } from './sidecarProtocol.ts';
import { createDenDesktopSidecarApi, type DenDesktopSidecarApi } from './preloadSidecarApi.ts';
import { createSidecarBridgeTransport } from './sidecarBridgeConnection.ts';
import { resolveRendererLoadTarget } from './rendererLoadMode.ts';
import { assertAllowedSidecarCallMethod, assertAllowedSidecarSubscriptionEvent } from './ipcAllowList.ts';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// ── Sidecar launch configuration ──

const AUTH_TOKEN = crypto.randomUUID();
const SIDECAR_PROJECT_PATH = path.resolve(__dirname, '../../DenMcp.Desktop.Sidecar/DenMcp.Desktop.Sidecar.csproj');
const SIDECAR_CONFIG_PATH = path.resolve(app.getPath('userData'), 'sidecar');
const SIDECAR_RELEASE_PATH = process.env.DEN_DESKTOP_SIDECAR_PATH;
const RELEASE_ROOT = process.env.DEN_DESKTOP_RELEASE_ROOT;
const RELEASE_COMMIT = process.env.DEN_DESKTOP_RELEASE_COMMIT;
const APP_VERSION = RELEASE_COMMIT ? `0.1.0+${RELEASE_COMMIT.slice(0, 12)}` : '0.1.0-dev';
const SIDECAR_READY_TIMEOUT_MS = 30_000;

// ── State ──

let mainWindow: BrowserWindow | null = null;
let supervisor: SidecarSupervisor | null = null;
let bridgeTransport: ReturnType<typeof createSidecarBridgeTransport> | null = null;
let sidecarApi: DenDesktopSidecarApi | null = null;
let bridgeClient: ReturnType<typeof createCheckedBridgeClient> | null = null;
let sidecarReadySentinel: { port: number; endpoint_path: string } | null = null;

// ── IPC channel names ──

const IPC_SIDECAR_CALL = 'den-desktop:sidecar-call';
const IPC_SIDECAR_SUBSCRIBE = 'den-desktop:sidecar-subscribe';
const IPC_SIDECAR_UNSUBSCRIBE = 'den-desktop:sidecar-unsubscribe';

// ── Schema bundle loading ──
// When bundled into electron-dist/, __dirname is src/DenMcp.Desktop/electron-dist/.
// The testdata fixture lives at the repo root: <repo>/testdata/...
// So from electron-dist we need ../../../testdata (up to src/DenMcp.Desktop, src, repo root).
const bundlePath = RELEASE_ROOT
  ? path.resolve(RELEASE_ROOT, 'testdata/den-desktop-sidecar/sidecar-wire-fixture.json')
  : path.resolve(__dirname, '../../../testdata/den-desktop-sidecar/sidecar-wire-fixture.json');

function loadSchemaBundle() {
  try {
    const raw = JSON.parse(readFileSync(bundlePath, 'utf8'));
    return raw.schema_bundle;
  } catch {
    return null;
  }
}

// ── Sidecar lifecycle ──

async function launchSidecar(): Promise<void> {
  const launchConfig = SIDECAR_RELEASE_PATH
    ? buildPublishedSidecarLaunchConfig({
        sidecarPath: SIDECAR_RELEASE_PATH,
        configPath: SIDECAR_CONFIG_PATH,
        authToken: AUTH_TOKEN,
        appVersion: APP_VERSION,
        port: 0,
      })
    : buildDevSidecarLaunchConfig({
        projectPath: SIDECAR_PROJECT_PATH,
        configPath: SIDECAR_CONFIG_PATH,
        authToken: AUTH_TOKEN,
        appVersion: APP_VERSION,
        port: 0,
      });

  supervisor = new SidecarSupervisor({
    launchConfig,
    launcher: {
      launch(config) {
        const child = spawn(config.command, config.args, {
          env: { ...process.env, ...config.env },
          stdio: ['ignore', 'pipe', 'pipe'],
        });
        return {
          pid: child.pid,
          on(event, callback) { child.on(event, callback as any); },
          stdout: child.stdout ? {
            on(event, callback) { child.stdout!.on(event, callback as any); },
          } : undefined,
          stderr: child.stderr ? {
            on(event, callback) { child.stderr!.on(event, callback as any); },
          } : undefined,
          kill(signal) { return child.kill(signal as any); },
        };
      },
    },
    restartOnCrash: false,
  });

  const readyPromise = new Promise<void>((resolve, reject) => {
    const timeout = setTimeout(() => {
      reject(new Error(`Sidecar did not emit ready sentinel within ${SIDECAR_READY_TIMEOUT_MS}ms.`));
    }, SIDECAR_READY_TIMEOUT_MS);

    const unsubscribe = supervisor!.subscribe((snapshot) => {
      if (snapshot.state === 'ready' && snapshot.ready) {
        sidecarReadySentinel = {
          port: snapshot.ready.port,
          endpoint_path: snapshot.ready.endpoint_path,
        };
        clearTimeout(timeout);
        unsubscribe();
        resolve();
      } else if (snapshot.state === 'crashed') {
        clearTimeout(timeout);
        unsubscribe();
        reject(new Error(`Sidecar crashed: ${snapshot.last_error ?? 'unknown error'}`));
      }
    });
  });

  supervisor.start();
  await readyPromise;
}

// ── Event source: tracks bridge event listeners and broadcasts incoming frames ──

type EventListener = (frame: BridgeEventFrame) => void;

const eventListeners = new Set<EventListener>();

function broadcastEvent(frame: BridgeEventFrame): void {
  for (const listener of eventListeners) {
    try {
      listener(frame);
    } catch {
      // Listener errors must not break other listeners.
    }
  }
}

const eventSource = {
  subscribe(listener: EventListener): () => void {
    eventListeners.add(listener);
    return () => {
      eventListeners.delete(listener);
    };
  },
};

// ── IPC subscription tracking ──

const activeSubscriptions = new Map<string, () => void>();

async function connectBridge(): Promise<void> {
  if (!sidecarReadySentinel) {
    throw new Error('Cannot connect bridge before sidecar is ready.');
  }

  const wsUrl = `http://127.0.0.1:${sidecarReadySentinel.port}`;
  const wsModule = await import('ws');
  const WebSocketCtor = wsModule.default ?? wsModule;

  bridgeTransport = createSidecarBridgeTransport({
    baseUrl: wsUrl,
    endpointPath: sidecarReadySentinel.endpoint_path,
    authToken: AUTH_TOKEN,
    WebSocketCtor: WebSocketCtor as any,
    onEvent: broadcastEvent,
  });

  const bundle = loadSchemaBundle();
  if (bundle) {
    assertBridgeSchemaBundle(bundle);
  }

  const client = createCheckedBridgeClient({
    bundle: bundle ?? {
      bundle_kind: 'den.bridge.schema_bundle' as const,
      version: 1 as const,
      bundle_id: 'den-desktop.sidecar@2026-04-29',
      protocol_version: '1.0',
      schema_version: 'den-desktop@2026-04-29',
      definitions: {} as any,
      commands: [],
      events: [],
    },
    commands: sidecarCommands,
    events: sidecarEvents,
    transport: bridgeTransport,
  });

  bridgeClient = client;
  sidecarApi = createDenDesktopSidecarApi(client, eventSource);
}

// ── Hotkey state ──

/**
 * Window-local hotkey registration compatibility no-op.
 *
 * Den Desktop hotkeys are handled locally in the renderer via
 * window.addEventListener('keydown', …). This function remains
 * as a no-op so that renderer/preload callers do not break.
 *
 * Previously this registered OS-global shortcuts via Electron's
 * globalShortcut, which blocked other applications while Den
 * Desktop was minimized or unfocused (task #1166).
 */
function registerHotkeys(_actions: Record<string, string>): void {
  // Intentionally empty — hotkeys are window-local in the renderer.
}

/** Send a hotkey action to the renderer. */
function dispatchHotkeyAction(action: string): void {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send('den-desktop:hotkey-action', action);
  }
}

// ── IPC bridge setup ──

function setupIpcBridge(): void {
  // Command dispatch: renderer calls 'den-desktop:sidecar-call' with method name + args
  ipcMain.handle(IPC_SIDECAR_CALL, async (_event, method: string, ...args: unknown[]) => {
    const allowedMethod = assertAllowedSidecarCallMethod(method);
    if (!sidecarApi) {
      throw new Error('Sidecar bridge is not connected.');
    }

    const apiMethod = sidecarApi[allowedMethod];
    if (typeof apiMethod !== 'function') {
      throw new Error(`Allowed sidecar method '${allowedMethod}' is unavailable.`);
    }

    return await (apiMethod as (...methodArgs: unknown[]) => Promise<unknown>)(...args);
  });

  // Event subscription: renderer requests subscription to an event channel.
  // The returned subscriptionId is used by the renderer to unsubscribe later.
  ipcMain.handle(IPC_SIDECAR_SUBSCRIBE, async (_event, eventName: string) => {
    const allowedEventName = assertAllowedSidecarSubscriptionEvent(eventName);
    if (!sidecarApi) {
      throw new Error('Sidecar bridge is not connected.');
    }

    const subscriptionMethod = `on${allowedEventName.charAt(0).toUpperCase()}${allowedEventName.slice(1)}` as keyof DenDesktopSidecarApi;
    const apiMethod = sidecarApi[subscriptionMethod];
    if (typeof apiMethod !== 'function') {
      throw new Error(`Allowed sidecar event subscription '${allowedEventName}' is unavailable.`);
    }

    const subscriptionId = `${allowedEventName}:${Date.now().toString(36)}`;

    const unsubscribe = (apiMethod as (listener: (payload: unknown) => void) => () => void)((payload: unknown) => {
      if (mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.webContents.send(`den-desktop:event:${allowedEventName}`, payload);
      }
    });

    // Track for deterministic cleanup on unsubscribe or window close.
    // If the renderer subscribes to the same event multiple times,
    // each gets a unique subscriptionId.
    activeSubscriptions.set(subscriptionId, unsubscribe);

    return { subscriptionId };
  });

  ipcMain.handle(IPC_SIDECAR_UNSUBSCRIBE, async (_event, subscriptionId: string) => {
    const unsubscribe = activeSubscriptions.get(subscriptionId);
    if (unsubscribe) {
      activeSubscriptions.delete(subscriptionId);
      unsubscribe();
    }
  });

  // Hotkey registration: renderer sends its hotkey config when settings load/change.
  // This is now a no-op; hotkeys are handled window-local in the renderer (task #1166).
  ipcMain.handle('den-desktop:hotkeys-register', async (_event, _actions: Record<string, string>) => {
    // Intentionally empty — globalShortcut removed to avoid capturing keys OS-wide.
  });

  // Progress-enabled console command: direct IPC handler that forwards progress
  // frames from the bridge transport to the renderer via a dedicated IPC channel.
  // This avoids serializing callbacks through the generic sidecar-call bridge.
  ipcMain.handle('den-desktop:console-run-command-with-progress', async (_event, request: unknown, progressChannel: string) => {
    if (!bridgeClient) {
      throw new Error('Sidecar bridge client is not connected.');
    }

    // Set up IPC listener for progress events from the sidecar bridge.
    // Each progress frame from the bridge transport's onProgress callback
    // is forwarded to the renderer through the dedicated progress channel.
    const onProgress = (frame: unknown) => {
      if (mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.webContents.send(progressChannel, frame);
      }
    };

    try {
      const result = await bridgeClient.call('consoleRunCommand', request as JsonValue, {
        expectsProgress: true,
        onProgress,
      });
      return result;
    } finally {
      ipcMain.removeAllListeners(progressChannel);
    }
  });
}

// ── Window creation ──

function setupRendererContextMenu(win: BrowserWindow): void {
  win.webContents.on('context-menu', (_event, params) => {
    const hasSelection = params.selectionText.trim().length > 0;
    const template: Electron.MenuItemConstructorOptions[] = params.isEditable
      ? [
          { role: 'cut' },
          { role: 'copy' },
          { role: 'paste' },
          { type: 'separator' },
          { role: 'selectAll' },
        ]
      : [
          { role: 'copy', enabled: hasSelection },
          { type: 'separator' },
          { role: 'selectAll' },
        ];

    Menu.buildFromTemplate(template).popup({ window: win });
  });
}

function setupAppCommandHandler(win: BrowserWindow): void {
  // Handle the Browser_Back app-command (mouse back button on Windows)
  win.on('app-command', (_event, command) => {
    if (command === 'browser-backward') {
      dispatchHotkeyAction('goBack');
    }
  });
}

function createWindow(): BrowserWindow {
  const win = new BrowserWindow({
    width: 1440,
    height: 960,
    minWidth: 1100,
    minHeight: 720,
    title: 'Den Operator',
    webPreferences: {
      preload: path.resolve(__dirname, './preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
      allowRunningInsecureContent: false,
      // Security: no remote module, no nodeIntegrationInWorker
    },
  });

  setupRendererContextMenu(win);
  setupAppCommandHandler(win);

  const loadTarget = resolveRendererLoadTarget({
    isPackaged: app.isPackaged,
    electronDistDir: __dirname,
  });
  if (loadTarget.kind === 'url') {
    win.loadURL(loadTarget.url);
  } else {
    win.loadFile(loadTarget.path);
  }

  return win;
}

// ── App lifecycle ──

app.whenReady().then(async () => {
  try {
    if (RELEASE_COMMIT) {
      console.log(`[DenDesktop] Starting release ${RELEASE_COMMIT}`);
    }
    await launchSidecar();
    await connectBridge();
    setupIpcBridge();
    mainWindow = createWindow();
  } catch (error) {
    console.error('[DenDesktop] Failed to start:', error);
    app.quit();
  }
});

app.on('window-all-closed', () => {
  // Cleanup IPC subscriptions, bridge, and sidecar on quit
  for (const [id, unsubscribe] of activeSubscriptions) {
    unsubscribe();
  }
  activeSubscriptions.clear();
  ipcMain.removeHandler('den-desktop:console-run-command-with-progress');
  bridgeTransport?.close();
  supervisor?.stop('SIGTERM');
  app.quit();
});

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) {
    mainWindow = createWindow();
  }
});

app.on('before-quit', () => {
  for (const [id, unsubscribe] of activeSubscriptions) {
    unsubscribe();
  }
  activeSubscriptions.clear();
  ipcMain.removeHandler('den-desktop:console-run-command-with-progress');
  bridgeTransport?.close();
  supervisor?.stop('SIGTERM');
});
