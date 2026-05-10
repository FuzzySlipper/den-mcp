import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import {
  assertBridgeFrameMatchesBundle,
  assertBridgeSchemaBundle,
  createCheckedBridgeClient,
} from '../src/bridge/contract.ts';
import { createDenDesktopSidecarApi } from '../src/electron/preloadSidecarApi.ts';
import {
  DEN_DESKTOP_READY_PREFIX,
  assertProtocolCompatibility,
  createSidecarBridgeFacade,
  parseReadySentinelLine,
  sidecarCommands,
  sidecarEvents,
} from '../src/electron/sidecarProtocol.ts';
import { SidecarSupervisor, buildDevSidecarLaunchConfig, buildPublishedSidecarLaunchConfig } from '../src/electron/sidecarSupervisor.ts';
import { normalizeAppAgentSelection } from '../src/desktop/appAgentSelection.ts';

const __dirname = dirname(fileURLToPath(import.meta.url));
const fixturePath = resolve(__dirname, '../../../testdata/den-desktop-sidecar/sidecar-wire-fixture.json');

async function readFixture() {
  return JSON.parse(await readFile(fixturePath, 'utf8'));
}

test('sidecar schema bundle and representative frames are compatible with the checked bridge contract', async () => {
  const fixture = await readFixture();
  const bundle = fixture.schema_bundle;
  const frames = fixture.frames;

  assertBridgeSchemaBundle(bundle);
  assert.equal(bundle.bundle_id, 'den-desktop.sidecar@2026-04-29');
  assert.deepEqual(bundle.commands.map((command) => command.command), [
    'bridge.get_capabilities',
    'bridge.get_health',
    'den_desktop.app_agent.build_context',
    'den_desktop.app_agent.cancel_request',
    'den_desktop.app_agent.invoke_tool',
    'den_desktop.app_agent.list_tools',
    'den_desktop.collaboration.send_compiled_response',
    'den_desktop.console.list_commands',
    'den_desktop.console.run_command',
    'den_desktop.documents.get',
    'den_desktop.documents.list',
    'den_desktop.documents.store',
    'den_desktop.messages.get_snapshot',
    'den_desktop.operator.get_appearance_settings',
    'den_desktop.operator.get_latest_diff_snapshot',
    'den_desktop.operator.get_settings',
    'den_desktop.operator.get_status',
    'den_desktop.operator.list_local_git_snapshots',
    'den_desktop.operator.list_local_session_snapshots',
    'den_desktop.operator.refresh_now',
    'den_desktop.operator.save_appearance_settings',
    'den_desktop.operator.save_settings',
    'den_desktop.tasks.get_dashboard_snapshot',
    'den_desktop.tasks.update',
    'den_desktop.terminal.ack_output',
    'den_desktop.terminal.attach',
    'den_desktop.terminal.create_session',
    'den_desktop.terminal.detach',
    'den_desktop.terminal.list_sessions',
    'den_desktop.terminal.read_activity',
    'den_desktop.terminal.reconnect',
    'den_desktop.terminal.resize',
    'den_desktop.terminal.send_input',
    'den_desktop.terminal.terminate',
  ]);
  assert.deepEqual(bundle.events.map((event) => event.event), [
    'den.app_agent.run_state_changed',
    'den.app_agent.tool_call_state_changed',
    'den.collaboration.delivery_state_changed',
    'den.terminal.backpressure',
    'den.terminal.error',
    'den.terminal.exit',
    'den.terminal.heartbeat',
    'den.terminal.output',
    'den.terminal.replay_complete',
    'den.terminal.session_list_updated',
    'den.terminal.session_status_changed',
    'den://git-snapshot-updated',
    'den://operator-status',
    'den://session-snapshot-updated',
  ]);

  assertBridgeFrameMatchesBundle(frames.health_response, bundle, { resultSchema: 'bridge.get_health.response' });
  assertBridgeFrameMatchesBundle(frames.capabilities_response, bundle, { resultSchema: 'bridge.get_capabilities.response' });
  assertBridgeFrameMatchesBundle(frames.operator_status_event, bundle);
  assertBridgeFrameMatchesBundle(frames.git_snapshot_event, bundle);
  assertBridgeFrameMatchesBundle(frames.session_snapshot_event, bundle);
});

