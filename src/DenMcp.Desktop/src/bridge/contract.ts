export type JsonPrimitive = string | number | boolean | null;
export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };
export type BridgeFrameType = 'request' | 'response' | 'event' | 'progress' | 'cancel' | 'health' | 'capabilities';

export interface BridgeSchemaBundle {
  bundle_kind: 'den.bridge.schema_bundle';
  /**
   * Bundle artifact contract version. Version 1 describes this JSON container
   * shape (metadata, definitions, command/event indexes), not the app schema
   * content revision. Breaking container-shape changes increment this value.
   */
  version: 1;
  /** Stable content identity for the exact schema bundle emitted by tooling. */
  bundle_id: string;
  protocol_version: string;
  /** App schema/content compatibility version compared during bridge startup. */
  schema_version: string;
  definitions: Record<string, BridgeJsonSchema>;
  commands: BridgeCommandSchema[];
  events: BridgeEventSchema[];
}

export interface BridgeJsonSchema {
  type?: string | string[];
  const?: JsonValue;
  enum?: JsonValue[];
  required?: string[];
  properties?: Record<string, BridgeJsonSchema>;
  additionalProperties?: boolean;
  items?: BridgeJsonSchema;
  oneOf?: BridgeJsonSchema[];
  $ref?: string;
  format?: string;
}

export interface BridgeCommandSchema {
  command: string;
  request_schema: string;
  response_schema: string;
  supports_cancellation: boolean;
  supports_progress: boolean;
  required_capabilities: string[];
}

export interface BridgeEventSchema {
  event: string;
  payload_schema: string;
  required_capabilities: string[];
}

export interface BridgeCorrelation {
  trace_id?: string;
  causation_id?: string;
  parent_request_id?: string;
  task_id?: number;
  operator_session_id?: string;
  metadata?: Record<string, JsonValue>;
}

export interface BridgeBaseFrame {
  protocol_version: string;
  schema_version: string;
  frame_type: BridgeFrameType;
  correlation?: BridgeCorrelation;
  sent_at?: string;
}

export interface BridgeRequestFrame<TPayload = JsonValue> extends BridgeBaseFrame {
  frame_type: 'request';
  request_id: string;
  command: string;
  payload: TPayload;
  deadline_ms?: number;
  expects_progress?: boolean;
}

export interface BridgeResponseFrame<TResult = JsonValue> extends BridgeBaseFrame {
  frame_type: 'response';
  request_id: string;
  result?: TResult;
  error?: BridgeError;
}

export interface BridgeEventFrame<TPayload = JsonValue> extends BridgeBaseFrame {
  frame_type: 'event';
  event_id: string;
  sequence: number;
  event: string;
  payload: TPayload;
}

export interface BridgeProgressFrame<TPayload = JsonValue> extends BridgeBaseFrame {
  frame_type: 'progress';
  request_id: string;
  stage: string;
  message?: string;
  percent?: number;
  payload: TPayload;
}

export interface BridgeCancelFrame extends BridgeBaseFrame {
  frame_type: 'cancel';
  request_id: string;
  reason?: string;
}

export interface BridgeHealthFrame extends BridgeBaseFrame {
  frame_type: 'health';
  process_id: number;
  uptime_ms: number;
  ready_state: string;
  app_id: string;
  app_version: string;
  active_request_count: number;
  degraded_subsystems: string[];
  last_error?: BridgeError;
}

export interface BridgeCapabilitiesFrame extends BridgeBaseFrame {
  frame_type: 'capabilities';
  app_id: string;
  app_version: string;
  supported_transports: string[];
  commands: BridgeCommandSchema[];
  events: BridgeEventCapability[];
  feature_flags: string[];
  schema_bundle_id: string;
}

export interface BridgeEventCapability {
  event: string;
  payload_schema?: string;
  required_capabilities?: string[];
}

export type BridgeFrame =
  | BridgeRequestFrame
  | BridgeResponseFrame
  | BridgeEventFrame
  | BridgeProgressFrame
  | BridgeCancelFrame
  | BridgeHealthFrame
  | BridgeCapabilitiesFrame;

