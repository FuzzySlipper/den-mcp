export type JsonObject = Record<string, unknown>;

export const SUBAGENT_RUN_SCHEMA = "den_subagent_run";
export const SUBAGENT_RUN_SCHEMA_VERSION = 1;

export type SubagentArtifacts = {
  dir: string;
  stdout_jsonl_path: string;
  stderr_log_path: string;
  status_json_path: string;
  events_jsonl_path: string;
};

export type SubagentRunContext = {
  reviewRoundId?: number;
  workspaceId?: string;
  worktreePath?: string;
  branch?: string;
  baseBranch?: string;
  baseCommit?: string;
  headCommit?: string;
  purpose?: string;
};

export type SubagentRunIdentity = {
  runId: string;
  role: string;
  taskId?: number;
  cwd?: string;
  backend: string;
  model?: string;
  tools?: string;
  sessionMode?: string;
  session?: string;
  rerunOfRunId?: string;
  artifacts?: SubagentArtifacts;
} & SubagentRunContext;

export type SubagentRunState =
  | "running"
  | "retrying"
  | "aborting"
  | "rerun_requested"
  | "rerun_accepted"
  | "complete"
  | "failed"
  | "timeout"
  | "aborted"
  | "unknown";

export type SubagentOpsEventType =
  | "subagent_started"
  | "subagent_process_started"
  | "subagent_heartbeat"
  | "subagent_assistant_output"
  | "subagent_prompt_echo_detected"
  | "subagent_fallback_started"
  | "subagent_abort_requested"
  | "subagent_rerun_requested"
  | "subagent_rerun_accepted"
  | "subagent_rerun_unavailable"
  | "subagent_completed"
  | "subagent_timeout"
  | "subagent_startup_timeout"
  | "subagent_terminal_drain_timeout"
  | "subagent_aborted"
  | "subagent_abort"
  | "subagent_failed"
  | "subagent_spawn_error"
  | "subagent_work_turn_start"
  | "subagent_work_turn_end"
  | "subagent_work_tool_start"
  | "subagent_work_tool_end"
  | "subagent_work_message_end";

export function buildSubagentRunMetadata(
  identity: SubagentRunIdentity,
  extra: JsonObject = {},
): JsonObject {
  return omitUndefined({
    schema: SUBAGENT_RUN_SCHEMA,
    schema_version: SUBAGENT_RUN_SCHEMA_VERSION,
    run_id: identity.runId,
    role: identity.role,
    task_id: identity.taskId ?? null,
    cwd: identity.cwd ?? null,
    backend: identity.backend,
    model: identity.model ?? null,
    tools: identity.tools ?? null,
    session_mode: identity.sessionMode ?? "fresh",
    session: identity.session ?? null,
    rerun_of_run_id: identity.rerunOfRunId ?? null,
    ...buildSubagentRunContextMetadata(identity),
    artifacts: identity.artifacts ?? null,
    ...extra,
  });
}

export function normalizeSubagentRunEvent(event: JsonObject): JsonObject {
  return omitUndefined({
    schema: SUBAGENT_RUN_SCHEMA,
    schema_version: SUBAGENT_RUN_SCHEMA_VERSION,
    ...event,
  });
}

export function buildSubagentRunContextMetadata(context: SubagentRunContext = {}): JsonObject {
  return {
    review_round_id: optionalPositiveInteger(context.reviewRoundId) ?? null,
    workspace_id: normalizeContextString(context.workspaceId) ?? null,
    worktree_path: normalizeContextString(context.worktreePath) ?? null,
    branch: normalizeContextString(context.branch) ?? null,
    base_branch: normalizeContextString(context.baseBranch) ?? null,
    base_commit: normalizeContextString(context.baseCommit) ?? null,
    head_commit: normalizeContextString(context.headCommit) ?? null,
    purpose: normalizeSubagentRunPurpose(context.purpose) ?? null,
  };
}

export function normalizeSubagentRunPurpose(value: unknown): string | undefined {
  const normalized = normalizeContextString(value);
  if (!normalized) return undefined;
  const purpose = normalized
    .toLowerCase()
    .replace(/[\s-]+/g, "_")
    .replace(/[^a-z0-9_.:]+/g, "_")
    .replace(/_+/g, "_")
    .replace(/^_+|_+$/g, "");
  return purpose ? purpose.slice(0, 80) : undefined;
}