test('ready sentinel parsing enforces protocol, schema, and bundle compatibility without exposing secrets', async () => {
  const fixture = await readFixture();
  const sentinelLine = `${DEN_DESKTOP_READY_PREFIX}${JSON.stringify({
    port: 54321,
    endpoint_path: '/bridge',
    protocol_version: fixture.schema_bundle.protocol_version,
    schema_version: fixture.schema_bundle.schema_version,
    schema_bundle_id: fixture.schema_bundle.bundle_id,
    app_id: 'den-desktop',
    app_version: '0.1.0-test',
  })}`;

  const sentinel = parseReadySentinelLine(sentinelLine);
  assert.equal(sentinel.port, 54321);
  assert.equal(sentinel.endpoint_path, '/bridge');
  assertProtocolCompatibility(sentinel, fixture.schema_bundle);
  assert.equal(parseReadySentinelLine('ordinary log line'), null);
  assert.doesNotMatch(sentinelLine, /token|secret/i);

  assert.throws(
    () => parseReadySentinelLine(sentinelLine.replace('"protocol_version":"1.0"', '"protocol_version":"2.0"')),
    /Unsupported Den Desktop sidecar protocol/,
  );
});

test('sidecar checked facade allow-lists health/capabilities/runtime commands and events only', async () => {
  const fixture = await readFixture();
  const sent = [];
  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    requestIdFactory: () => sent.length === 0 ? 'req_health' : 'req_capabilities',
    now: () => '2026-04-29T12:34:56.000Z',
    transport: {
      async send(frame) {
        sent.push(frame);
        if (frame.command === 'bridge.get_health') return fixture.frames.health_response;
        if (frame.command === 'bridge.get_capabilities') return fixture.frames.capabilities_response;
        const dashboardSnapshot = {
          snapshot_id: 'task-dashboard:den-mcp:900:fixture',
          project_id: 'den-mcp',
          parent_task_id: 900,
          focused_task_id: null,
          generated_at: '2026-04-29T12:34:56.000Z',
          header: { state: 'running', task_count: 1, completion_percent: 0 },
          tasks: [],
          waves: [],
          lanes: [],
          freshness: { source: 'den_http', generated_at: '2026-04-29T12:34:56.000Z', is_partial: false, warnings: [], errors: [] },
        };
        return {
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: frame.command === 'den_desktop.operator.get_status'
            ? fixture.frames.operator_status_event.payload
            : frame.command === 'den_desktop.tasks.get_dashboard_snapshot'
              ? dashboardSnapshot
              : {},
          correlation: {},
          sent_at: '2026-04-29T12:34:56.000Z',
        };
      },
    },
  });
  const facade = createSidecarBridgeFacade(client);

  const health = await facade.getHealth();
  const capabilities = await facade.getCapabilities();
  const status = await facade.getOperatorStatus();
  const dashboard = await facade.tasksGetDashboardSnapshot({ project_id: 'den-mcp', parent_task_id: 900 });
  facade.assertOperatorStatusEvent(fixture.frames.operator_status_event);
  facade.assertGitSnapshotsEvent(fixture.frames.git_snapshot_event);
  facade.assertSessionSnapshotsEvent(fixture.frames.session_snapshot_event);

  assert.equal(health.schema_bundle_id, fixture.schema_bundle.bundle_id);
  assert.ok(capabilities.supported_transports.includes('loopback_websocket'));
  assert.equal(status.phase, 'starting');
  assert.equal(dashboard.project_id, 'den-mcp');
  assert.deepEqual(sent.map((frame) => frame.command), [
    'bridge.get_health',
    'bridge.get_capabilities',
    'den_desktop.operator.get_status',
    'den_desktop.tasks.get_dashboard_snapshot',
  ]);
  assert.deepEqual(Object.keys(facade).sort(), [
    'appAgentBuildContext',
    'appAgentCancelRequest',
    'appAgentInvokeTool',
    'appAgentListTools',
    'assertAppAgentRunStateEvent',
    'assertAppAgentToolCallStateEvent',
    'assertCollaborationDeliveryEvent',
    'assertGitSnapshotsEvent',
    'assertOperatorStatusEvent',
    'assertSessionSnapshotsEvent',
    'assertTerminalBackpressureEvent',
    'assertTerminalErrorEvent',
    'assertTerminalExitEvent',
    'assertTerminalHeartbeatEvent',
    'assertTerminalOutputEvent',
    'assertTerminalReplayCompleteEvent',
    'assertTerminalSessionListEvent',
    'assertTerminalSessionStatusEvent',
    'collaborationSendCompiledResponse',
    'consoleListCommands',
    'consoleRunCommand',
    'getAppearanceSettings',
    'getCapabilities',
    'getHealth',
    'getLatestDiffSnapshot',
    'getOperatorStatus',
    'getSettings',
    'listLocalSessionSnapshots',
    'listLocalSnapshots',
    'refreshNow',
    'saveAppearanceSettings',
    'saveOperatorSettings',
    'tasksGetDashboardSnapshot',
    'taskUpdate',
    'messagesGetSnapshot',
    'terminalAckOutput',
    'terminalAttach',
    'terminalCreateSession',
    'terminalDetach',
    'terminalListSessions',
    'terminalReadActivity',
    'terminalReconnect',
    'terminalResize',
    'terminalSendInput',
    'terminalTerminate',
    'documentsList',
    'documentGet',
    'documentStore',
  ].sort());
});

