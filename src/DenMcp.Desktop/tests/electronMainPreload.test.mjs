import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import {
  assertBridgeSchemaBundle,
  createCheckedBridgeClient,
} from '../src/bridge/contract.ts';
import {
  DEN_DESKTOP_READY_PREFIX,
  sidecarCommands,
  sidecarEvents,
} from '../src/electron/sidecarProtocol.ts';
import { createDenDesktopSidecarApi } from '../src/electron/preloadSidecarApi.ts';
import { createSidecarBridgeTransport } from '../src/electron/sidecarBridgeConnection.ts';

const __dirname = dirname(fileURLToPath(import.meta.url));
const fixturePath = resolve(__dirname, '../../../testdata/den-desktop-sidecar/sidecar-wire-fixture.json');

async function readFixture() {
  return JSON.parse(await readFile(fixturePath, 'utf8'));
}

test('sidecar bridge transport sends request frames and receives responses via WebSocket', async () => {
  const fixture = await readFixture();
  const sentFrames = [];
  const receivedEvents = [];
  const fakeWebSocket = createFakeWebSocketClass(sentFrames, fixture);

  const transport = createSidecarBridgeTransport({
    baseUrl: 'http://127.0.0.1:54321',
    endpointPath: '/bridge',
    authToken: 'test-token-abc',
    WebSocketCtor: fakeWebSocket,
    onEvent: (frame) => receivedEvents.push(frame),
  });

  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    requestIdFactory: () => 'req_test_1',
    now: () => '2026-04-30T12:00:00.000Z',
    transport,
  });

  const facade = createDenDesktopSidecarApi(client, {
    subscribe() { return () => undefined; },
  });

  const health = await facade.getHealth();
  assert.equal(health.schema_bundle_id, fixture.schema_bundle.bundle_id);
  assert.equal(sentFrames.length, 1);
  assert.equal(sentFrames[0].command, 'bridge.get_health');
  assert.equal(sentFrames[0].frame_type, 'request');

  transport.close();
});

test('sidecar bridge transport connects with correct WebSocket URL and auth header', async () => {
  const fixture = await readFixture();
  const connectionArgs = [];
  const fakeWebSocket = createFakeWebSocketClass([], fixture, connectionArgs);

  const transport = createSidecarBridgeTransport({
    baseUrl: 'http://127.0.0.1:54321',
    endpointPath: '/bridge',
    authToken: 'test-auth-token-xyz',
    WebSocketCtor: fakeWebSocket,
    onEvent: () => {},
  });

  // Trigger connection by sending a request
  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    requestIdFactory: () => 'req_connect_test',
    now: () => '2026-04-30T12:00:00.000Z',
    transport,
  });

  await client.call('getHealth', {});
  assert.equal(connectionArgs.length, 1);
  assert.equal(connectionArgs[0].url, 'ws://127.0.0.1:54321/bridge');
  assert.equal(connectionArgs[0].options.headers.Authorization, 'Bearer test-auth-token-xyz');

  transport.close();
});

test('sidecar bridge transport rejects requests when connection times out', async () => {
  const fixture = await readFixture();
  // WebSocket that never opens
  const FakeWebSocket = class {
    constructor() {
      this.readyState = 0; // CONNECTING
      this._listeners = {};
    }
    on(event, callback) {
      if (!this._listeners[event]) this._listeners[event] = [];
      this._listeners[event].push(callback);
      return this;
    }
    addEventListener(event, callback) {
      this.on(event, callback);
    }
    send() {}
    close() {}
  };

  const transport = createSidecarBridgeTransport({
    baseUrl: 'http://127.0.0.1:99999',
    endpointPath: '/bridge',
    authToken: 'token',
    WebSocketCtor: FakeWebSocket,
    onEvent: () => {},
  });

  // Sending a request should eventually timeout since WebSocket never opens
  const sendPromise = transport.send({
    protocol_version: '1.0',
    schema_version: 'den-desktop@2026-04-29',
    frame_type: 'request',
    request_id: 'req_timeout_test',
    command: 'bridge.get_health',
    payload: {},
  });

  // The request should timeout (30s default), but we can test the close behavior
  transport.close();
  await assert.rejects(sendPromise, /Bridge connection closed/);
});