export interface BridgeError {
  code: string;
  message: string;
  category: BridgeErrorCategory;
  details?: JsonValue;
  retryable?: boolean;
  caused_by?: BridgeError[];
}

export type BridgeErrorCategory =
  | 'validation'
  | 'not_found'
  | 'conflict'
  | 'unauthorized'
  | 'transient'
  | 'cancelled'
  | 'internal'
  | 'unavailable'
  | 'unsupported_capability';

export interface BridgeFrameCheckOptions {
  resultSchema?: string;
}

export interface BridgeCommandSpec<TRequest extends JsonValue, TResponse extends JsonValue> {
  command: string;
  requestSchema: string;
  responseSchema: string;
  supportsCancellation?: boolean;
  supportsProgress?: boolean;
}

export interface BridgeEventSpec<TPayload extends JsonValue> {
  event: string;
  payloadSchema: string;
}

export interface BridgeClientTransport {
  send(frame: BridgeRequestFrame, onProgress?: (frame: BridgeProgressFrame) => void): Promise<BridgeResponseFrame>;
  cancel?(frame: BridgeCancelFrame): Promise<void>;
}

export interface BridgeClientOptions<
  TCommands extends Record<string, BridgeCommandSpec<JsonValue, JsonValue>>,
  TEvents extends Record<string, BridgeEventSpec<JsonValue>> = Record<string, never>,
> {
  bundle: BridgeSchemaBundle;
  commands: TCommands;
  events?: TEvents;
  transport: BridgeClientTransport;
  requestIdFactory?: () => string;
  now?: () => string;
  correlation?: () => BridgeCorrelation | undefined;
}

export interface BridgeCallOptions {
  requestId?: string;
  deadlineMs?: number;
  expectsProgress?: boolean;
  correlation?: BridgeCorrelation;
  /**
   * Optional per-request progress callback. When the transport receives a
   * progress frame whose request_id matches this call, the callback is
   * invoked before the final response arrives. This enables incremental
   * rendering of structured command output (e.g. ConsoleDock lines).
   */
  onProgress?: (frame: BridgeProgressFrame) => void;
}

export type RequestOf<TSpec> = TSpec extends BridgeCommandSpec<infer TRequest, JsonValue> ? TRequest : never;
export type ResponseOf<TSpec> = TSpec extends BridgeCommandSpec<JsonValue, infer TResponse> ? TResponse : never;
export type PayloadOf<TSpec> = TSpec extends BridgeEventSpec<infer TPayload> ? TPayload : never;

export interface CheckedBridgeClient<
  TCommands extends Record<string, BridgeCommandSpec<JsonValue, JsonValue>>,
  TEvents extends Record<string, BridgeEventSpec<JsonValue>> = Record<string, never>,
> {
  readonly bundle: BridgeSchemaBundle;
  readonly commands: TCommands;
  readonly events: TEvents;
  call<TKey extends keyof TCommands>(
    key: TKey,
    payload: RequestOf<TCommands[TKey]>,
    options?: BridgeCallOptions,
  ): Promise<ResponseOf<TCommands[TKey]>>;
  cancel(requestId: string, reason?: string, correlation?: BridgeCorrelation): Promise<void>;
  assertEvent<TKey extends keyof TEvents>(key: TKey, frame: BridgeEventFrame): asserts frame is BridgeEventFrame<PayloadOf<TEvents[TKey]>>;
}

export type BridgeCommandFacade<TCommands extends Record<string, BridgeCommandSpec<JsonValue, JsonValue>>> = {
  [TKey in keyof TCommands]: (
    payload: RequestOf<TCommands[TKey]>,
    options?: BridgeCallOptions,
  ) => Promise<ResponseOf<TCommands[TKey]>>;
};

export class BridgeContractError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'BridgeContractError';
  }
}

export class BridgeResponseError extends Error {
  readonly bridgeError: BridgeError;