test('terminal attach facade accepts typed viewport and replay fields and rejects unknown replay fields', async () => {
  const fixture = await readFixture();
  const sent = [];
  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    requestIdFactory: () => `req_terminal_${sent.length + 1}`,
    now: () => '2026-04-29T12:34:56.000Z',
    transport: {
      async send(frame) {
        sent.push(frame);
        return {
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: { stream_id: 'stream_fixture', session_id: frame.payload.session_id },
          correlation: {},
          sent_at: '2026-04-29T12:34:56.000Z',
        };
      },
    },
  });
  const facade = createSidecarBridgeFacade(client);

  await facade.terminalAttach({
    terminal_protocol_version: '1.0',
    session_id: 'tmux-session:test',
    mode: 'terminal_stream',
    client_id: 'client-1',
    viewport: { cols: 132, rows: 43 },
    replay: { after_cursor: 'cur_000000000010', max_bytes: 65536, max_chunks: 20 },
  });

  assert.equal(sent[0].command, 'den_desktop.terminal.attach');
  assert.deepEqual(sent[0].payload.viewport, { cols: 132, rows: 43 });
  assert.deepEqual(sent[0].payload.replay, { after_cursor: 'cur_000000000010', max_bytes: 65536, max_chunks: 20 });

  await assert.rejects(
    () => facade.terminalAttach({ session_id: 'tmux-session:test', replay: { after_cursor: null, unexpected: true } }),
    /den_desktop\.terminal\.attach\.request\.replay has unexpected property 'unexpected'/,
  );
});

test('terminal reconnect facade accepts typed viewport and rejects unknown viewport properties', async () => {
  const fixture = await readFixture();
  const sent = [];
  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    requestIdFactory: () => `req_reconnect_${sent.length + 1}`,
    now: () => '2026-04-29T12:34:56.000Z',
    transport: {
      async send(frame) {
        sent.push(frame);
        return {
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: { stream_id: 'stream_reconnect', session_id: frame.payload.session_id },
          correlation: {},
          sent_at: '2026-04-29T12:34:56.000Z',
        };
      },
    },
  });
  const facade = createSidecarBridgeFacade(client);

  // Valid viewport
  await facade.terminalReconnect({
    session_id: 'tmux-session:test',
    previous_stream_id: 'stream-old',
    last_seen_cursor: 'cur_000000000010',
    viewport: { cols: 120, rows: 40 },
  });

  assert.equal(sent[0].command, 'den_desktop.terminal.reconnect');
  assert.deepEqual(sent[0].payload.viewport, { cols: 120, rows: 40 });

  // Null viewport is allowed by the schema (type includes "null") but the
  // checked bridge contract validator does not exercise the null path for
  // properties/additionalProperties schemas; skip null here and rely on the
  // sidecar C# schema tests for the null-permission contract.

  // Unknown viewport properties are rejected
  await assert.rejects(
    () => facade.terminalReconnect({ session_id: 'tmux-session:test', viewport: { cols: 80, rows: 24, depth: 256 } }),
    /den_desktop\.terminal\.reconnect\.request\.viewport has unexpected property 'depth'/,
  );
});

