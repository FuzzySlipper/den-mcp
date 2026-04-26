import {
  formatDuration,
  subagentSucceeded,
  type SubagentResult,
} from "./den-subagent-runner.ts";
import {
  isSubagentInfrastructureFailure,
  type JsonObject,
} from "./den-subagent-pipeline.ts";

const SUBAGENT_PARENT_TOOL_RESULT_SCHEMA = "den_subagent_parent_tool_result";
const SUBAGENT_PARENT_TOOL_RESULT_SCHEMA_VERSION = 1;
const PARENT_TOOL_SUMMARY_MAX_CHARS = 1_200;
const PARENT_TOOL_DETAIL_PREVIEW_MAX_CHARS = 600;

type ParentSubagentState = "completed" | "failed" | "infrastructure_failed";

type BoundedText = {
  text: string;
  truncated: boolean;
  originalChars: number;
};

export function buildSubagentParentToolResult(result: SubagentResult) {
  const ok = subagentSucceeded(result);
  const state = subagentParentState(result);
  const finalSummary = boundedParentText(result.final_output || "(no assistant final output)", PARENT_TOOL_SUMMARY_MAX_CHARS);
  const failureSummary = ok ? undefined : boundedParentText(buildParentFailureSummary(result), PARENT_TOOL_SUMMARY_MAX_CHARS);

  const parentSummary = ok || !failureSummary ? finalSummary : failureSummary;
  return {
    content: [{ type: "text", text: formatParentToolResultText(result, state, parentSummary) }],
    details: buildSubagentParentToolDetails(result, state, finalSummary, failureSummary),
    isError: !ok,
  };
}

function subagentParentState(result: SubagentResult): ParentSubagentState {
  if (subagentSucceeded(result)) return "completed";
  return isSubagentInfrastructureFailure(result) ? "infrastructure_failed" : "failed";
}

function formatParentToolResultText(
  result: SubagentResult,
  state: ParentSubagentState,
  summary: BoundedText,
): string {
  const lines = [
    `Sub-agent ${state.replace(/_/g, " ")} (${result.role})`,
    `Run: ${result.run_id}`,
    result.task_id ? `Task: #${result.task_id}` : null,
    result.review_round_id ? `Review round: #${result.review_round_id}` : null,
    `Exit: ${result.exit_code}${result.signal ? ` (${result.signal})` : ""}`,
    `Duration: ${formatDuration(result.duration_ms)}`,
    result.model ? `Model: ${result.model}` : null,
    result.output_status ? `Output status: ${result.output_status}` : null,
    result.infrastructure_failure_reason ? `Infrastructure: ${formatInfrastructureFailureReason(result.infrastructure_failure_reason)}` : null,
    `Artifacts: ${result.artifacts.dir}`,
    result.artifacts.status_json_path ? `Status artifact: ${result.artifacts.status_json_path}` : null,
    result.artifacts.events_jsonl_path ? `Work-event artifact: ${result.artifacts.events_jsonl_path}` : null,
    result.pi_session_file_path ? `Pi session artifact: ${result.pi_session_file_path}` : null,
    !result.pi_session_file_path && result.pi_session_dir ? `Pi session dir: ${result.pi_session_dir}` : null,
    "",
    state === "completed" ? "Final summary (bounded parent copy):" : "Failure summary (bounded parent copy):",
    summary.text,
  ];
  return lines.filter((line): line is string => line !== null).join("\n");
}