  constructor(error: BridgeError) {
    super(error.message);
    this.name = 'BridgeResponseError';
    this.bridgeError = error;
  }
}

export function assertBridgeSchemaBundle(value: unknown): asserts value is BridgeSchemaBundle {
  const bundle = expectRecord(value, 'schema bundle');
  expectString(bundle.bundle_kind, 'bundle.bundle_kind');
  if (bundle.bundle_kind !== 'den.bridge.schema_bundle') {
    fail(`Unsupported bridge schema bundle kind '${String(bundle.bundle_kind)}'.`);
  }

  expectNumber(bundle.version, 'bundle.version');
  if (bundle.version !== 1) {
    fail(
      `Unsupported bridge schema bundle container version '${String(bundle.version)}'; ` +
        'this TypeScript client supports container version 1. Schema content compatibility is tracked by bundle_id, protocol_version, and schema_version.',
    );
  }

  expectString(bundle.bundle_id, 'bundle.bundle_id');
  expectString(bundle.protocol_version, 'bundle.protocol_version');
  expectString(bundle.schema_version, 'bundle.schema_version');
  const definitions = expectRecord(bundle.definitions, 'bundle.definitions') as Record<string, BridgeJsonSchema>;
  for (const name of [
    'bridge.request_frame',
    'bridge.response_frame',
    'bridge.event_frame',
    'bridge.progress_frame',
    'bridge.cancel_frame',
    'bridge.health_frame',
    'bridge.capabilities_frame',
    'bridge.error',
  ]) {
    if (!(name in definitions)) {
      fail(`Bridge schema bundle is missing definition '${name}'.`);
    }
  }

  const commands = expectArray(bundle.commands, 'bundle.commands');
  for (const commandValue of commands) {
    const command = expectRecord(commandValue, 'bundle.commands[]');
    const commandName = expectString(command.command, 'command.command');
    const requestSchema = expectString(command.request_schema, `command ${commandName}.request_schema`);
    const responseSchema = expectString(command.response_schema, `command ${commandName}.response_schema`);
    expectBoolean(command.supports_cancellation, `command ${commandName}.supports_cancellation`);
    expectBoolean(command.supports_progress, `command ${commandName}.supports_progress`);
    expectStringArray(command.required_capabilities, `command ${commandName}.required_capabilities`);
    requireDefinition(definitions, requestSchema);
    requireDefinition(definitions, responseSchema);
  }

  const events = expectArray(bundle.events, 'bundle.events');
  for (const eventValue of events) {
    const event = expectRecord(eventValue, 'bundle.events[]');
    const eventName = expectString(event.event, 'event.event');
    const payloadSchema = expectString(event.payload_schema, `event ${eventName}.payload_schema`);
    expectStringArray(event.required_capabilities, `event ${eventName}.required_capabilities`);
    requireDefinition(definitions, payloadSchema);
  }
}