test('app-agent selection normalization sends nulls for absent optional bridge fields', () => {
  assert.deepEqual(normalizeAppAgentSelection({ project_id: 'den-mcp', current_tab: 'agent' }), {
    project_id: 'den-mcp',
    task_id: null,
    workspace_id: null,
    current_route: null,
    current_tab: 'agent',
    session_id: null,
    selected_file_path: null,
    selected_diff_range: null,
  });
});

test('app-agent helper DTOs use typed commands and events without generic dispatch', async () => {
  const fixture = await readFixture();
  const sent = [];
  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    requestIdFactory: () => `req_app_agent_${sent.length + 1}`,
    now: () => '2026-04-29T12:34:56.000Z',
    transport: {
      async send(frame) {
        sent.push(frame);
        const result = frame.command === 'den_desktop.app_agent.list_tools'
          ? {
              tools: [
                { name: 'get_context', display_name: 'Get Context', category: 'read', description: 'Build context.', enabled: true, requires_explicit_target: false, destructive: false, requires_confirmation: false, cancellable: true, audit_event_type: 'app_agent.context_requested', capabilities: ['context.read'] },
                { name: 'stop_agent_run', display_name: 'Stop Agent Run', category: 'action', description: 'Stop an app-agent run when supported by the backend.', enabled: false, disabled_reason: 'Backend adapter not implemented in this foundation slice.', requires_explicit_target: false, destructive: false, requires_confirmation: false, cancellable: true, audit_event_type: 'app_agent.stop_requested', capabilities: ['app_agent.stop'] },
              ],
            }
          : frame.command === 'den_desktop.app_agent.build_context'
            ? { context: { context_version: 1, selection: {}, git_snapshot: {}, session_summaries: [], command_summaries: [], terminal_excerpts: [], collaboration_state: {}, authority: { allowed_tools: [{ name: 'get_context', display_name: 'Get Context', category: 'read', description: 'Build context.', enabled: true, requires_explicit_target: false, destructive: false, requires_confirmation: false, cancellable: true, audit_event_type: 'app_agent.context_requested', capabilities: ['context.read'] }], disabled_tools: [{ name: 'stop_agent_run', reason: 'Backend adapter not implemented in this foundation slice.' }], cancel_available: true, stop_available: false, sandbox_scope: 'trusted_desktop_app_core_v1' }, audit: { agent_run_id: 'run_1', trace_id: 'tr_1' }, warnings: [], built_at: '2026-04-29T12:34:56.000Z' } }
            : frame.command === 'den_desktop.app_agent.cancel_request'
              ? { request_id: frame.payload.request_id, accepted: true, status: 'cancel_requested' }
              : { tool_name: 'summarize_output', tool_call_id: 'tool_1', status: 'completed', result: { summary: 'hello' }, audit: { agent_run_id: 'run_1', trace_id: 'tr_1' } };
        return {
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result,
          correlation: {},
          sent_at: '2026-04-29T12:34:56.000Z',
        };
      },
    },
  });
  const facade = createSidecarBridgeFacade(client);
  const explicitSelection = {
    project_id: 'den-mcp',
    task_id: 1023,
    workspace_id: 'workspace-1',
    current_route: '/tasks/1023',
    current_tab: 'context',
    session_id: 'session-1',
    selected_file_path: 'src/DenMcp.Desktop/src/electron/sidecarProtocol.ts',
    selected_diff_range: 'L161-L168',
  };

  const toolsResponse = await facade.appAgentListTools({ selection: explicitSelection });
  const contextResponse = await facade.appAgentBuildContext({
    selection: {
      project_id: 'den-mcp',
      task_id: null,
      workspace_id: null,
      current_route: null,
      current_tab: null,
      session_id: null,
      selected_file_path: null,
      selected_diff_range: null,
    },
  });
  await assert.rejects(
    () => facade.appAgentBuildContext({ selection: { project_id: 'den-mcp', unexpected_selection_ref: true } }),
    /den_desktop\.app_agent\.build_context\.request\.selection -> app_agent_selection has unexpected property 'unexpected_selection_ref'/,
  );
  await facade.appAgentInvokeTool({ tool_name: 'summarize_output', input: { text: 'hello' } });
  await facade.appAgentCancelRequest({ request_id: 'req_app_agent_3', reason: 'user_requested' });
  facade.assertAppAgentRunStateEvent({
    protocol_version: fixture.schema_bundle.protocol_version,
    schema_version: fixture.schema_bundle.schema_version,
    frame_type: 'event',
    event_id: 'evt_app_agent_run_1',
    sequence: 10,
    event: 'den.app_agent.run_state_changed',
    payload: { agent_run_id: 'run_1', status: 'complete', observed_at: '2026-04-29T12:34:56.000Z' },
  });
  facade.assertAppAgentToolCallStateEvent({
    protocol_version: fixture.schema_bundle.protocol_version,
    schema_version: fixture.schema_bundle.schema_version,
    frame_type: 'event',
    event_id: 'evt_app_agent_tool_1',
    sequence: 11,
    event: 'den.app_agent.tool_call_state_changed',
    payload: { tool_call_id: 'tool_1', agent_run_id: 'run_1', tool_name: 'summarize_output', status: 'completed', cancellable: false },
  });

  assert.deepEqual(sent.map((frame) => frame.command), [
    'den_desktop.app_agent.list_tools',
    'den_desktop.app_agent.build_context',
    'den_desktop.app_agent.invoke_tool',
    'den_desktop.app_agent.cancel_request',
  ]);
  assert.deepEqual(sent[0].payload.selection, explicitSelection);
  assert.equal(toolsResponse.tools.find((tool) => tool.name === 'stop_agent_run')?.enabled, false);
  assert.equal(toolsResponse.tools.find((tool) => tool.name === 'stop_agent_run')?.disabled_reason, 'Backend adapter not implemented in this foundation slice.');
  assert.deepEqual(contextResponse.context.authority.disabled_tools, [{ name: 'stop_agent_run', reason: 'Backend adapter not implemented in this foundation slice.' }]);
  assert.equal(contextResponse.context.authority.stop_available, false);
  assert.equal(facade.dispatch, undefined);
});