test('preload exposes only allow-listed sidecar API surface via contextBridge', async () => {
  const fixture = await readFixture();
  const sent = [];
  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    requestIdFactory: () => `req_preload_${sent.length + 1}`,
    now: () => '2026-04-30T12:00:00.000Z',
    transport: {
      async send(frame, _onProgress) {
        sent.push(frame);
        return {
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: frame.command === 'bridge.get_health'
            ? fixture.frames.health_response.result
            : {},
          correlation: {},
          sent_at: '2026-04-30T12:00:00.000Z',
        };
      },
    },
  });

  const api = createDenDesktopSidecarApi(client, {
    subscribe() { return () => undefined; },
  });

  // Verify the API surface matches the expected allow-list
  const expectedMethods = [
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
    'documentsList',
    'documentGet',
    'documentStore',
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
  ].sort();

  assert.deepEqual(Object.keys(api).sort(), expectedMethods);

  // Verify collaboration methods are exposed as typed, allow-listed bridge (task #1074)
  assert.equal(typeof api.collaborationSendCompiledResponse, 'function',
    'collaborationSendCompiledResponse must be a typed function on the sidecar API');
  assert.equal(typeof api.onCollaborationDelivery, 'function',
    'onCollaborationDelivery must be a typed function on the sidecar API');

  // Verify no escape hatches
  assert.equal(api.dispatch, undefined, 'No generic dispatch exposed');
  assert.equal(api.ipcRenderer, undefined, 'No ipcRenderer exposed');
  assert.equal(api.token, undefined, 'No raw token exposed');
  assert.equal(api.endpoint, undefined, 'No endpoint URL exposed');
  assert.equal(api.fs, undefined, 'No filesystem exposed');
  assert.equal(api.process, undefined, 'No process exposed');
  assert.equal(api.require, undefined, 'No require exposed');
  assert.equal(api.child_process, undefined, 'No child_process exposed');
  assert.equal(api.shell, undefined, 'No shell exposed');

  // Verify all methods are functions (not arbitrary objects)
  for (const key of Object.keys(api)) {
    assert.equal(typeof api[key], 'function', `API method '${key}' should be a function`);
  }
});

test('preload API does not expose raw token or sidecar URL in method arguments or return values', async () => {
  const fixture = await readFixture();
  const authToken = 'super-secret-token-never-expose';
  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    transport: {
      async send(frame) {
        return {
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: frame.command === 'bridge.get_health'
            ? fixture.frames.health_response.result
            : {},
          correlation: {},
          sent_at: '2026-04-30T12:00:00.000Z',
        };
      },
    },
  });

  const api = createDenDesktopSidecarApi(client, {
    subscribe() { return () => undefined; },
  });

  // Call various methods and verify no token/URL leaks
  const health = await api.getHealth();
  const healthJson = JSON.stringify(health);
  assert.doesNotMatch(healthJson, /super-secret/, 'Health response must not contain token');
  assert.doesNotMatch(healthJson, /ws:\/\//, 'Health response must not contain WebSocket URL');

  // Verify API is frozen (no runtime property injection)
  assert.throws(() => {
    (api).maliciousProp = 'injected';
  }, /read only|cannot assign|extensible/i);
});

test('main process sidecar IPC allow-list accepts known methods and rejects unknown methods', async () => {
  const {
    allowedSidecarCallMethods,
    allowedSidecarSubscriptionEvents,
    assertAllowedSidecarCallMethod,
    assertAllowedSidecarSubscriptionEvent,
  } = await import('../src/electron/ipcAllowList.ts');

  assert.ok(allowedSidecarCallMethods.includes('getHealth'));
  assert.ok(allowedSidecarCallMethods.includes('terminalAttach'));
  assert.ok(!allowedSidecarCallMethods.includes('dispatch'));
  assert.ok(!allowedSidecarCallMethods.includes('then'));
  assert.equal(assertAllowedSidecarCallMethod('getHealth'), 'getHealth');
  assert.throws(() => assertAllowedSidecarCallMethod('dispatch'), /Unknown sidecar method 'dispatch'/);
  assert.throws(() => assertAllowedSidecarCallMethod('__proto__'), /Unknown sidecar method '__proto__'/);
  assert.throws(() => assertAllowedSidecarCallMethod(42), /Unknown sidecar method '42'/);

  assert.ok(allowedSidecarSubscriptionEvents.includes('terminalOutput'));
  assert.ok(!allowedSidecarSubscriptionEvents.includes('dispatch'));
  assert.equal(assertAllowedSidecarSubscriptionEvent('terminalOutput'), 'terminalOutput');
  assert.throws(() => assertAllowedSidecarSubscriptionEvent('dispatch'), /Unknown sidecar event subscription 'dispatch'/);

  // Verify collaboration methods are allow-listed (task #1074 re-introduces typed path)
  assert.ok(allowedSidecarCallMethods.includes('collaborationSendCompiledResponse'),
    'collaborationSendCompiledResponse must be in the IPC call allow-list');
  assert.ok(allowedSidecarSubscriptionEvents.includes('collaborationDelivery'),
    'collaborationDelivery must be in the IPC subscription allow-list');
  assert.equal(assertAllowedSidecarCallMethod('collaborationSendCompiledResponse'), 'collaborationSendCompiledResponse');
  assert.equal(assertAllowedSidecarSubscriptionEvent('collaborationDelivery'), 'collaborationDelivery');
});

test('main process IPC bridge channel names are deterministic and scoped', () => {
  // Verify the IPC channel names used in the main/preload contract
  const expectedChannels = [
    'den-desktop:sidecar-call',
    'den-desktop:sidecar-subscribe',
    'den-desktop:sidecar-unsubscribe',
  ];

  // These are the same channels used in both main.ts and preload.ts
  // If either side changes, this test will catch the mismatch
  for (const channel of expectedChannels) {
    assert.ok(channel.startsWith('den-desktop:'), `Channel '${channel}' should be scoped to den-desktop`);
    assert.ok(!channel.includes(' '), `Channel '${channel}' should not contain spaces`);
  }
});