export function assertBridgeFrameMatchesBundle(
  frameValue: unknown,
  bundle: BridgeSchemaBundle,
  options: BridgeFrameCheckOptions = {},
): asserts frameValue is BridgeFrame {
  assertBridgeSchemaBundle(bundle);
  const frame = expectRecord(frameValue, 'bridge frame');
  expectString(frame.protocol_version, 'frame.protocol_version');
  if (frame.protocol_version !== bundle.protocol_version) {
    fail(`Frame protocol_version '${frame.protocol_version}' does not match bundle '${bundle.protocol_version}'.`);
  }

  expectString(frame.schema_version, 'frame.schema_version');
  if (frame.schema_version !== bundle.schema_version) {
    fail(`Frame schema_version '${frame.schema_version}' does not match bundle '${bundle.schema_version}'.`);
  }

  const frameType = expectString(frame.frame_type, 'frame.frame_type') as BridgeFrameType;
  switch (frameType) {
    case 'request': {
      assertJsonMatchesSchema(frame, bundle, 'bridge.request_frame');
      const commandName = expectString(frame.command, 'request.command');
      const command = findCommand(bundle, commandName);
      assertJsonMatchesSchema(frame.payload, bundle, command.request_schema);
      return;
    }
    case 'response': {
      assertJsonMatchesSchema(frame, bundle, 'bridge.response_frame');
      const hasResult = Object.hasOwn(frame, 'result');
      const hasError = Object.hasOwn(frame, 'error');
      if (hasResult === hasError) {
        fail('Bridge response frame must contain exactly one of result or error.');
      }

      if (hasResult && options.resultSchema) {
        assertJsonMatchesSchema(frame.result, bundle, options.resultSchema);
      }

      if (hasError) {
        assertJsonMatchesSchema(frame.error, bundle, 'bridge.error');
      }

      return;
    }
    case 'event': {
      assertJsonMatchesSchema(frame, bundle, 'bridge.event_frame');
      const eventName = expectString(frame.event, 'event.event');
      const event = findEvent(bundle, eventName);
      assertJsonMatchesSchema(frame.payload, bundle, event.payload_schema);
      return;
    }
    case 'progress':
      assertJsonMatchesSchema(frame, bundle, 'bridge.progress_frame');
      return;
    case 'cancel':
      assertJsonMatchesSchema(frame, bundle, 'bridge.cancel_frame');
      return;
    case 'health':
      assertJsonMatchesSchema(frame, bundle, 'bridge.health_frame');
      return;
    case 'capabilities':
      assertJsonMatchesSchema(frame, bundle, 'bridge.capabilities_frame');
      assertCapabilitiesCompatibleWithBundle(frame as unknown as BridgeCapabilitiesFrame, bundle);
      return;
    default:
      fail(`Unsupported bridge frame_type '${String(frame.frame_type)}'.`);
  }
}

export function assertCapabilitiesCompatibleWithBundle(
  capabilitiesValue: unknown,
  bundle: BridgeSchemaBundle,
): asserts capabilitiesValue is BridgeCapabilitiesFrame {
  const capabilities = expectRecord(capabilitiesValue, 'capabilities frame');
  const schemaBundleId = expectString(capabilities.schema_bundle_id, 'capabilities.schema_bundle_id');
  if (schemaBundleId !== bundle.bundle_id) {
    fail(`Capabilities schema_bundle_id '${schemaBundleId}' does not match bundle '${bundle.bundle_id}'.`);
  }

  const commands = expectArray(capabilities.commands, 'capabilities.commands');
  for (const bundleCommand of bundle.commands) {
    const capability = commands.find((value) => isRecord(value) && value.command === bundleCommand.command);
    if (!capability) {
      fail(`Capabilities frame is missing command '${bundleCommand.command}'.`);
    }

    const command = expectRecord(capability, `capabilities.commands.${bundleCommand.command}`);
    expectEqual(command.request_schema, bundleCommand.request_schema, `${bundleCommand.command}.request_schema`);
    expectEqual(command.response_schema, bundleCommand.response_schema, `${bundleCommand.command}.response_schema`);
    expectEqual(command.supports_cancellation, bundleCommand.supports_cancellation, `${bundleCommand.command}.supports_cancellation`);
    expectEqual(command.supports_progress, bundleCommand.supports_progress, `${bundleCommand.command}.supports_progress`);
    expectStringArray(command.required_capabilities, `${bundleCommand.command}.required_capabilities`);
  }

  const events = expectArray(capabilities.events, 'capabilities.events');
  for (const bundleEvent of bundle.events) {
    const capability = events.find((value) => isRecord(value) && value.event === bundleEvent.event);
    if (!capability) {
      fail(`Capabilities frame is missing event '${bundleEvent.event}'.`);
    }

    const event = expectRecord(capability, `capabilities.events.${bundleEvent.event}`);
    expectEqual(event.payload_schema, bundleEvent.payload_schema, `${bundleEvent.event}.payload_schema`);
    if (event.required_capabilities !== undefined) {
      expectStringArray(event.required_capabilities, `${bundleEvent.event}.required_capabilities`);
    }
  }
}