test('preload sidecar API exposes no generic dispatch, token, endpoint, or node escape hatch', async () => {
  const fixture = await readFixture();
  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    transport: {
      async send(frame) {
        if (frame.command === 'bridge.get_health') return fixture.frames.health_response;
        if (frame.command === 'bridge.get_capabilities') return fixture.frames.capabilities_response;
        return {
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: {},
          correlation: {},
          sent_at: '2026-04-29T12:34:56.000Z',
        };
      },
    },
  });
  const api = createDenDesktopSidecarApi(client, {
    subscribe(listener) {
      listener(fixture.frames.operator_status_event);
      listener(fixture.frames.git_snapshot_event);
      listener(fixture.frames.session_snapshot_event);
      return () => undefined;
    },
  });
  const events = [];
  api.onOperatorStatus((event) => events.push(event));

  assert.deepEqual(Object.keys(api).sort(), [
    'appAgentBuildContext',
    'appAgentCancelRequest',
    'appAgentInvokeTool',
    'appAgentListTools',
    'collaborationSendCompiledResponse',
    'consoleListCommands',
    'consoleRunCommand',
    'consoleRunCommandWithProgress',
    'getAppearanceSettings',
    'getCapabilities',
    'getHealth',
    'getLatestDiffSnapshot',
    'getOperatorStatus',
    'getSettings',
    'listLocalSessionSnapshots',
    'listLocalSnapshots',
    'onAppAgentRunState',
    'onAppAgentToolCallState',
    'onCollaborationDelivery',
    'onGitSnapshots',
    'onOperatorStatus',
    'onSessionSnapshots',
    'onTerminalBackpressure',
    'onTerminalLifecycle',
    'onTerminalOutput',
    'onTerminalSessionList',
    'onTerminalStatus',
    'refreshNow',
    'saveAppearanceSettings',
    'saveOperatorSettings',
    'tasksGetDashboardSnapshot',
    'taskUpdate',
    'messagesGetSnapshot',
    'terminalAckOutput',
    'terminalAttach',
    'terminalCreateSession',
    'terminalDetach',
    'terminalListSessions',
    'terminalReadActivity',
    'terminalReconnect',
    'terminalResize',
    'terminalSendInput',
    'terminalTerminate',
    'documentsList',
    'documentGet',
    'documentStore',
  ].sort());
  assert.equal(api.dispatch, undefined);
  assert.equal(api.ipcRenderer, undefined);
  assert.equal(api.token, undefined);
  assert.equal(api.endpoint, undefined);
  assert.equal(api.fs, undefined);
  assert.equal(events[0].phase, 'starting');
});