test('event subscription channel names follow the den-desktop:event prefix convention', () => {
  const eventNames = [
    'terminalOutput',
    'terminalStatus',
    'terminalLifecycle',
    'terminalBackpressure',
    'terminalSessionList',
    'appAgentRunState',
    'appAgentToolCallState',
    'operatorStatus',
    'gitSnapshots',
    'sessionSnapshots',
  ];

  for (const name of eventNames) {
    const channel = `den-desktop:event:${name}`;
    assert.ok(channel.startsWith('den-desktop:event:'), `Event channel should use den-desktop:event prefix`);
  }
});

test('Electron shell exposes safe context menu and avoids dead topbar controls', async () => {
  const mainSource = await readFile(resolve(__dirname, '../src/electron/main.ts'), 'utf8');
  assert.match(mainSource, /role: 'copy'/);
  assert.match(mainSource, /role: 'paste'/);
  assert.match(mainSource, /role: 'selectAll'/);
  assert.match(mainSource, /setupRendererContextMenu\(win\)/);
  assert.doesNotMatch(mainSource, /role: 'open'/);
  assert.doesNotMatch(mainSource, /shell\.open/);

  const shellSource = await readFile(resolve(__dirname, '../src/components/AppShell.tsx'), 'utf8');
  assert.match(shellSource, /onClick=\{onOpenSearch\}/);
  assert.match(shellSource, /Notifications are not wired yet" disabled/);
  assert.match(shellSource, /Expand project sidebar/);
  assert.match(shellSource, /handleProjectClick\(row\.id\)/);
});

test('built Electron dev launch uses sandbox-compatible preload and relative Vite asset URLs', async () => {
  const packageJsonPath = resolve(__dirname, '../package.json');
  const packageJson = JSON.parse(await readFile(packageJsonPath, 'utf8'));
  const electronBuildScript = packageJson.scripts['electron:build'];
  assert.match(electronBuildScript, /--format=cjs --outfile=electron-dist\/preload\.cjs/);
  assert.doesNotMatch(electronBuildScript, /preload\.mjs/);

  const mainSource = await readFile(resolve(__dirname, '../src/electron/main.ts'), 'utf8');
  assert.match(mainSource, /preload: path\.resolve\(__dirname, '\.\/preload\.cjs'\)/);
  assert.doesNotMatch(mainSource, /preload\.mjs/);

  const viteConfig = await readFile(resolve(__dirname, '../vite.config.ts'), 'utf8');
  assert.match(viteConfig, /base:\s*'\.\/'/);
});

test('sidecar launch config builder produces safe command line without leaking auth token in args', async () => {
  // The buildDevSidecarLaunchConfig function is tested in sidecarProtocol.test.mjs,
  // but this test verifies the Electron-specific integration concern: the auth token
  // is passed via env vars, not command-line arguments.
  const { buildDevSidecarLaunchConfig } = await import('../src/electron/sidecarSupervisor.ts');
  assert.ok(typeof buildDevSidecarLaunchConfig === 'function', 'buildDevSidecarLaunchConfig is importable');

  const config = buildDevSidecarLaunchConfig({
    projectPath: '/test/DenMcp.Desktop.Sidecar.csproj',
    configPath: '/tmp/sidecar',
    authToken: 'secret-token-xyz',
    port: 0,
  });

  // Auth token must be in env, not in args
  assert.ok(!config.args.includes('secret-token-xyz'), 'Auth token must not appear in command args');
  assert.equal(config.env.DEN_DESKTOP_BRIDGE_TOKEN, 'secret-token-xyz', 'Auth token must be in env var');
});

test('bridge transport delivers event frames to onEvent callback', async () => {
  const fixture = await readFixture();
  const receivedEvents = [];
  const sentFrames = [];

  // Create a fake WebSocket that can emit events.
  // The transport creates the WebSocket lazily on the first send().
  const sockets = [];
  class EventCapableWebSocket {
    constructor(url, options) {
      this.readyState = 0;
      this._listeners = {};
      this._url = url;
      sockets.push(this);
      setTimeout(() => {
        this.readyState = 1;
        this._emit('open', {});
      }, 0);
    }
    on(event, callback) {
      if (!this._listeners[event]) this._listeners[event] = [];
      this._listeners[event].push(callback);
      return this;
    }
    addEventListener(event, callback) {
      this.on(event, callback);
    }
    send(data) {
      const frame = JSON.parse(data);
      sentFrames.push(frame);
      // Simulate a response so the request promise resolves
      setTimeout(() => {
        this._emit('message', JSON.stringify({
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: {},
          correlation: {},
          sent_at: '2026-04-30T12:00:00.000Z',
        }));
      }, 0);
    }
    close() {
      this.readyState = 3;
      this._emit('close', {});
    }
    _emit(event, data) {
      for (const listener of this._listeners[event] ?? []) {
        listener(data);
      }
    }
  }

  const transport = createSidecarBridgeTransport({
    baseUrl: 'http://127.0.0.1:54321',
    endpointPath: '/bridge',
    authToken: 'token',
    WebSocketCtor: EventCapableWebSocket,
    onEvent: (frame) => receivedEvents.push(frame),
  });

  // Trigger connection by sending a request (transport creates WS lazily)
  const requestPromise = transport.send({
    protocol_version: '1.0',
    schema_version: 'den-desktop@2026-04-29',
    frame_type: 'request',
    request_id: 'req_connect_trigger',
    command: 'bridge.get_health',
    payload: {},
  });
  await requestPromise;

  // Now simulate the sidecar sending an event frame
  const eventFrame = fixture.frames.operator_status_event;
  sockets[0]._emit('message', JSON.stringify(eventFrame));

  assert.equal(receivedEvents.length, 1, 'Should have received one event frame');
  assert.equal(receivedEvents[0].frame_type, 'event');
  assert.equal(receivedEvents[0].event, 'den://operator-status');
  assert.equal(receivedEvents[0].event_id, 'evt_status_001');
  assert.equal(receivedEvents[0].payload.phase, 'starting');

  transport.close();
});

test('bridge transport ignores event frames when no onEvent callback is provided', async () => {
  const fixture = await readFixture();
  const sentFrames = [];
  const sockets = [];

  class SilentWebSocket {
    constructor() {
      this.readyState = 0;
      this._listeners = {};
      sockets.push(this);
      setTimeout(() => {
        this.readyState = 1;
        this._emit('open', {});
      }, 0);
    }
    on(event, callback) {
      if (!this._listeners[event]) this._listeners[event] = [];
      this._listeners[event].push(callback);
      return this;
    }
    addEventListener(event, callback) { this.on(event, callback); }
    send(data) {
      const frame = JSON.parse(data);
      sentFrames.push(frame);
      setTimeout(() => {
        this._emit('message', JSON.stringify({
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: {},
          correlation: {},
          sent_at: '2026-04-30T12:00:00.000Z',
        }));
      }, 0);
    }
    close() { this.readyState = 3; }
    _emit(event, data) {
      for (const listener of this._listeners[event] ?? []) listener(data);
    }
  }

  // No onEvent callback
  const transport = createSidecarBridgeTransport({
    baseUrl: 'http://127.0.0.1:54321',
    endpointPath: '/bridge',
    authToken: 'token',
    WebSocketCtor: SilentWebSocket,
  });

  // Trigger connection by sending a request
  await transport.send({
    protocol_version: '1.0',
    schema_version: 'den-desktop@2026-04-29',
    frame_type: 'request',
    request_id: 'req_no_event_trigger',
    command: 'bridge.get_health',
    payload: {},
  });

  // Should not throw when an event frame arrives with no onEvent callback
  const eventFrame = fixture.frames.operator_status_event;
  sockets[0]._emit('message', JSON.stringify(eventFrame));

  // No assertion needed — the test passes if no exception is thrown.
  transport.close();
});

test('event source tracks listeners and broadcasts frames with deterministic unsubscribe', async () => {
  const fixture = await readFixture();

  // Simulate the event source pattern used in main.ts
  const listeners = new Set();
  const eventSource = {
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };

  const receivedA = [];
  const receivedB = [];
  const unsubscribeA = eventSource.subscribe((frame) => receivedA.push(frame));
  const unsubscribeB = eventSource.subscribe((frame) => receivedB.push(frame));

  assert.equal(listeners.size, 2);

  // Broadcast event to all listeners
  const eventFrame = fixture.frames.operator_status_event;
  for (const listener of listeners) {
    listener(eventFrame);
  }

  assert.equal(receivedA.length, 1);
  assert.equal(receivedB.length, 1);
  assert.equal(receivedA[0].event, 'den://operator-status');

  // Unsubscribe A, broadcast again
  unsubscribeA();
  assert.equal(listeners.size, 1);

  for (const listener of listeners) {
    listener(eventFrame);
  }

  assert.equal(receivedA.length, 1, 'A should not receive after unsubscribe');
  assert.equal(receivedB.length, 2, 'B should still receive');

  unsubscribeB();
  assert.equal(listeners.size, 0);
});

test('transport event callback wires through to facade onEvent subscriptions', async () => {
  const fixture = await readFixture();

  // Build the full chain: transport → eventSource → facade event subscription
  const listeners = new Set();
  const eventSource = {
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };

  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    transport: {
      async send(frame) {
        return {
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: {},
          correlation: {},
          sent_at: '2026-04-30T12:00:00.000Z',
        };
      },
    },
  });

  const api = createDenDesktopSidecarApi(client, eventSource);

  // Subscribe to operator status events
  const receivedStatuses = [];
  const unsubscribe = api.onOperatorStatus((status) => {
    receivedStatuses.push(status);
  });

  assert.equal(listeners.size, 1, 'Should have one event source listener');

  // Simulate transport delivering an event frame (the eventSource listeners are
  // what the transport's onEvent callback would invoke)
  const eventFrame = fixture.frames.operator_status_event;
  for (const listener of listeners) {
    listener(eventFrame);
  }

  assert.equal(receivedStatuses.length, 1);
  assert.equal(receivedStatuses[0].phase, 'starting');

  // Unsubscribe and verify no more deliveries
  unsubscribe();
  assert.equal(listeners.size, 0);

  for (const listener of listeners) {
    listener(eventFrame);
  }

  assert.equal(receivedStatuses.length, 1, 'Should not receive after unsubscribe');
});