export function assertJsonMatchesSchema(value: unknown, bundle: BridgeSchemaBundle, schemaName: string): void {
  const schema = requireDefinition(bundle.definitions, schemaName);
  assertValueMatchesSchema(value, schema, bundle, schemaName);
}

export function createCheckedBridgeClient<
  TCommands extends Record<string, BridgeCommandSpec<JsonValue, JsonValue>>,
  TEvents extends Record<string, BridgeEventSpec<JsonValue>> = Record<string, never>,
>(options: BridgeClientOptions<TCommands, TEvents>): CheckedBridgeClient<TCommands, TEvents> {
  assertBridgeSchemaBundle(options.bundle);
  for (const spec of Object.values(options.commands)) {
    const bundleCommand = findCommand(options.bundle, spec.command);
    expectEqual(bundleCommand.request_schema, spec.requestSchema, `${spec.command}.requestSchema`);
    expectEqual(bundleCommand.response_schema, spec.responseSchema, `${spec.command}.responseSchema`);
  }

  const events = (options.events ?? {}) as TEvents;
  for (const spec of Object.values(events)) {
    const bundleEvent = findEvent(options.bundle, spec.event);
    expectEqual(bundleEvent.payload_schema, spec.payloadSchema, `${spec.event}.payloadSchema`);
  }

  const requestIdFactory = options.requestIdFactory ?? defaultRequestIdFactory;
  const now = options.now ?? (() => new Date().toISOString());

  return {
    bundle: options.bundle,
    commands: options.commands,
    events,
    async call(key, payload, callOptions) {
      const spec = options.commands[key];
      if (!spec) {
        fail(`Bridge command key '${String(key)}' is not allow-listed.`);
      }

      assertJsonMatchesSchema(payload, options.bundle, spec.requestSchema);
      const frame: BridgeRequestFrame = {
        protocol_version: options.bundle.protocol_version,
        schema_version: options.bundle.schema_version,
        frame_type: 'request',
        request_id: callOptions?.requestId ?? requestIdFactory(),
        command: spec.command,
        payload: payload as JsonValue,
        sent_at: now(),
      };
      const correlation = callOptions?.correlation ?? options.correlation?.();
      if (correlation !== undefined) {
        frame.correlation = correlation;
      }

      if (callOptions?.deadlineMs !== undefined) {
        frame.deadline_ms = callOptions.deadlineMs;
      }

      if (callOptions?.expectsProgress !== undefined) {
        frame.expects_progress = callOptions.expectsProgress;
      }

      assertBridgeFrameMatchesBundle(frame, options.bundle);
      const response = await options.transport.send(frame, callOptions?.onProgress);
      assertBridgeFrameMatchesBundle(response, options.bundle, { resultSchema: spec.responseSchema });
      if (response.error) {
        throw new BridgeResponseError(response.error);
      }

      return response.result as ResponseOf<TCommands[typeof key]>;
    },
    async cancel(requestId, reason, correlation) {
      const frame: BridgeCancelFrame = {
        protocol_version: options.bundle.protocol_version,
        schema_version: options.bundle.schema_version,
        frame_type: 'cancel',
        request_id: requestId,
        sent_at: now(),
      };
      if (reason !== undefined) {
        frame.reason = reason;
      }

      if (correlation !== undefined) {
        frame.correlation = correlation;
      }
      assertBridgeFrameMatchesBundle(frame, options.bundle);
      await options.transport.cancel?.(frame);
    },
    assertEvent(key, frame): asserts frame is BridgeEventFrame<PayloadOf<TEvents[typeof key]>> {
      const spec = events[key];
      if (!spec) {
        fail(`Bridge event key '${String(key)}' is not allow-listed.`);
      }

      assertBridgeFrameMatchesBundle(frame, options.bundle);
      if (frame.event !== spec.event) {
        fail(`Expected event '${spec.event}', got '${frame.event}'.`);
      }
    },
  };
}