export function subagentOpsEventTypeForEvent(eventType: string): SubagentOpsEventType | undefined {
  switch (eventType) {
    case "subagent.process_started":
      return "subagent_process_started";
    case "subagent.heartbeat":
      return "subagent_heartbeat";
    case "subagent.assistant_output":
      return "subagent_assistant_output";
    case "subagent.prompt_echo_detected":
      return "subagent_prompt_echo_detected";
    case "subagent.startup_timeout":
      return "subagent_startup_timeout";
    case "subagent.terminal_drain_timeout":
      return "subagent_terminal_drain_timeout";
    case "subagent.abort":
      return "subagent_abort";
    case "subagent.spawn_error":
      return "subagent_spawn_error";
    case "subagent.work_turn_start":
      return "subagent_work_turn_start";
    case "subagent.work_turn_end":
      return "subagent_work_turn_end";
    case "subagent.work_tool_start":
      return "subagent_work_tool_start";
    case "subagent.work_tool_end":
      return "subagent_work_tool_end";
    case "subagent.work_message_end":
      return "subagent_work_message_end";
    default:
      return undefined;
  }
}

export function subagentRunStateFromOpsEventType(eventType: string): SubagentRunState {
  switch (eventType) {
    case "subagent_started":
    case "subagent_process_started":
    case "subagent_heartbeat":
    case "subagent_assistant_output":
    case "subagent_prompt_echo_detected":
    case "subagent_work_turn_start":
    case "subagent_work_turn_end":
    case "subagent_work_tool_start":
    case "subagent_work_tool_end":
    case "subagent_work_message_end":
      return "running";
    case "subagent_fallback_started":
      return "retrying";
    case "subagent_abort_requested":
      return "aborting";
    case "subagent_rerun_requested":
      return "rerun_requested";
    case "subagent_rerun_accepted":
      return "rerun_accepted";
    case "subagent_rerun_unavailable":
      return "failed";
    case "subagent_completed":
      return "complete";
    case "subagent_timeout":
    case "subagent_startup_timeout":
    case "subagent_terminal_drain_timeout":
      return "timeout";
    case "subagent_aborted":
    case "subagent_abort":
      return "aborted";
    case "subagent_failed":
    case "subagent_spawn_error":
      return "failed";
    default:
      return "unknown";
  }
}

export type PiStdoutParseResult =
  | { kind: "json"; line: string; event: any }
  | { kind: "raw_stdout"; line: string };

export type SubagentOutputSnapshot = {
  finalOutput: string;
  model?: string;
  messageCount: number;
  assistantMessageCount: number;
  promptEchoDetected: boolean;
  childErrorMessage?: string;
};

export type SubagentOutputObserver = {
  appendEvent(event: JsonObject): Promise<void> | void;
};

export type SubagentOutputExtractor = {
  updateFromEvent(event: any): string | undefined;
  recordChildError(message: string): void;
  snapshot(): SubagentOutputSnapshot;
};

export type InfrastructureFailureLike = {
  aborted?: boolean;
  timeout_kind?: string;
  forced_kill?: boolean;
  signal?: string;
  child_error_message?: string;
  stderr?: string;
  stderr_tail?: string;
};

export type InfrastructureFailureReason =
  | "aborted"
  | "timeout"
  | "forced_kill"
  | "signal"
  | "child_error"
  | "extension_load"
  | "extension_runtime";

export function parsePiStdoutLine(line: string): PiStdoutParseResult | undefined {
  if (!line.trim()) return undefined;
  try {
    return { kind: "json", line: line.trim(), event: JSON.parse(line) };
  } catch {
    return { kind: "raw_stdout", line };
  }
}

export function normalizePiWorkEvent(event: any, now = Date.now()): JsonObject | undefined {
  if (!event || typeof event.type !== "string") return undefined;

  switch (event.type) {
    case "session":
      return omitUndefined({
        type: "subagent.work_session",
        ts: eventTimestamp(event, now),
        source_type: event.type,
        session_id: normalizeString(event.id),
        cwd: normalizeString(event.cwd),
        version: finiteNumber(event.version),
      });
    case "agent_start":
      return {
        type: "subagent.work_agent_start",
        ts: eventTimestamp(event, now),
        source_type: event.type,
      };
    case "turn_start":
      return {
        type: "subagent.work_turn_start",
        ts: eventTimestamp(event, now),
        source_type: event.type,
      };
    case "turn_end":
      return omitUndefined({
        type: "subagent.work_turn_end",
        ts: eventTimestamp(event, now),
        source_type: event.type,
        ...summarizeAssistantMessage(event.message),
      });
    case "message_start":
      return normalizePiMessageWorkEvent(event, "start", now);
    case "message_update":
      return normalizePiMessageWorkEvent(event, "update", now);
    case "message_end":
      return normalizePiMessageWorkEvent(event, "end", now);
    case "tool_execution_start":
      return normalizePiToolWorkEvent(event, "start", now);
    case "tool_execution_update":
      return normalizePiToolWorkEvent(event, "update", now);
    case "tool_execution_end":
      return normalizePiToolWorkEvent(event, "end", now);
    default:
      return undefined;
  }
}