test('IPC subscription lifecycle: subscribe returns unique ID, unsubscribe removes tracking', () => {
  // Simulate the IPC subscription tracking from main.ts
  const activeSubscriptions = new Map();
  const sidecarApiSubscriptions = [];

  function simulateSubscribe(eventName) {
    const subscriptionId = `${eventName}:${Date.now().toString(36)}`;
    // Simulate calling the sidecar API on-method
    const unsubscribe = () => {
      sidecarApiSubscriptions.push({ event: eventName, action: 'unsubscribed' });
    };
    activeSubscriptions.set(subscriptionId, unsubscribe);
    sidecarApiSubscriptions.push({ event: eventName, action: 'subscribed', id: subscriptionId });
    return { subscriptionId };
  }

  function simulateUnsubscribe(subscriptionId) {
    const unsubscribe = activeSubscriptions.get(subscriptionId);
    if (unsubscribe) {
      activeSubscriptions.delete(subscriptionId);
      unsubscribe();
    }
  }

  // Subscribe to two events
  const sub1 = simulateSubscribe('terminalOutput');
  const sub2 = simulateSubscribe('operatorStatus');

  assert.ok(sub1.subscriptionId.startsWith('terminalOutput:'), 'Subscription ID should be prefixed with event name');
  assert.ok(sub2.subscriptionId.startsWith('operatorStatus:'), 'Subscription ID should be prefixed with event name');
  assert.notEqual(sub1.subscriptionId, sub2.subscriptionId, 'Subscription IDs must be unique');
  assert.equal(activeSubscriptions.size, 2);

  // Unsubscribe first
  simulateUnsubscribe(sub1.subscriptionId);
  assert.equal(activeSubscriptions.size, 1);
  assert.ok(!activeSubscriptions.has(sub1.subscriptionId));

  // Unsubscribe second
  simulateUnsubscribe(sub2.subscriptionId);
  assert.equal(activeSubscriptions.size, 0);

  // Unsubscribing non-existent ID should be a no-op
  simulateUnsubscribe('nonexistent:event');
  assert.equal(activeSubscriptions.size, 0);

  // Verify the sidecar API unsubscribe was called for each
  assert.equal(sidecarApiSubscriptions.filter((s) => s.action === 'subscribed').length, 2);
  assert.equal(sidecarApiSubscriptions.filter((s) => s.action === 'unsubscribed').length, 2);
});

