/**
 * Lightweight runtime validation for app-agent sidecar bridge responses.
 *
 * The preload/sidecar bridge boundary returns `AppAgentResponse`
 * (`Record<string, JsonValue>`). These validators check the expected
 * response shape before the renderer trusts the payload, turning silent
 * mis-cast bugs into early, descriptive errors.
 *
 * Validators return the validated value typed as `T`. They are
 * intentionally cheap: top-level key presence and critical field types
 * only — no deep schema validation.
 *
 * This module is the single designated cast boundary for app-agent
 * responses. All other code should rely on the validated types rather
 * than casting raw bridge output directly.
 */

// ── Internal helpers ──

function hasObjectShape(value: unknown, label: string): asserts value is Record<string, unknown> {
  if (value === null || value === undefined || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`sidecar bridge validation: ${label} expected an object, got ${value === null ? 'null' : Array.isArray(value) ? 'an array' : typeof value}`);
  }
}

function requireStringField(obj: Record<string, unknown>, field: string, label: string): void {
  const value = obj[field];
  if (value === undefined || value === null) {
    throw new Error(`sidecar bridge validation: ${label}.${field} is required`);
  }
  if (typeof value !== 'string') {
    throw new Error(`sidecar bridge validation: ${label}.${field} expected string, got ${typeof value}`);
  }
}

function requireNumberField(obj: Record<string, unknown>, field: string, label: string): void {
  const value = obj[field];
  if (value === undefined || value === null) {
    throw new Error(`sidecar bridge validation: ${label}.${field} is required`);
  }
  if (typeof value !== 'number') {
    throw new Error(`sidecar bridge validation: ${label}.${field} expected number, got ${typeof value}`);
  }
}

function requireBooleanField(obj: Record<string, unknown>, field: string, label: string): void {
  const value = obj[field];
  if (typeof value !== 'boolean') {
    throw new Error(`sidecar bridge validation: ${label}.${field} expected boolean, got ${typeof value}`);
  }
}

// ── Public validators ──

/**
 * Validate a build-context response: must have a `.context` object
 * with numeric `context_version`, string `built_at`, and array `warnings`.
 */
export function validateBuildContextResponse<T>(raw: unknown): T {
  hasObjectShape(raw, 'buildContextResponse');
  const resp = raw as Record<string, unknown>;

  if (!resp.context || typeof resp.context !== 'object' || Array.isArray(resp.context)) {
    throw new Error(`sidecar bridge validation: buildContextResponse.context expected an object, got ${typeof resp.context}`);
  }

  const ctx = resp.context as Record<string, unknown>;
  requireNumberField(ctx, 'context_version', 'buildContextResponse.context');
  requireStringField(ctx, 'built_at', 'buildContextResponse.context');

  if (!Array.isArray(ctx.warnings)) {
    throw new Error(`sidecar bridge validation: buildContextResponse.context.warnings expected array, got ${typeof ctx.warnings}`);
  }

  return raw as T;
}

/**
 * Validate a list-tools response: must have a `.tools` array.
 */
export function validateListToolsResponse<T>(raw: unknown): T {
  hasObjectShape(raw, 'listToolsResponse');
  const resp = raw as Record<string, unknown>;

  if (!Array.isArray(resp.tools)) {
    throw new Error(`sidecar bridge validation: listToolsResponse.tools expected array, got ${typeof resp.tools}`);
  }

  return raw as T;
}

/**
 * Validate an invoke-tool response: must have string `tool_name`,
 * `tool_call_id`, `status`, and an `audit` field.
 */
export function validateInvokeToolResponse<T>(raw: unknown): T {
  hasObjectShape(raw, 'invokeToolResponse');
  const resp = raw as Record<string, unknown>;

  requireStringField(resp, 'tool_name', 'invokeToolResponse');
  requireStringField(resp, 'tool_call_id', 'invokeToolResponse');
  requireStringField(resp, 'status', 'invokeToolResponse');

  if (resp.audit === undefined) {
    throw new Error('sidecar bridge validation: invokeToolResponse.audit is required');
  }

  return raw as T;
}

/**
 * Validate a cancel response: must have string `request_id`, `status`,
 * and boolean `accepted`.
 */
export function validateCancelResponse<T>(raw: unknown): T {
  hasObjectShape(raw, 'cancelResponse');
  const resp = raw as Record<string, unknown>;

  requireStringField(resp, 'request_id', 'cancelResponse');
  requireStringField(resp, 'status', 'cancelResponse');
  requireBooleanField(resp, 'accepted', 'cancelResponse');

  return raw as T;
}