function normalizePiMessageWorkEvent(event: any, phase: "start" | "update" | "end", now: number): JsonObject | undefined {
  const message = event.message;
  const role = normalizeString(message?.role);
  if (role !== "assistant") return undefined;

  const messageSummary = summarizeAssistantMessage(message);
  const updateKind = normalizeString(event.assistantMessageEvent?.type);
  if (phase === "update" && !messageSummary.text_preview && !messageSummary.tool_calls) return undefined;

  return omitUndefined({
    type: `subagent.work_message_${phase}`,
    ts: eventTimestamp(event, now),
    source_type: event.type,
    role,
    provider: normalizeString(message?.provider),
    model: normalizeString(message?.model),
    update_kind: updateKind,
    ...messageSummary,
  });
}

function normalizePiToolWorkEvent(event: any, phase: "start" | "update" | "end", now: number): JsonObject | undefined {
  const toolName = normalizeString(event.toolName ?? event.tool_name);
  if (!toolName) return undefined;
  const result = event.result ?? event.partialResult ?? event.partial_result;
  if (phase === "update" && !hasMeaningfulToolResult(result)) return undefined;
  const resultPreview = boundedPreview(result, 500);
  const isError = typeof event.isError === "boolean" ? event.isError : typeof event.is_error === "boolean" ? event.is_error : undefined;

  return omitUndefined({
    type: `subagent.work_tool_${phase}`,
    ts: eventTimestamp(event, now),
    source_type: event.type,
    tool_call_id: normalizeString(event.toolCallId ?? event.tool_call_id),
    tool_name: toolName,
    args_preview: boundedPreview(event.args, 500),
    result_preview: resultPreview,
    is_error: isError,
  });
}

export function createSubagentOutputExtractor(
  prompt: string,
  observer?: SubagentOutputObserver,
): SubagentOutputExtractor {
  let finalOutput = "";
  let model: string | undefined;
  let childErrorMessage: string | undefined;
  let messageCount = 0;
  let assistantMessageCount = 0;
  let promptEchoDetected = false;

  return {
    updateFromEvent(event: any): string | undefined {
      const message = event.message;
      if (!message) return undefined;
      if (event.type !== "message_end" && event.type !== "tool_result_end") return undefined;
      messageCount++;
      const text = extractText(message);
      if (message.errorMessage && typeof message.errorMessage === "string") childErrorMessage = message.errorMessage;
      if (message.role !== "assistant") return undefined;

      assistantMessageCount++;
      if (message.model && typeof message.model === "string") model = message.model;
      if (!text) return undefined;
      const terminalAssistantMessage = event.type === "message_end" && isTerminalAssistantMessage(message);
      if (!terminalAssistantMessage) return undefined;

      if (isPromptEcho(text, prompt)) {
        promptEchoDetected = true;
        void observer?.appendEvent({
          type: "subagent.prompt_echo_detected",
          ts: Date.now(),
          chars: text.length,
          terminal: true,
        });
        return undefined;
      }

      finalOutput = text;
      void observer?.appendEvent({
        type: "subagent.assistant_output",
        ts: Date.now(),
        chars: text.length,
        terminal: true,
      });
      return text;
    },
    recordChildError(message: string) {
      childErrorMessage = message;
    },
    snapshot() {
      return {
        finalOutput,
        model,
        messageCount,
        assistantMessageCount,
        promptEchoDetected,
        childErrorMessage,
      };
    },
  };
}

export function isSubagentInfrastructureFailure(result: InfrastructureFailureLike): boolean {
  return Boolean(classifySubagentInfrastructureFailure(result));
}

export function classifySubagentInfrastructureFailure(
  result: InfrastructureFailureLike,
): InfrastructureFailureReason | undefined {
  if (result.aborted) return "aborted";
  if (result.timeout_kind) return "timeout";
  if (result.forced_kill) return "forced_kill";
  if (result.signal) return "signal";
  if (result.child_error_message) return "child_error";

  const stderr = `${result.stderr_tail ?? ""}\n${result.stderr ?? ""}`;
  return classifySubagentStderrIssue(stderr);
}

export function classifySubagentStderrIssue(stderr: string): InfrastructureFailureReason | undefined {
  if (isExtensionLoadFailure(stderr)) return "extension_load";
  if (isExtensionRuntimeFailure(stderr)) return "extension_runtime";
  return undefined;
}

function isExtensionLoadFailure(stderr: string): boolean {
  return /Failed to load extension/i.test(stderr)
    || /Extension does not export a valid factory function/i.test(stderr);
}