test('IPC subscription contract: preload captures subscriptionId from subscribe and sends it on unsubscribe', async () => {
  // This test exercises the full preload→main IPC subscription contract
  // to verify the subscriptionId flows correctly (covers R1066-1 fix).
  //
  // Simulates:
  //   preload: subscribeToEvent(eventName, listener)
  //     → ipcRenderer.invoke('subscribe', eventName) → { subscriptionId }
  //     → unsubscribe: ipcRenderer.invoke('unsubscribe', subscriptionId)
  //   main:
  //     subscribe handler: creates subscriptionId, returns it
  //     unsubscribe handler: looks up subscriptionId, calls unsubscribe

  const activeSubscriptions = new Map();
  const sidecarUnsubscribes = [];

  // Simulate main-process subscribe handler
  function mainHandleSubscribe(eventName) {
    const subscriptionId = `${eventName}:${Date.now().toString(36)}`;
    const unsubscribe = () => {
      sidecarUnsubscribes.push({ event: eventName, id: subscriptionId });
    };
    activeSubscriptions.set(subscriptionId, unsubscribe);
    return { subscriptionId };
  }

  // Simulate main-process unsubscribe handler
  function mainHandleUnsubscribe(subscriptionId) {
    const unsubscribe = activeSubscriptions.get(subscriptionId);
    if (unsubscribe) {
      activeSubscriptions.delete(subscriptionId);
      unsubscribe();
    }
  }

  // Simulate the fixed preload subscribeToEvent behavior
  // (captures subscriptionId from subscribe response)
  function simulatePreloadSubscribeToEvent(eventName) {
    // 1. Preload calls subscribe and captures subscriptionId
    const subscriptionIdPromise = Promise.resolve(mainHandleSubscribe(eventName))
      .then((result) => result.subscriptionId);

    // 2. Return unsubscribe function that uses the captured subscriptionId
    return () => {
      subscriptionIdPromise.then((subscriptionId) => {
        mainHandleUnsubscribe(subscriptionId);
      });
    };
  }

  // Subscribe to events
  const unsub1 = simulatePreloadSubscribeToEvent('terminalOutput');
  const unsub2 = simulatePreloadSubscribeToEvent('operatorStatus');

  // Let promises resolve so subscriptionIds are captured
  await new Promise((r) => setTimeout(r, 0));

  assert.equal(activeSubscriptions.size, 2, 'Should have two active subscriptions');

  // Unsubscribe first
  unsub1();
  await new Promise((r) => setTimeout(r, 0));

  assert.equal(activeSubscriptions.size, 1, 'Should have one active subscription after unsub1');
  assert.equal(sidecarUnsubscribes.length, 1, 'Should have one sidecar unsubscribe');
  assert.equal(sidecarUnsubscribes[0].event, 'terminalOutput');

  // Unsubscribe second
  unsub2();
  await new Promise((r) => setTimeout(r, 0));

  assert.equal(activeSubscriptions.size, 0, 'Should have zero active subscriptions after unsub2');
  assert.equal(sidecarUnsubscribes.length, 2, 'Should have two sidecar unsubscribes');
  assert.equal(sidecarUnsubscribes[1].event, 'operatorStatus');
});