export function createBridgeCommandFacade<
  TCommands extends Record<string, BridgeCommandSpec<JsonValue, JsonValue>>,
  TEvents extends Record<string, BridgeEventSpec<JsonValue>> = Record<string, never>,
>(
  client: CheckedBridgeClient<TCommands, TEvents>,
): BridgeCommandFacade<TCommands> {
  const facade = {} as BridgeCommandFacade<TCommands>;
  for (const key of Object.keys(client.commands) as Array<keyof TCommands>) {
    facade[key] = ((payload: RequestOf<TCommands[typeof key]>, options?: BridgeCallOptions) =>
      client.call(key, payload, options)) as BridgeCommandFacade<TCommands>[typeof key];
  }

  return facade;
}

function findCommand(bundle: BridgeSchemaBundle, commandName: string): BridgeCommandSchema {
  const command = bundle.commands.find((candidate) => candidate.command === commandName);
  if (!command) {
    fail(`Bridge command '${commandName}' is not present in schema bundle '${bundle.bundle_id}'.`);
  }

  return command;
}

function findEvent(bundle: BridgeSchemaBundle, eventName: string): BridgeEventSchema {
  const event = bundle.events.find((candidate) => candidate.event === eventName);
  if (!event) {
    fail(`Bridge event '${eventName}' is not present in schema bundle '${bundle.bundle_id}'.`);
  }

  return event;
}

function assertValueMatchesSchema(
  value: unknown,
  schema: BridgeJsonSchema,
  bundle: BridgeSchemaBundle,
  path: string,
): void {
  if (schema.$ref) {
    assertValueMatchesSchema(value, requireDefinition(bundle.definitions, schema.$ref), bundle, `${path} -> ${schema.$ref}`);
    return;
  }

  if (schema.const !== undefined && !jsonEqual(value, schema.const)) {
    fail(`${path} must equal ${JSON.stringify(schema.const)}.`);
  }

  if (schema.enum && !schema.enum.some((candidate) => jsonEqual(value, candidate))) {
    fail(`${path} must be one of ${JSON.stringify(schema.enum)}.`);
  }

  if (schema.oneOf) {
    const branchResults = schema.oneOf.map((candidate) => collectSchemaMatch(value, candidate, bundle, path));
    const matches = branchResults.filter((result) => result.matched).length;
    if (matches !== 1) {
      const details = branchResults
        .map((result, index) => (result.matched ? `branch ${index + 1}: matched` : `branch ${index + 1}: ${result.error}`))
        .join('; ');
      fail(`${path} must match exactly one oneOf schema; matched ${matches}. Branch results: ${details}.`);
    }
  }

  if (schema.format !== undefined) {
    assertValueMatchesFormat(value, schema.format, path);
  }

  if (schema.type !== undefined) {
    const allowedTypes = Array.isArray(schema.type) ? schema.type : [schema.type];
    const actualType = jsonType(value);
    if (!allowedTypes.includes(actualType) && !(actualType === 'integer' && allowedTypes.includes('number'))) {
      fail(`${path} must be ${allowedTypes.join(' | ')}, got ${actualType}.`);
    }
  }

  if (schema.properties || schema.required || schema.additionalProperties === false) {
    const record = expectRecord(value, path);
    for (const property of schema.required ?? []) {
      if (!Object.hasOwn(record, property)) {
        fail(`${path} is missing required property '${property}'.`);
      }
    }

    const properties = schema.properties ?? {};
    if (schema.additionalProperties === false) {
      for (const property of Object.keys(record)) {
        if (!(property in properties)) {
          fail(`${path} has unexpected property '${property}'.`);
        }
      }
    }

    for (const [property, propertySchema] of Object.entries(properties)) {
      if (Object.hasOwn(record, property)) {
        assertValueMatchesSchema(record[property], propertySchema, bundle, `${path}.${property}`);
      }
    }
  }

  if (schema.items !== undefined) {
    const values = expectArray(value, path);
    values.forEach((item, index) => assertValueMatchesSchema(item, schema.items!, bundle, `${path}[${index}]`));
  }
}

