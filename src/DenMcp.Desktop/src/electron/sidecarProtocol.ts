import type {
  BridgeCommandSpec,
  BridgeEventFrame,
  BridgeEventSpec,
  BridgeSchemaBundle,
  CheckedBridgeClient,
  JsonValue,
} from '../bridge/contract.ts';
import { createBridgeCommandFacade } from '../bridge/contract.ts';

export const DEN_DESKTOP_READY_PREFIX = 'DEN_DESKTOP_BRIDGE_READY ';
export const DEN_DESKTOP_PROTOCOL_VERSION = '1.0';
export const DEN_DESKTOP_SCHEMA_VERSION = 'den-desktop@2026-04-29';
export const DEN_DESKTOP_SCHEMA_BUNDLE_ID = 'den-desktop.sidecar@2026-04-29';

export interface SidecarReadySentinel {
  port: number;
  endpoint_path: string;
  protocol_version: string;
  schema_version: string;
  schema_bundle_id: string;
  app_id: string;
  app_version: string;
}

export interface SidecarHealthResponse {
  process_id: number;
  uptime_ms: number;
  ready_state: string;
  app_id: string;
  app_version: string;
  config_path: string;
  log_path?: string;
  protocol_version: string;
  schema_version: string;
  schema_bundle_id: string;
  active_request_count: number;
  degraded_subsystems: string[];
  last_error?: JsonValue;
}

export interface SidecarCapabilitiesResponse {
  app_id: string;
  app_version: string;
  protocol_version: string;
  schema_version: string;
  schema_bundle_id: string;
  supported_transports: string[];
  commands: JsonValue[];
  events: JsonValue[];
  feature_flags: string[];
}

export interface ConsoleCommandDefinition {
  name: string;
  displayName: string;
  description: string;
  needsTarget: boolean;
}

export interface ConsoleCommandLine {
  level: string;
  timestamp: string;
  source: string;
  message: string;
}

export interface ConsoleCommandRunRequest {
  command: string;
  projectId?: string | null;
  taskId?: number | null;
  workspaceId?: string | null;
  sessionId?: string | null;
}

export interface ConsoleCommandRunResponse {
  command: string;
  status: string;
  errorMessage?: string | null;
  lines: ConsoleCommandLine[];
}

export interface ConsoleCommandListResponse {
  commands: ConsoleCommandDefinition[];
}

export const sidecarCommands: Record<string, BridgeCommandSpec<JsonValue, JsonValue>> = {
  consoleListCommands: {
    command: 'den_desktop.console.list_commands',
    requestSchema: 'den_desktop.console.list_commands.request',
    responseSchema: 'den_desktop.console.list_commands.response',
  },
  consoleRunCommand: {
    command: 'den_desktop.console.run_command',
    requestSchema: 'den_desktop.console.run_command.request',
    responseSchema: 'den_desktop.console.run_command.response',
  },
  getHealth: {
    command: 'bridge.get_health',
    requestSchema: 'bridge.get_health.request',
    responseSchema: 'bridge.get_health.response',
  },
  getCapabilities: {
    command: 'bridge.get_capabilities',
    requestSchema: 'bridge.get_capabilities.request',
    responseSchema: 'bridge.get_capabilities.response',
  },
  getOperatorStatus: {
    command: 'den_desktop.operator.get_status',
    requestSchema: 'den_desktop.operator.get_status.request',
    responseSchema: 'den_desktop.operator.get_status.response',
  },
  getSettings: {
    command: 'den_desktop.operator.get_settings',
    requestSchema: 'den_desktop.operator.get_settings.request',
    responseSchema: 'den_desktop.operator.get_settings.response',
  },
  saveOperatorSettings: {
    command: 'den_desktop.operator.save_settings',
    requestSchema: 'den_desktop.operator.save_settings.request',
    responseSchema: 'den_desktop.operator.save_settings.response',
  },
  getAppearanceSettings: {
    command: 'den_desktop.operator.get_appearance_settings',
    requestSchema: 'den_desktop.operator.get_appearance_settings.request',
    responseSchema: 'den_desktop.operator.get_appearance_settings.response',
  },
  saveAppearanceSettings: {
    command: 'den_desktop.operator.save_appearance_settings',
    requestSchema: 'den_desktop.operator.save_appearance_settings.request',
    responseSchema: 'den_desktop.operator.save_appearance_settings.response',
  },
  refreshNow: {
    command: 'den_desktop.operator.refresh_now',
    requestSchema: 'den_desktop.operator.refresh_now.request',
    responseSchema: 'den_desktop.operator.refresh_now.response',
  },
  listLocalSnapshots: {
    command: 'den_desktop.operator.list_local_git_snapshots',
    requestSchema: 'den_desktop.operator.list_local_git_snapshots.request',
    responseSchema: 'den_desktop.operator.list_local_git_snapshots.response',
  },
  listLocalSessionSnapshots: {
    command: 'den_desktop.operator.list_local_session_snapshots',
    requestSchema: 'den_desktop.operator.list_local_session_snapshots.request',
    responseSchema: 'den_desktop.operator.list_local_session_snapshots.response',
  },
  getLatestDiffSnapshot: {
    command: 'den_desktop.operator.get_latest_diff_snapshot',
    requestSchema: 'den_desktop.operator.get_latest_diff_snapshot.request',
    responseSchema: 'den_desktop.operator.get_latest_diff_snapshot.response',
  },
};

export const sidecarEvents: Record<string, BridgeEventSpec<JsonValue>> = {
  operatorStatus: {
    event: 'den://operator-status',
    payloadSchema: 'den://operator-status.payload',
  },
  gitSnapshots: {
    event: 'den://git-snapshot-updated',
    payloadSchema: 'den://git-snapshot-updated.payload',
  },
  sessionSnapshots: {
    event: 'den://session-snapshot-updated',
    payloadSchema: 'den://session-snapshot-updated.payload',
  },
};