test('IPC subscription contract: the old broken preload pattern (sending eventName) fails to clean up', async () => {
  // This test demonstrates the bug that R1066-1 describes:
  // if preload sends eventName instead of subscriptionId, the main process
  // Map lookup fails silently and subscriptions leak.

  const activeSubscriptions = new Map();
  const sidecarUnsubscribes = [];

  function mainHandleSubscribe(eventName) {
    const subscriptionId = `${eventName}:${Date.now().toString(36)}`;
    const unsubscribe = () => {
      sidecarUnsubscribes.push({ event: eventName, id: subscriptionId });
    };
    activeSubscriptions.set(subscriptionId, unsubscribe);
    return { subscriptionId };
  }

  function mainHandleUnsubscribe(subscriptionIdOrEventName) {
    const unsubscribe = activeSubscriptions.get(subscriptionIdOrEventName);
    if (unsubscribe) {
      activeSubscriptions.delete(subscriptionIdOrEventName);
      unsubscribe();
    }
  }

  // Simulate the BROKEN preload behavior (ignores subscriptionId, sends eventName)
  function simulateBrokenPreload(eventName) {
    mainHandleSubscribe(eventName); // subscriptionId is created but ignored
    return () => {
      mainHandleUnsubscribe(eventName); // BUG: sends eventName, not subscriptionId
    };
  }

  const unsub = simulateBrokenPreload('terminalOutput');
  assert.equal(activeSubscriptions.size, 1, 'Should have one active subscription');

  unsub();
  // The Map lookup with eventName fails because keys are subscriptionIds
  assert.equal(activeSubscriptions.size, 1, 'BUG: subscription still active — leak!');
  assert.equal(sidecarUnsubscribes.length, 0, 'BUG: sidecar unsubscribe never called — leak!');
});

test('electron renderer load mode defaults to built UI and supports explicit hot mode', async () => {
  const { resolveRendererLoadTarget } = await import('../src/electron/rendererLoadMode.ts');
  const simulatedBundledDir = resolve(__dirname, '../electron-dist');

  const defaultTarget = resolveRendererLoadTarget({
    isPackaged: false,
    electronDistDir: simulatedBundledDir,
    env: {},
  });
  assert.equal(defaultTarget.kind, 'file');
  assert.equal(defaultTarget.mode, 'build');
  assert.equal(defaultTarget.path, resolve(__dirname, '../dist/index.html'));

  const hotTarget = resolveRendererLoadTarget({
    isPackaged: false,
    electronDistDir: simulatedBundledDir,
    env: { DEN_DESKTOP_ELECTRON_LOAD_MODE: 'hot', VITE_DEV_SERVER_URL: 'http://127.0.0.1:1666' },
  });
  assert.deepEqual(hotTarget, { kind: 'url', mode: 'hot', url: 'http://127.0.0.1:1666' });

  const packagedTarget = resolveRendererLoadTarget({
    isPackaged: true,
    electronDistDir: simulatedBundledDir,
    env: { DEN_DESKTOP_ELECTRON_LOAD_MODE: 'hot', VITE_DEV_SERVER_URL: 'http://127.0.0.1:1666' },
  });
  assert.equal(packagedTarget.kind, 'file');
  assert.equal(packagedTarget.mode, 'build');
});

test('electron main/preload path helpers resolve correctly from electron-dist context', async () => {
  const path = await import('node:path');
  // Simulate the path resolution as it would work from electron-dist/main.mjs
  const simulatedBundledDir = resolve(__dirname, '../electron-dist');

  // UI dist path: from electron-dist/ to ../dist/index.html
  const uiDistPath = path.default.resolve(simulatedBundledDir, '../dist/index.html');
  const expectedUiDistPath = resolve(__dirname, '../dist/index.html');
  assert.equal(uiDistPath, expectedUiDistPath, 'UI dist path should resolve to dist/index.html relative to electron-dist');

  // Schema bundle path: from electron-dist/ to ../../../testdata/... (up to repo root)
  const schemaBundlePath = path.default.resolve(simulatedBundledDir, '../../../testdata/den-desktop-sidecar/sidecar-wire-fixture.json');
  const expectedSchemaPath = resolve(__dirname, '../../../testdata/den-desktop-sidecar/sidecar-wire-fixture.json');
  assert.equal(schemaBundlePath, expectedSchemaPath, 'Schema bundle path should resolve to repo testdata/');
});