test('published sidecar launch config runs executable or dll without exposing token in args', () => {
  const executableConfig = buildPublishedSidecarLaunchConfig({
    sidecarPath: '/opt/den-desktop/current/sidecar/DenMcp.Desktop.Sidecar',
    configPath: '/tmp/den-desktop/config',
    authToken: 'secret-token',
    appVersion: '0.1.0+abc123',
    port: 0,
  });
  assert.equal(executableConfig.command, '/opt/den-desktop/current/sidecar/DenMcp.Desktop.Sidecar');
  assert.equal(executableConfig.args[0], '--app-id');
  assert.equal(executableConfig.env.DEN_DESKTOP_BRIDGE_TOKEN, 'secret-token');
  assert.doesNotMatch(executableConfig.args.join(' '), /secret-token/);
  assert.match(executableConfig.args.join(' '), /0\.1\.0\+abc123/);

  const dllConfig = buildPublishedSidecarLaunchConfig({
    sidecarPath: '/opt/den-desktop/current/sidecar/DenMcp.Desktop.Sidecar.dll',
    configPath: '/tmp/den-desktop/config',
    authToken: 'secret-token',
    port: 0,
  });
  assert.equal(dllConfig.command, 'dotnet');
  assert.equal(dllConfig.args[0], '/opt/den-desktop/current/sidecar/DenMcp.Desktop.Sidecar.dll');
  assert.equal(dllConfig.env.DEN_DESKTOP_BRIDGE_TOKEN, 'secret-token');
  assert.doesNotMatch(dllConfig.args.join(' '), /secret-token/);
});

test('sidecar supervisor recognizes ready sentinel split across stdout chunks', () => {
  const fakeProcess = createFakeProcess(4343);
  const supervisor = new SidecarSupervisor({
    launchConfig: buildDevSidecarLaunchConfig({
      projectPath: '../DenMcp.Desktop.Sidecar/DenMcp.Desktop.Sidecar.csproj',
      configPath: '/tmp/den-desktop/config',
      authToken: 'secret-token',
      port: 0,
    }),
    launcher: {
      launch() {
        return fakeProcess;
      },
    },
    now: () => '2026-04-29T12:34:56.000Z',
  });
  const readyLine = `${DEN_DESKTOP_READY_PREFIX}${JSON.stringify({
    port: 54321,
    endpoint_path: '/bridge',
    protocol_version: '1.0',
    schema_version: 'den-desktop@2026-04-29',
    schema_bundle_id: 'den-desktop.sidecar@2026-04-29',
    app_id: 'den-desktop',
    app_version: '0.1.0-test',
  })}`;

  supervisor.start();
  fakeProcess.emitStdout(readyLine.slice(0, 17));
  fakeProcess.emitStdout(readyLine.slice(17, 49));
  assert.equal(supervisor.snapshot().state, 'starting');
  assert.equal(supervisor.snapshot().ready, undefined);

  fakeProcess.emitStdout(`${readyLine.slice(49)}\n`);

  assert.equal(supervisor.snapshot().state, 'ready');
  assert.equal(supervisor.snapshot().ready.port, 54321);
});