export type SidecarBridgeClient = CheckedBridgeClient<typeof sidecarCommands, typeof sidecarEvents>;

export function createSidecarBridgeFacade(client: SidecarBridgeClient) {
  const facade = createBridgeCommandFacade(client);
  return {
    getHealth: async (): Promise<SidecarHealthResponse> => facade.getHealth({}) as unknown as SidecarHealthResponse,
    getCapabilities: async (): Promise<SidecarCapabilitiesResponse> => facade.getCapabilities({}) as unknown as SidecarCapabilitiesResponse,
    getOperatorStatus: async <T>(): Promise<T> => facade.getOperatorStatus({}) as Promise<T>,
    getSettings: async <T>(): Promise<T> => facade.getSettings({}) as Promise<T>,
    saveOperatorSettings: async <TRequest, TResponse>(request: TRequest): Promise<TResponse> =>
      facade.saveOperatorSettings(request as JsonValue) as Promise<TResponse>,
    getAppearanceSettings: async <T>(): Promise<T> => facade.getAppearanceSettings({}) as Promise<T>,
    saveAppearanceSettings: async <TRequest, TResponse>(request: TRequest): Promise<TResponse> =>
      facade.saveAppearanceSettings(request as JsonValue) as Promise<TResponse>,
    refreshNow: async (): Promise<void> => { await facade.refreshNow({}); },
    listLocalSnapshots: async <T>(): Promise<T> => facade.listLocalSnapshots({}) as Promise<T>,
    listLocalSessionSnapshots: async <T>(): Promise<T> => facade.listLocalSessionSnapshots({}) as Promise<T>,
    getLatestDiffSnapshot: async <TRequest, TResponse>(request: TRequest): Promise<TResponse> =>
      facade.getLatestDiffSnapshot(request as JsonValue) as Promise<TResponse>,
    consoleListCommands: async <T>(): Promise<T> => facade.consoleListCommands({}) as Promise<T>,
    consoleRunCommand: async <TRequest, TResponse>(request: TRequest): Promise<TResponse> =>
      facade.consoleRunCommand(request as JsonValue) as Promise<TResponse>,
    assertOperatorStatusEvent(frame: BridgeEventFrame): void {
      client.assertEvent('operatorStatus', frame);
    },
    assertGitSnapshotsEvent(frame: BridgeEventFrame): void {
      client.assertEvent('gitSnapshots', frame);
    },
    assertSessionSnapshotsEvent(frame: BridgeEventFrame): void {
      client.assertEvent('sessionSnapshots', frame);
    },
  };
}

export function parseReadySentinelLine(line: string): SidecarReadySentinel | null {
  if (!line.startsWith(DEN_DESKTOP_READY_PREFIX)) {
    return null;
  }

  const parsed = JSON.parse(line.slice(DEN_DESKTOP_READY_PREFIX.length)) as unknown;
  return assertReadySentinel(parsed);
}

export function assertReadySentinel(value: unknown): SidecarReadySentinel {
  const sentinel = expectRecord(value, 'sidecar ready sentinel');
  const result: SidecarReadySentinel = {
    port: expectInteger(sentinel.port, 'sentinel.port'),
    endpoint_path: expectString(sentinel.endpoint_path, 'sentinel.endpoint_path'),
    protocol_version: expectString(sentinel.protocol_version, 'sentinel.protocol_version'),
    schema_version: expectString(sentinel.schema_version, 'sentinel.schema_version'),
    schema_bundle_id: expectString(sentinel.schema_bundle_id, 'sentinel.schema_bundle_id'),
    app_id: expectString(sentinel.app_id, 'sentinel.app_id'),
    app_version: expectString(sentinel.app_version, 'sentinel.app_version'),
  };

  assertProtocolCompatibility(result);
  return result;
}

export function assertProtocolCompatibility(sentinel: SidecarReadySentinel, bundle?: BridgeSchemaBundle): void {
  if (sentinel.protocol_version !== DEN_DESKTOP_PROTOCOL_VERSION) {
    throw new Error(`Unsupported Den Desktop sidecar protocol '${sentinel.protocol_version}'.`);
  }

  if (sentinel.schema_version !== DEN_DESKTOP_SCHEMA_VERSION) {
    throw new Error(`Unsupported Den Desktop sidecar schema '${sentinel.schema_version}'.`);
  }

  if (sentinel.schema_bundle_id !== DEN_DESKTOP_SCHEMA_BUNDLE_ID) {
    throw new Error(`Unsupported Den Desktop sidecar schema bundle '${sentinel.schema_bundle_id}'.`);
  }

  if (bundle) {
    if (bundle.protocol_version !== sentinel.protocol_version) {
      throw new Error(`Sidecar protocol '${sentinel.protocol_version}' does not match bundled client '${bundle.protocol_version}'.`);
    }

    if (bundle.schema_version !== sentinel.schema_version) {
      throw new Error(`Sidecar schema '${sentinel.schema_version}' does not match bundled client '${bundle.schema_version}'.`);
    }

    if (bundle.bundle_id !== sentinel.schema_bundle_id) {
      throw new Error(`Sidecar bundle '${sentinel.schema_bundle_id}' does not match bundled client '${bundle.bundle_id}'.`);
    }
  }
}

function expectRecord(value: unknown, name: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error(`${name} must be an object.`);
  }

  return value as Record<string, unknown>;
}

function expectString(value: unknown, name: string): string {
  if (typeof value !== 'string') {
    throw new Error(`${name} must be a string.`);
  }

  return value;
}

function expectInteger(value: unknown, name: string): number {
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    throw new Error(`${name} must be an integer.`);
  }

  return value;
}