function buildSubagentParentToolDetails(
  result: SubagentResult,
  state: ParentSubagentState,
  finalSummary: BoundedText,
  failureSummary: BoundedText | undefined,
): JsonObject {
  const childError = result.child_error_message
    ? boundedParentText(result.child_error_message, PARENT_TOOL_DETAIL_PREVIEW_MAX_CHARS)
    : undefined;

  return omitUndefined({
    schema: SUBAGENT_PARENT_TOOL_RESULT_SCHEMA,
    schema_version: SUBAGENT_PARENT_TOOL_RESULT_SCHEMA_VERSION,
    parent_context_contract: "bounded tool-result payload; full stdout/stderr/work events/session transcript stay in artifacts, Den AgentRun/run detail, and task-thread messages",
    run_id: result.run_id,
    role: result.role,
    task_id: result.task_id,
    review_round_id: result.review_round_id,
    workspace_id: result.workspace_id,
    worktree_path: result.worktree_path,
    branch: result.branch,
    base_branch: result.base_branch,
    base_commit: result.base_commit,
    head_commit: result.head_commit,
    purpose: result.purpose,
    state,
    ok: state === "completed",
    exit_code: result.exit_code,
    signal: result.signal,
    duration_ms: result.duration_ms,
    backend: result.backend,
    model: result.model,
    session_mode: result.session_mode,
    session: result.session,
    pi_session_id: result.pi_session_id,
    pi_session_persisted: result.pi_session_persisted,
    started_at: result.started_at,
    ended_at: result.ended_at,
    aborted: result.aborted,
    timeout_kind: result.timeout_kind,
    forced_kill: result.forced_kill,
    assistant_final_found: result.assistant_final_found,
    prompt_echo_detected: result.prompt_echo_detected,
    output_status: result.output_status,
    message_count: result.message_count,
    assistant_message_count: result.assistant_message_count,
    infrastructure_failure_reason: result.infrastructure_failure_reason,
    infrastructure_warning_reason: result.infrastructure_warning_reason,
    fallback_from_model: result.fallback_from_model,
    fallback_from_exit_code: result.fallback_from_exit_code,
    final_output_preview: finalSummary.text,
    final_output_chars: finalSummary.originalChars,
    final_output_truncated: finalSummary.truncated,
    failure_summary: failureSummary?.text,
    failure_summary_truncated: failureSummary?.truncated,
    child_error_preview: childError?.text,
    child_error_chars: childError?.originalChars,
    child_error_truncated: childError?.truncated,
    artifacts: compactSubagentArtifacts(result),
  });
}

function compactSubagentArtifacts(result: SubagentResult): JsonObject {
  return omitUndefined({
    dir: result.artifacts.dir,
    status_json_path: result.artifacts.status_json_path,
    events_jsonl_path: result.artifacts.events_jsonl_path,
    stdout_jsonl_path: result.artifacts.stdout_jsonl_path,
    stderr_log_path: result.artifacts.stderr_log_path,
    session_dir: result.artifacts.session_dir ?? result.pi_session_dir,
    session_file_path: result.artifacts.session_file_path ?? result.pi_session_file_path,
    session_id: result.artifacts.session_id ?? result.pi_session_id,
  });
}

function buildParentFailureSummary(result: SubagentResult): string {
  const lines = [formatFailureSummary(result)];
  if (result.child_error_message) {
    const childError = boundedParentText(oneLine(result.child_error_message), PARENT_TOOL_DETAIL_PREVIEW_MAX_CHARS);
    lines.push(`Child error: ${childError.text}`);
  }
  return lines.join("\n");
}

function boundedParentText(raw: string, maxChars: number): BoundedText {
  const text = (raw || "").trim() || "(empty)";
  if (text.length <= maxChars) {
    return { text, truncated: false, originalChars: raw.length };
  }

  const truncatedChars = text.length - maxChars;
  return {
    text: `${text.slice(0, maxChars).trimEnd()}\n\n[... ${truncatedChars} more characters omitted from the parent tool result; see Den run detail/artifacts for the full sub-agent output.]`,
    truncated: true,
    originalChars: raw.length,
  };
}

function formatFailureSummary(result: SubagentResult): string {
  if (result.infrastructure_failure_reason) {
    return `Sub-agent infrastructure failure: ${formatInfrastructureFailureReason(result.infrastructure_failure_reason)}.`;
  }
  if (result.output_status === "prompt_echo_only") {
    return "Sub-agent did not produce an assistant final answer; only prompt-echo-like output was observed.";
  }
  if (result.timeout_kind === "startup") return "Sub-agent timed out before emitting JSON output.";
  if (result.aborted) return "Sub-agent was aborted before producing a usable final answer.";
  if (result.timeout_kind === "terminal_drain") return "Sub-agent produced output but did not exit cleanly after the terminal message.";
  if (!result.assistant_final_found) return "Sub-agent did not produce an assistant final answer.";
  return `Sub-agent process exited ${result.exit_code}${result.signal ? ` (${result.signal})` : ""}.`;
}

function formatInfrastructureFailureReason(reason: string): string {
  switch (reason) {
    case "extension_load":
      return "Pi extension load failed";
    case "extension_runtime":
      return "Pi extension runtime error";
    case "child_error":
      return "child process error";
    case "forced_kill":
      return "forced process kill";
    default:
      return reason.replace(/_/g, " ");
  }
}

function oneLine(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}

function omitUndefined(value: Record<string, unknown>): JsonObject {
  const result: JsonObject = {};
  for (const [key, entry] of Object.entries(value)) {
    if (entry !== undefined) result[key] = entry;
  }
  return result;
}