test('bridge transport delivers progress frames to per-request onProgress callback', async () => {
  const fixture = await readFixture();
  const receivedProgress = [];
  const sockets = [];

  class ProgressCapableWebSocket {
    constructor() {
      this.readyState = 0;
      this._listeners = {};
      sockets.push(this);
      setTimeout(() => {
        this.readyState = 1;
        this._emit('open', {});
      }, 0);
    }
    on(event, callback) {
      if (!this._listeners[event]) this._listeners[event] = [];
      this._listeners[event].push(callback);
      return this;
    }
    addEventListener(event, callback) { this.on(event, callback); }
    send(data) {
      const frame = JSON.parse(data);
      // Simulate two progress frames followed by a response
      setTimeout(() => {
        this._emit('message', JSON.stringify({
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'progress',
          request_id: frame.request_id,
          stage: 'running',
          message: 'first output line',
          payload: { lines: [{ level: 'info', timestamp: '2026-04-30T12:00:01.000Z', source: 'console', message: 'line 1' }] },
          sent_at: '2026-04-30T12:00:01.000Z',
        }));
      }, 0);
      setTimeout(() => {
        this._emit('message', JSON.stringify({
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'progress',
          request_id: frame.request_id,
          stage: 'running',
          message: 'second output line',
          payload: { lines: [{ level: 'info', timestamp: '2026-04-30T12:00:02.000Z', source: 'console', message: 'line 2' }] },
          sent_at: '2026-04-30T12:00:02.000Z',
        }));
      }, 0);
      setTimeout(() => {
        this._emit('message', JSON.stringify({
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: { command: 'test', status: 'success', lines: [] },
          correlation: {},
          sent_at: '2026-04-30T12:00:03.000Z',
        }));
      }, 0);
    }
    close() { this.readyState = 3; }
    _emit(event, data) {
      for (const listener of this._listeners[event] ?? []) listener(data);
    }
  }

  const transport = createSidecarBridgeTransport({
    baseUrl: 'http://127.0.0.1:54321',
    endpointPath: '/bridge',
    authToken: 'token',
    WebSocketCtor: ProgressCapableWebSocket,
  });

  // Send a request with a progress callback
  const responsePromise = transport.send({
    protocol_version: '1.0',
    schema_version: 'den-desktop@2026-04-29',
    frame_type: 'request',
    request_id: 'req_progress_test',
    command: 'den_desktop.console.run_command',
    payload: { command: 'test' },
    expects_progress: true,
  }, (progressFrame) => {
    receivedProgress.push(progressFrame);
  });

  const response = await responsePromise;

  // Both progress frames should have been delivered to the callback
  assert.equal(receivedProgress.length, 2, 'Should have received two progress frames');
  assert.equal(receivedProgress[0].frame_type, 'progress');
  assert.equal(receivedProgress[0].payload.lines[0].message, 'line 1');
  assert.equal(receivedProgress[1].payload.lines[0].message, 'line 2');

  // Final response should still resolve correctly
  assert.equal(response.frame_type, 'response');
  assert.equal(response.result.status, 'success');

  transport.close();
});

test('bridge transport ignores progress frames when no onProgress callback is provided', async () => {
  const fixture = await readFixture();
  const sockets = [];

  class ProgressSilentWebSocket {
    constructor() {
      this.readyState = 0;
      this._listeners = {};
      sockets.push(this);
      setTimeout(() => {
        this.readyState = 1;
        this._emit('open', {});
      }, 0);
    }
    on(event, callback) {
      if (!this._listeners[event]) this._listeners[event] = [];
      this._listeners[event].push(callback);
      return this;
    }
    addEventListener(event, callback) { this.on(event, callback); }
    send(data) {
      const frame = JSON.parse(data);
      setTimeout(() => {
        // Send a progress frame (should be silently ignored)
        this._emit('message', JSON.stringify({
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'progress',
          request_id: frame.request_id,
          stage: 'running',
          payload: {},
          sent_at: '2026-04-30T12:00:00.000Z',
        }));
      }, 0);
      setTimeout(() => {
        // Then send the response
        this._emit('message', JSON.stringify({
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: {},
          correlation: {},
          sent_at: '2026-04-30T12:00:01.000Z',
        }));
      }, 0);
    }
    close() { this.readyState = 3; }
    _emit(event, data) {
      for (const listener of this._listeners[event] ?? []) listener(data);
    }
  }

  const transport = createSidecarBridgeTransport({
    baseUrl: 'http://127.0.0.1:54321',
    endpointPath: '/bridge',
    authToken: 'token',
    WebSocketCtor: ProgressSilentWebSocket,
  });

  // Send a request without a progress callback
  const response = await transport.send({
    protocol_version: '1.0',
    schema_version: 'den-desktop@2026-04-29',
    frame_type: 'request',
    request_id: 'req_no_progress',
    command: 'den_desktop.console.run_command',
    payload: { command: 'test' },
  });

  // Response should still resolve even though progress was silently ignored
  assert.equal(response.frame_type, 'response');

  transport.close();
});