function isExtensionRuntimeFailure(stderr: string): boolean {
  return /Extension error \([^)]+\):/i.test(stderr)
    || /This extension ctx is stale after session replacement or reload/i.test(stderr);
}

export function isTerminalAssistantMessage(message: any): boolean {
  if (!message || message.role !== "assistant") return false;
  if (!extractText(message)) return false;
  const stopReason = String(message.stopReason ?? message.stop_reason ?? "");
  if (stopReason && stopReason !== "stop" && stopReason !== "end_turn") return false;
  if (!Array.isArray(message.content)) return true;
  return !message.content.some((part: any) => part?.type === "toolCall" || part?.type === "tool_call");
}

export function extractText(message: any): string | undefined {
  if (!Array.isArray(message.content)) return undefined;
  for (let i = message.content.length - 1; i >= 0; i--) {
    const part = message.content[i];
    if (part?.type === "text" && typeof part.text === "string") return part.text;
  }
  return undefined;
}

function summarizeAssistantMessage(message: any): JsonObject {
  if (!message || message.role !== "assistant") return {};
  const text = extractText(message);
  const toolCalls = extractToolCallSummaries(message);
  return omitUndefined({
    text_preview: boundedPreview(text, 240),
    text_chars: typeof text === "string" ? text.length : undefined,
    content_types: extractContentTypes(message),
    tool_calls: toolCalls.length > 0 ? toolCalls : undefined,
    stop_reason: normalizeString(message.stopReason ?? message.stop_reason),
  });
}

function extractContentTypes(message: any): string[] | undefined {
  if (!Array.isArray(message?.content)) return undefined;
  const types = Array.from(new Set(message.content
    .map((part: any) => normalizeString(part?.type))
    .filter((type: string | undefined): type is string => Boolean(type))));
  return types.length > 0 ? types : undefined;
}

function extractToolCallSummaries(message: any): JsonObject[] {
  if (!Array.isArray(message?.content)) return [];
  return message.content
    .filter((part: any) => part?.type === "toolCall" || part?.type === "tool_call")
    .slice(0, 8)
    .map((part: any) => omitUndefined({
      id: normalizeString(part.id ?? part.toolCallId ?? part.tool_call_id),
      name: normalizeString(part.name ?? part.toolName ?? part.tool_name),
      args_preview: boundedPreview(part.arguments ?? part.args, 300),
    }));
}

function hasMeaningfulToolResult(value: unknown): boolean {
  if (value === undefined || value === null) return false;
  if (typeof value !== "object") return true;
  const content = (value as { content?: unknown }).content;
  if (Array.isArray(content)) return content.length > 0;
  return true;
}

function eventTimestamp(event: any, fallback: number): number {
  const candidates = [
    event?.ts,
    event?.timestamp,
    event?.message?.timestamp,
    event?.assistantMessageEvent?.timestamp,
    event?.assistantMessageEvent?.partial?.timestamp,
  ];
  for (const candidate of candidates) {
    if (typeof candidate === "number" && Number.isFinite(candidate)) return candidate;
    if (typeof candidate === "string") {
      const parsed = Date.parse(candidate);
      if (Number.isFinite(parsed)) return parsed;
    }
  }
  return fallback;
}

function finiteNumber(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function normalizeString(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const trimmed = value.trim();
  return trimmed ? trimmed : undefined;
}

function optionalPositiveInteger(value: unknown): number | undefined {
  return typeof value === "number" && Number.isInteger(value) && value > 0 ? value : undefined;
}

function normalizeContextString(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const oneLineValue = value.replace(/\s+/g, " ").trim();
  if (!oneLineValue) return undefined;
  return oneLineValue.length <= 500 ? oneLineValue : oneLineValue.slice(0, 500);
}

function boundedPreview(value: unknown, maxChars: number): string | undefined {
  if (value === undefined || value === null) return undefined;
  const raw = typeof value === "string" ? value : safeJson(value);
  const oneLineValue = raw.replace(/\s+/g, " ").trim();
  if (!oneLineValue) return undefined;
  return oneLineValue.length <= maxChars ? oneLineValue : `${oneLineValue.slice(0, Math.max(0, maxChars - 1))}…`;
}

function safeJson(value: unknown): string {
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

export function isPromptEcho(text: string, prompt: string): boolean {
  const normalizedText = normalizeForEchoDetection(text);
  const normalizedPrompt = normalizeForEchoDetection(prompt);
  if (!normalizedText || !normalizedPrompt) return false;
  if (normalizedText === normalizedPrompt) return true;
  const prefix = normalizedPrompt.slice(0, Math.min(normalizedPrompt.length, 500));
  return prefix.length > 80 && normalizedText.includes(prefix);
}

function omitUndefined(value: JsonObject): JsonObject {
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined));
}

function normalizeForEchoDetection(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}
