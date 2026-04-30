import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import {
  assertBridgeFrameMatchesBundle,
  assertBridgeSchemaBundle,
  assertCapabilitiesCompatibleWithBundle,
  assertJsonMatchesSchema,
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

test('TypeScript bridge checks reject invalid date-time formatted fields', async () => {
  const bundle = await readJson('sample-schema-bundle.json');
  const fixture = await readJson('sample-wire-frames.json');
  const invalidRequest = structuredClone(fixture.frames.request);
  invalidRequest.sent_at = 'not-a-date';

  assert.throws(
    () => assertBridgeFrameMatchesBundle(invalidRequest, bundle),
    /bridge\.request_frame\.sent_at must match date-time format\./,
  );
});

test('TypeScript bridge checks reject non-string date-time values even without an explicit string type', async () => {
  const bundle = await readJson('sample-schema-bundle.json');
  bundle.definitions['sample.implicit_date_time'] = { format: 'date-time' };

  assert.doesNotThrow(() => assertJsonMatchesSchema('2026-04-29T12:34:56Z', bundle, 'sample.implicit_date_time'));
  assert.throws(
    () => assertJsonMatchesSchema(1777466096000, bundle, 'sample.implicit_date_time'),
    /sample\.implicit_date_time must match date-time format\./,
  );
});

test('oneOf mismatch diagnostics include nested branch failures', async () => {
  const bundle = await readJson('sample-schema-bundle.json');
  bundle.definitions['sample.one_of_nested'] = {
    oneOf: [
      {
        type: 'object',
        additionalProperties: false,
        required: ['kind', 'payload'],
        properties: {
          kind: { const: 'text' },
          payload: {
            type: 'object',
            additionalProperties: false,
            required: ['message'],
            properties: { message: { type: 'string' } },
          },
        },
      },
      {
        type: 'object',
        additionalProperties: false,
        required: ['kind', 'payload'],
        properties: {
          kind: { const: 'count' },
          payload: {
            type: 'object',
            additionalProperties: false,
            required: ['count'],
            properties: { count: { type: 'integer' } },
          },
        },
      },
    ],
  };

  assert.throws(
    () => assertJsonMatchesSchema({ kind: 'text', payload: { message: 42 } }, bundle, 'sample.one_of_nested'),
    (error) => {
      assert.ok(error instanceof Error);
      assert.match(error.message, /sample\.one_of_nested must match exactly one oneOf schema; matched 0\./);
      assert.match(error.message, /branch 1: sample\.one_of_nested\.payload\.message must be string, got integer\./);
      assert.match(error.message, /branch 2: sample\.one_of_nested\.kind must equal "count"\./);
      return true;
    },
  );
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
