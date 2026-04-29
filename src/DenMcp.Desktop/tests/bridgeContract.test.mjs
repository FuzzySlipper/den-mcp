import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import {
  assertBridgeFrameMatchesBundle,
  assertBridgeSchemaBundle,
  assertCapabilitiesCompatibleWithBundle,
  createBridgeCommandFacade,
  createCheckedBridgeClient,
} from '../src/bridge/contract.ts';

const __dirname = dirname(fileURLToPath(import.meta.url));
const fixtureRoot = resolve(__dirname, '../../../testdata/bridge-contract');

async function readJson(name) {
  return JSON.parse(await readFile(resolve(fixtureRoot, name), 'utf8'));
}

test('bridge schema bundle exposes deterministic protocol and representative DTO contracts', async () => {
  const bundle = await readJson('sample-schema-bundle.json');

  assertBridgeSchemaBundle(bundle);
  assert.equal(bundle.bundle_id, 'sample.bridge@2026-04-29');
  assert.equal(bundle.protocol_version, '1.0');
  assert.equal(bundle.schema_version, 'den-desktop@2026-04-29');
  assert.deepEqual(bundle.commands.map((command) => command.command), ['sample.echo']);
  assert.deepEqual(bundle.events.map((event) => event.event), ['sample.echoed']);
  assert.ok(bundle.definitions['sample.echo.request']);
  assert.ok(bundle.definitions['sample.echo.response']);
  assert.ok(bundle.definitions['sample.echoed.payload']);
});

test('TypeScript bridge checks accept C# representative wire frames', async () => {
  const bundle = await readJson('sample-schema-bundle.json');
  const fixture = await readJson('sample-wire-frames.json');
  const frames = fixture.frames;

  assertBridgeSchemaBundle(bundle);
  assert.equal(fixture.schema_bundle_id, bundle.bundle_id);
  assertBridgeFrameMatchesBundle(frames.request, bundle);
  assertBridgeFrameMatchesBundle(frames.response_success, bundle, { resultSchema: 'sample.echo.response' });
  assertBridgeFrameMatchesBundle(frames.response_error, bundle);
  assertBridgeFrameMatchesBundle(frames.event, bundle);
  assertBridgeFrameMatchesBundle(frames.progress, bundle);
  assertBridgeFrameMatchesBundle(frames.cancel, bundle);
  assertBridgeFrameMatchesBundle(frames.health, bundle);
  assertBridgeFrameMatchesBundle(frames.capabilities, bundle);
  assertCapabilitiesCompatibleWithBundle(frames.capabilities, bundle);
});

test('checked client and facade keep preload exposure allow-list oriented', async () => {
  const bundle = await readJson('sample-schema-bundle.json');
  const fixture = await readJson('sample-wire-frames.json');
  const sentRequests = [];
  const sentCancels = [];

  const client = createCheckedBridgeClient({
    bundle,
    commands: {
      echo: {
        command: 'sample.echo',
        requestSchema: 'sample.echo.request',
        responseSchema: 'sample.echo.response',
        supportsCancellation: true,
        supportsProgress: true,
      },
    },
    events: {
      echoed: {
        event: 'sample.echoed',
        payloadSchema: 'sample.echoed.payload',
      },
    },
    requestIdFactory: () => 'req_001',
    now: () => '2026-04-29T12:34:56.000Z',
    transport: {
      async send(frame) {
        sentRequests.push(frame);
        return fixture.frames.response_success;
      },
      async cancel(frame) {
        sentCancels.push(frame);
      },
    },
  });
  const facade = createBridgeCommandFacade(client);

  const response = await facade.echo({ message: 'hello' }, { expectsProgress: true, deadlineMs: 30000 });
  assert.deepEqual(response, { echo: 'hello', request_id: 'req_001' });
  assert.equal(sentRequests.length, 1);
  assert.equal(sentRequests[0].command, 'sample.echo');
  assert.equal(sentRequests[0].payload.message, 'hello');
  assert.deepEqual(Object.keys(facade), ['echo']);

  client.assertEvent('echoed', fixture.frames.event);
  await client.cancel('req_001', 'user_requested');
  assert.equal(sentCancels.length, 1);
  assert.equal(sentCancels[0].frame_type, 'cancel');
  assert.equal(sentCancels[0].request_id, 'req_001');
});

test('checked client rejects payloads that drift from the shared schema bundle', async () => {
  const bundle = await readJson('sample-schema-bundle.json');
  const client = createCheckedBridgeClient({
    bundle,
    commands: {
      echo: {
        command: 'sample.echo',
        requestSchema: 'sample.echo.request',
        responseSchema: 'sample.echo.response',
      },
    },
    transport: {
      async send() {
        throw new Error('send should not be called for invalid payloads');
      },
    },
  });
  const facade = createBridgeCommandFacade(client);

  await assert.rejects(
    () => facade.echo({ message: 'hello', arbitrary_escape_hatch: true }),
    /unexpected property 'arbitrary_escape_hatch'/,
  );
});