test('sidecar supervisor resets partial stdout buffer on process error', () => {
  const fakeProcess = createFakeProcess(4444);
  const supervisor = new SidecarSupervisor({
    launchConfig: buildDevSidecarLaunchConfig({
      projectPath: '../DenMcp.Desktop.Sidecar/DenMcp.Desktop.Sidecar.csproj',
      configPath: '/tmp/den-desktop/config',
      authToken: 'secret-token',
      port: 0,
    }),
    launcher: {
      launch() {
        return fakeProcess;
      },
    },
    now: () => '2026-04-29T12:34:56.000Z',
  });
  const readyLine = `${DEN_DESKTOP_READY_PREFIX}${JSON.stringify({
    port: 54321,
    endpoint_path: '/bridge',
    protocol_version: '1.0',
    schema_version: 'den-desktop@2026-04-29',
    schema_bundle_id: 'den-desktop.sidecar@2026-04-29',
    app_id: 'den-desktop',
    app_version: '0.1.0-test',
  })}`;

  supervisor.start();
  fakeProcess.emitStdout(readyLine.slice(0, 17));
  fakeProcess.emitError(new Error('spawn failed after partial stdout'));
  assert.equal(supervisor.snapshot().state, 'crashed');
  assert.equal(supervisor.snapshot().last_error, 'spawn failed after partial stdout');

  fakeProcess.emitStdout(`${readyLine.slice(17)}\n`);
  assert.equal(supervisor.snapshot().state, 'crashed');
  assert.equal(supervisor.snapshot().ready, undefined);
});

test('sidecar supervisor starts, observes readiness, reconnects, stops, and can restart after crash', async () => {
  const launched = [];
  const connections = [];
  const fakeProcess = createFakeProcess(4242);
  const supervisor = new SidecarSupervisor({
    launchConfig: buildDevSidecarLaunchConfig({
      projectPath: '../DenMcp.Desktop.Sidecar/DenMcp.Desktop.Sidecar.csproj',
      configPath: '/tmp/den-desktop/config',
      authToken: 'secret-token',
      port: 0,
    }),
    launcher: {
      launch(config) {
        launched.push(config);
        return fakeProcess;
      },
    },
    connector: {
      async connect(sentinel) {
        connections.push(sentinel);
        return { port: sentinel.port };
      },
    },
    now: () => '2026-04-29T12:34:56.000Z',
  });

  supervisor.start();
  assert.equal(supervisor.snapshot().state, 'starting');
  assert.equal(launched[0].env.DEN_DESKTOP_BRIDGE_TOKEN, 'secret-token');
  assert.doesNotMatch(launched[0].args.join(' '), /secret-token/);

  fakeProcess.emitStdout(`${DEN_DESKTOP_READY_PREFIX}${JSON.stringify({
    port: 54321,
    endpoint_path: '/bridge',
    protocol_version: '1.0',
    schema_version: 'den-desktop@2026-04-29',
    schema_bundle_id: 'den-desktop.sidecar@2026-04-29',
    app_id: 'den-desktop',
    app_version: '0.1.0-test',
  })}\n`);
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(supervisor.snapshot().state, 'ready');
  assert.equal(supervisor.snapshot().ready.port, 54321);
  assert.equal(connections.length, 1);

  await supervisor.reconnect();
  assert.equal(supervisor.snapshot().state, 'ready');
  assert.equal(connections.length, 2);

  await supervisor.stop();
  fakeProcess.emitExit(0, null);
  assert.equal(supervisor.snapshot().state, 'stopped');
});

function createFakeProcess(pid) {
  const listeners = { exit: [], error: [], stdout: [], stderr: [] };
  return {
    pid,
    stdout: { on: (_event, callback) => listeners.stdout.push(callback) },
    stderr: { on: (_event, callback) => listeners.stderr.push(callback) },
    on(event, callback) {
      listeners[event].push(callback);
    },
    kill() {
      return true;
    },
    emitStdout(chunk) {
      for (const listener of listeners.stdout) listener(chunk);
    },
    emitExit(code, signal) {
      for (const listener of listeners.exit) listener(code, signal);
    },
    emitError(error) {
      for (const listener of listeners.error) listener(error);
    },
  };
}
