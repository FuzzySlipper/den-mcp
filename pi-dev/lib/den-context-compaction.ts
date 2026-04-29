export const DEN_CONTEXT_COMPACTION_SCHEMA = "den_context_compaction_request";
export const DEN_CONTEXT_COMPACTION_SCHEMA_VERSION = 1;

export type DenContextCompactionRequest = {
  durableContextPosted?: boolean;
  customInstructions?: string | null;
  safePointNotes?: string | null;
};

export type DenContextCompactionResult = {
  schema: typeof DEN_CONTEXT_COMPACTION_SCHEMA;
  schema_version: typeof DEN_CONTEXT_COMPACTION_SCHEMA_VERSION;
  requested: boolean;
  status: "requested" | "blocked" | "unavailable" | "failed";
  reason: string;
  custom_instructions: string | null;
  safe_point_notes: string | null;
  guardrails: string[];
};

export function defaultConductorCompactionInstructions(): string {
  return [
    "Preserve durable Den workflow state: current task(s), branch/head commits, review status, tests run, decisions, open findings, blockers, and next steps.",
    "Preserve user preferences and architectural/product decisions that affect upcoming work.",
    "Omit low-level tool-call minutiae unless needed to understand unresolved risk or dirty state.",
  ].join(" ");
}

export function requestDenContextCompaction(ctx: any, request: DenContextCompactionRequest): DenContextCompactionResult {
  const customInstructions = normalizeOptionalString(request.customInstructions) ?? defaultConductorCompactionInstructions();
  const safePointNotes = normalizeOptionalString(request.safePointNotes) ?? null;
  const guardrails = compactionGuardrails();

  if (request.durableContextPosted !== true) {
    return {
      schema: DEN_CONTEXT_COMPACTION_SCHEMA,
      schema_version: DEN_CONTEXT_COMPACTION_SCHEMA_VERSION,
      requested: false,
      status: "blocked",
      reason: "Refusing to compact until the conductor confirms durable Den context has been posted or is already up to date.",
      custom_instructions: customInstructions,
      safe_point_notes: safePointNotes,
      guardrails,
    };
  }

  if (typeof ctx?.compact !== "function") {
    return {
      schema: DEN_CONTEXT_COMPACTION_SCHEMA,
      schema_version: DEN_CONTEXT_COMPACTION_SCHEMA_VERSION,
      requested: false,
      status: "unavailable",
      reason: "This Pi runtime context does not expose ctx.compact(). Ask the user to run /compact or use a Pi RPC/session entrypoint that supports compaction.",
      custom_instructions: customInstructions,
      safe_point_notes: safePointNotes,
      guardrails,
    };
  }

  try {
    ctx.compact({
      customInstructions,
      onComplete: () => ctx?.ui?.notify?.("Den conductor context compaction completed.", "info"),
      onError: (error: unknown) => ctx?.ui?.notify?.(`Den conductor context compaction failed: ${errorMessage(error)}`, "error"),
    });
    return {
      schema: DEN_CONTEXT_COMPACTION_SCHEMA,
      schema_version: DEN_CONTEXT_COMPACTION_SCHEMA_VERSION,
      requested: true,
      status: "requested",
      reason: "Compaction was requested for the current Pi session. Pi runs compaction asynchronously and will emit compaction events/results through the normal session UI/events.",
      custom_instructions: customInstructions,
      safe_point_notes: safePointNotes,
      guardrails,
    };
  } catch (error) {
    return {
      schema: DEN_CONTEXT_COMPACTION_SCHEMA,
      schema_version: DEN_CONTEXT_COMPACTION_SCHEMA_VERSION,
      requested: false,
      status: "failed",
      reason: `Compaction request failed before it could start: ${errorMessage(error)}`,
      custom_instructions: customInstructions,
      safe_point_notes: safePointNotes,
      guardrails,
    };
  }
}

export function formatDenContextCompactionResult(result: DenContextCompactionResult): string {
  const lines = [
    `Context compaction: ${result.status}`,
    `Requested: ${result.requested ? "yes" : "no"}`,
    `Reason: ${result.reason}`,
    `Instructions: ${result.custom_instructions ?? "(none)"}`,
  ];
  if (result.safe_point_notes) lines.push(`Safe point notes: ${result.safe_point_notes}`);
  lines.push("Guardrails:");
  for (const guardrail of result.guardrails) lines.push(`- ${guardrail}`);
  return lines.join("\n");
}

export function buildDenContextCompactionToolResult(result: DenContextCompactionResult) {
  return {
    content: [{ type: "text", text: formatDenContextCompactionResult(result) }],
    details: result,
    isError: !result.requested,
  };
}

export function compactionGuardrails(): string[] {
  return [
    "Post or verify durable Den handoff/status context before compacting.",
    "Prefer task boundaries or just after a merge/review handoff; avoid mid-critical merge, review, or unresolved user-decision points.",
    "After compaction, re-check Den task/messages before starting the next substantial task.",
  ];
}

function normalizeOptionalString(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
