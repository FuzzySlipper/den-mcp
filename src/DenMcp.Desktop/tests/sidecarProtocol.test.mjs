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
import { SidecarSupervisor, buildDevSidecarLaunchConfig } from '../src/electron/sidecarSupervisor.ts';

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
  assert.deepEqual(bundle.commands.map((command) => command.command), ['bridge.get_capabilities', 'bridge.get_health']);
  assert.deepEqual(bundle.events.map((event) => event.event), ['den_desktop.runtime.placeholder']);

  assertBridgeFrameMatchesBundle(frames.health_response, bundle, { resultSchema: 'bridge.get_health.response' });
  assertBridgeFrameMatchesBundle(frames.capabilities_response, bundle, { resultSchema: 'bridge.get_capabilities.response' });
  assertBridgeFrameMatchesBundle(frames.placeholder_event, bundle);
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

test('sidecar checked facade allow-lists health/capabilities and placeholder events only', async () => {
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
        return frame.command === 'bridge.get_health'
          ? fixture.frames.health_response
          : fixture.frames.capabilities_response;
      },
    },
  });
  const facade = createSidecarBridgeFacade(client);

  const health = await facade.getHealth();
  const capabilities = await facade.getCapabilities();
  facade.assertPlaceholderRuntimeEvent(fixture.frames.placeholder_event);

  assert.equal(health.schema_bundle_id, fixture.schema_bundle.bundle_id);
  assert.ok(capabilities.supported_transports.includes('loopback_websocket'));
  assert.deepEqual(sent.map((frame) => frame.command), ['bridge.get_health', 'bridge.get_capabilities']);
  assert.deepEqual(Object.keys(facade).sort(), ['assertPlaceholderRuntimeEvent', 'getCapabilities', 'getHealth'].sort());
});

test('preload sidecar API exposes no generic dispatch, token, endpoint, or node escape hatch', async () => {
  const fixture = await readFixture();
  const client = createCheckedBridgeClient({
    bundle: fixture.schema_bundle,
    commands: sidecarCommands,
    events: sidecarEvents,
    transport: {
      async send(frame) {
        return frame.command === 'bridge.get_health'
          ? fixture.frames.health_response
          : fixture.frames.capabilities_response;
      },
    },
  });
  const api = createDenDesktopSidecarApi(client, {
    subscribe(listener) {
      listener(fixture.frames.placeholder_event);
      return () => undefined;
    },
  });
  const events = [];
  api.onPlaceholderRuntimeEvent((event) => events.push(event));

  assert.deepEqual(Object.keys(api).sort(), ['getCapabilities', 'getHealth', 'onPlaceholderRuntimeEvent'].sort());
  assert.equal(api.dispatch, undefined);
  assert.equal(api.ipcRenderer, undefined);
  assert.equal(api.token, undefined);
  assert.equal(api.endpoint, undefined);
  assert.equal(api.fs, undefined);
  assert.equal(events[0].schema_version, fixture.schema_bundle.schema_version);
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
  };
}