type SchemaMatchResult = { matched: true } | { matched: false; error: string };

function collectSchemaMatch(
  value: unknown,
  schema: BridgeJsonSchema,
  bundle: BridgeSchemaBundle,
  path: string,
): SchemaMatchResult {
  try {
    assertValueMatchesSchema(value, schema, bundle, path);
    return { matched: true };
  } catch (error) {
    if (error instanceof BridgeContractError) {
      return { matched: false, error: error.message };
    }

    throw error;
  }
}

function assertValueMatchesFormat(value: unknown, format: string, path: string): void {
  // Format validators must own their type checks even when the schema omits
  // `type: "string"`. Unknown formats remain annotations, but any future
  // active string format (for example `uri` or `email`) should reject
  // non-string values before applying its syntax check, matching date-time.
  switch (format) {
    case 'date-time':
      if (typeof value !== 'string' || !isJsonSchemaDateTime(value)) {
        fail(`${path} must match date-time format.`);
      }
      return;
    default:
      return;
  }
}

function isJsonSchemaDateTime(value: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?(Z|[+-]\d{2}:\d{2})$/.exec(value);
  if (!match) {
    return false;
  }

  const [, yearText, monthText, dayText, hourText, minuteText, secondText, offsetText] = match;
  const year = Number(yearText);
  const month = Number(monthText);
  const day = Number(dayText);
  const hour = Number(hourText);
  const minute = Number(minuteText);
  const second = Number(secondText);
  const offsetHour = offsetText === 'Z' ? 0 : Number(offsetText.slice(1, 3));
  const offsetMinute = offsetText === 'Z' ? 0 : Number(offsetText.slice(4, 6));

  return month >= 1 &&
    month <= 12 &&
    day >= 1 &&
    day <= daysInMonth(year, month) &&
    hour <= 23 &&
    minute <= 59 &&
    second <= 59 &&
    offsetHour <= 23 &&
    offsetMinute <= 59 &&
    Number.isFinite(Date.parse(value));
}

function daysInMonth(year: number, month: number): number {
  return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

function requireDefinition(definitions: Record<string, BridgeJsonSchema>, name: string): BridgeJsonSchema {
  const schema = definitions[name];
  if (!schema) {
    fail(`Bridge schema definition '${name}' is missing.`);
  }

  return schema;
}

function expectRecord(value: unknown, name: string): Record<string, unknown> {
  if (!isRecord(value)) {
    fail(`${name} must be an object.`);
  }

  return value;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function expectArray(value: unknown, name: string): unknown[] {
  if (!Array.isArray(value)) {
    fail(`${name} must be an array.`);
  }

  return value;
}

function expectString(value: unknown, name: string): string {
  if (typeof value !== 'string') {
    fail(`${name} must be a string.`);
  }

  return value;
}

function expectNumber(value: unknown, name: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    fail(`${name} must be a finite number.`);
  }

  return value;
}

function expectBoolean(value: unknown, name: string): boolean {
  if (typeof value !== 'boolean') {
    fail(`${name} must be a boolean.`);
  }

  return value;
}

function expectStringArray(value: unknown, name: string): string[] {
  const values = expectArray(value, name);
  for (const item of values) {
    expectString(item, name);
  }

  return values as string[];
}

function expectEqual(actual: unknown, expected: unknown, name: string): void {
  if (!jsonEqual(actual, expected)) {
    fail(`${name} expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}.`);
  }
}

function jsonType(value: unknown): string {
  if (value === null) {
    return 'null';
  }

  if (Array.isArray(value)) {
    return 'array';
  }

  if (typeof value === 'number' && Number.isInteger(value)) {
    return 'integer';
  }

  return typeof value;
}

function jsonEqual(left: unknown, right: unknown): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}

function defaultRequestIdFactory(): string {
  return `req_${Date.now().toString(36)}_${Math.random().toString(36).slice(2)}`;
}

function fail(message: string): never {
  throw new BridgeContractError(message);
}
