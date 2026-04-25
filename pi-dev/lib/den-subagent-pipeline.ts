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
  artifacts?: SubagentArtifacts;
};

export type SubagentRunState =
  | "running"
  | "retrying"
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
  | "subagent_completed"
  | "subagent_timeout"
  | "subagent_startup_timeout"
  | "subagent_terminal_drain_timeout"
  | "subagent_aborted"
  | "subagent_abort"
  | "subagent_failed"
  | "subagent_spawn_error";

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
      return "running";
    case "subagent_fallback_started":
      return "retrying";
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

      if (isPromptEcho(text, prompt)) {
        promptEchoDetected = true;
        void observer?.appendEvent({
          type: "subagent.prompt_echo_detected",
          ts: Date.now(),
          chars: text.length,
          terminal: isTerminalAssistantMessage(message),
        });
        return undefined;
      }

      finalOutput = text;
      void observer?.appendEvent({
        type: "subagent.assistant_output",
        ts: Date.now(),
        chars: text.length,
        terminal: isTerminalAssistantMessage(message),
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