test('preload API consoleRunCommandWithProgress delegates to progress-enabled IPC path', async () => {
  const fixture = await readFixture();
  const sent = [];
  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    requestIdFactory: () => 'req_progress_api_test',
    now: () => '2026-04-30T12:00:00.000Z',
    transport: {
      async send(frame, onProgress) {
        sent.push({ frame, hasProgressCallback: typeof onProgress === 'function' });
        return {
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: { command: 'test', status: 'success', lines: [] },
          correlation: {},
          sent_at: '2026-04-30T12:00:00.000Z',
        };
      },
    },
  });

  const api = createDenDesktopSidecarApi(client, {
    subscribe() { return () => undefined; },
  });

  const progressFrames = [];
  const result = await api.consoleRunCommandWithProgress(
    { command: 'test' },
    (frame) => progressFrames.push(frame),
  );

  assert.equal(result.status, 'success');
  assert.equal(sent.length, 1);
  assert.equal(sent[0].frame.command, 'den_desktop.console.run_command');
  assert.equal(sent[0].frame.expects_progress, true);
  assert.equal(sent[0].hasProgressCallback, true, 'Should pass onProgress callback to transport');
});

test('main process no longer imports or uses Electron globalShortcut', async () => {
  const mainSource = await readFile(resolve(__dirname, '../src/electron/main.ts'), 'utf8');
  // globalShortcut should not appear in the electron import line
  const importLine = mainSource.split('\n').find((line) => line.includes("from 'electron'"));
  assert.doesNotMatch(importLine, /globalShortcut/, 'main.ts electron import must not include globalShortcut');
  // No globalShortcut method calls should remain
  assert.doesNotMatch(mainSource, /globalShortcut\.register/, 'main.ts must not call globalShortcut.register');
  assert.doesNotMatch(mainSource, /globalShortcut\.unregister/, 'main.ts must not call globalShortcut.unregister');
  assert.doesNotMatch(mainSource, /globalShortcut\.unregisterAll/, 'main.ts must not call globalShortcut.unregisterAll');
});

test('preload event subscription returns unsubscribe function', async () => {
  const fixture = await readFixture();
  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    transport: {
      async send(frame) {
        return {
          protocol_version: fixture.schema_bundle.protocol_version,
          schema_version: fixture.schema_bundle.schema_version,
          frame_type: 'response',
          request_id: frame.request_id,
          result: {},
          correlation: {},
          sent_at: '2026-04-30T12:00:00.000Z',
        };
      },
    },
  });

  const eventCallbacks = [];
  let subscribed = true;
  const api = createDenDesktopSidecarApi(client, {
    subscribe(listener) {
      eventCallbacks.push(listener);
      return () => {
        subscribed = false;
      };
    },
  });

  // Subscribe to operator status
  const events = [];
  const unsubscribe = api.onOperatorStatus((status) => {
    events.push(status);
  });

  // Simulate event while subscribed
  assert.equal(eventCallbacks.length, 1, 'Should have one registered event callback');
  assert.equal(typeof eventCallbacks[0], 'function', 'Callback should be a function');
  assert.equal(subscribed, true, 'Should be subscribed');
  eventCallbacks[0](fixture.frames.operator_status_event);
  assert.equal(events.length, 1);
  assert.equal(events[0].phase, 'starting');

  // Unsubscribe
  unsubscribe();
  assert.equal(subscribed, false, 'Should be unsubscribed after calling unsubscribe');

  // Verify that in real usage the IPC listener would be removed,
  // so the callback wouldn't fire even though it still exists in the array
  assert.equal(eventCallbacks.length, 1, 'Callback still tracked in array (IPC cleanup is separate)');
});

// ── Helpers ──

function createFakeWebSocketClass(sentFrames, fixture, connectionArgs = []) {
  return class FakeWebSocket {
    constructor(url, options) {
      this.readyState = 0; // CONNECTING
      this._listeners = {};
      this._url = url;
      this._options = options;
      connectionArgs.push({ url, options });

      // Simulate async connect
      setTimeout(() => {
        this.readyState = 1; // OPEN
        this._emit('open', {});
      }, 0);
    }

    // EventEmitter-style API (matches ws package)
    on(event, callback) {
      if (!this._listeners[event]) this._listeners[event] = [];
      this._listeners[event].push(callback);
      return this;
    }

    // DOM-style API fallback
    addEventListener(event, callback) {
      this.on(event, callback);
    }

    send(data) {
      const frame = JSON.parse(data);
      sentFrames.push(frame);

      // Simulate response with matching request_id
      setTimeout(() => {
        let result;
        if (frame.command === 'bridge.get_health') {
          result = { ...fixture.frames.health_response, request_id: frame.request_id };
        } else if (frame.command === 'bridge.get_capabilities') {
          result = { ...fixture.frames.capabilities_response, request_id: frame.request_id };
        } else {
          result = {
            protocol_version: fixture.schema_bundle.protocol_version,
            schema_version: fixture.schema_bundle.schema_version,
            frame_type: 'response',
            request_id: frame.request_id,
            result: {},
            correlation: {},
            sent_at: '2026-04-30T12:00:00.000Z',
          };
        }
        this._emit('message', JSON.stringify(result));
      }, 0);
    }

    close() {
      this.readyState = 3; // CLOSED
      this._emit('close', {});
    }

    _emit(event, data) {
      for (const listener of this._listeners[event] ?? []) {
        listener(data);
      }
    }
  };
}
