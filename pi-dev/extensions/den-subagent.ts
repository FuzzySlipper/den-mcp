import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { appendFile, mkdir, readFile, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import type { ExtensionAPI } from "@mariozechner/pi-coding-agent";
import { Type } from "typebox";
import {
  createSubagentOutputExtractor,
  isSubagentInfrastructureFailure,
  isTerminalAssistantMessage,
  parsePiStdoutLine,
  type JsonObject,
} from "./den-subagent-pipeline.ts";

type DenConfig = {
  baseUrl: string;
  projectId: string;
  agent: string;
  role: string;
  instanceId: string;
};

type RunOptions = {
  role: string;
  prompt: string;
  taskId?: number;
  sessionMode?: "fresh" | "continue" | "fork" | "session";
  session?: string;
  model?: string;
  fallbackModel?: string;
  tools?: string;
  cwd?: string;
  postResult?: boolean;
};

type SubagentResult = {
  run_id: string;
  role: string;
  task_id?: number;
  session_mode: string;
  session?: string;
  exit_code: number;
  signal?: string;
  pid?: number;
  backend: string;
  started_at: string;
  ended_at: string;
  duration_ms: number;
  aborted: boolean;
  timeout_kind?: "startup" | "terminal_drain";
  forced_kill: boolean;
  final_output: string;
  assistant_final_found: boolean;
  prompt_echo_detected: boolean;
  output_status: "assistant_final" | "prompt_echo_only" | "no_assistant_final";
  stderr: string;
  stderr_tail: string;
  model?: string;
  message_count: number;
  assistant_message_count: number;
  child_error_message?: string;
  artifacts: SubagentArtifacts;
  fallback_from_model?: string;
  fallback_from_exit_code?: number;
};

type SubagentArtifacts = {
  dir: string;
  stdout_jsonl_path: string;
  stderr_log_path: string;
  status_json_path: string;
  events_jsonl_path: string;
};

type SubagentBackendInput = {
  cfg: DenConfig;
  options: RunOptions;
  cwd: string;
  runId: string;
  recorder: SubagentRunRecorder;
  startedAt: string;
  signal: AbortSignal | undefined;
  onUpdate: ((partial: string) => void) | undefined;
};

type SubagentBackend = {
  name: string;
  run(input: SubagentBackendInput): Promise<SubagentResult>;
};

type SubagentRunRecorder = {
  artifacts: SubagentArtifacts;
  writeStatus(payload: JsonObject): Promise<void>;
  appendEvent(event: JsonObject): Promise<void>;
  appendStdoutLine(line: string): Promise<void>;
  appendRawStdout(line: string): Promise<void>;
  appendStderr(text: string): Promise<void>;
};

type DenExtensionConfig = {
  version?: number;
  fallback_model?: string;
  subagents?: Record<string, {
    model?: string;
    tools?: string;
  }>;
};

type ConfigScope = "project" | "global";

const DEFAULT_BASE_URL = "http://192.168.1.10:5199";
const CODER_PROMPT_SLUG = "pi-coder-subagent-prompt";
const REVIEWER_PROMPT_SLUG = "pi-reviewer-subagent-prompt";
const GLOBAL_SUFFIX = "-default";
const DEN_CONFIG_FILENAME = "den-config.json";
const DEFAULT_STARTUP_TIMEOUT_MS = 60_000;
const DEFAULT_FINAL_DRAIN_MS = 5_000;
const DEFAULT_FORCE_KILL_MS = 5_000;

export default function denSubagent(pi: ExtensionAPI) {
  pi.registerCommand("den-config", {
    description: "Configure Den Pi integration settings, including sub-agent role defaults.",
    handler: async (_args, ctx) => {
      await runDenConfigCommand(ctx);
    },
  });

  pi.registerCommand("den-run-subagent", {
    description: "Run a Pi sub-agent. Usage: /den-run-subagent [--continue|--fork <session>|--session <session>] <role> <task_id|-> <prompt>",
    handler: async (args, ctx) => {
      const cfg = await resolveConfig(ctx);
      const options = parseRunCommand(args, ctx.cwd);
      const result = await runDenSubagent(cfg, options, ctx.cwd, undefined, undefined);
      ctx.ui.setWidget("den-subagent", formatResultLines(result));
      ctx.ui.notify(formatResultLines(result).join("\n"), subagentSucceeded(result) ? "info" : "error");
    },
  });

  pi.registerCommand("den-run-coder", {
    description: "Run a coder sub-agent using the Den-managed coder prompt. Usage: /den-run-coder [--continue|--fork <session>|--session <session>] <task_id> [extra notes]",
    handler: async (args, ctx) => {
      const cfg = await resolveConfig(ctx);
      const parsed = parseTaskWrapperCommand(args, "coder");
      const result = await runPromptedSubagent(cfg, "coder", parsed, ctx.cwd, undefined, undefined);
      ctx.ui.setWidget("den-subagent", formatResultLines(result));
      ctx.ui.notify(formatResultLines(result).join("\n"), subagentSucceeded(result) ? "info" : "error");
    },
  });

  pi.registerCommand("den-run-reviewer", {
    description: "Run a reviewer sub-agent using the Den-managed reviewer prompt. Usage: /den-run-reviewer [--fork <session>|--session <session>] <task_id> [review target/notes]",
    handler: async (args, ctx) => {
      const cfg = await resolveConfig(ctx);
      const parsed = parseTaskWrapperCommand(args, "reviewer");
      const result = await runPromptedSubagent(cfg, "reviewer", parsed, ctx.cwd, undefined, undefined);
      ctx.ui.setWidget("den-subagent", formatResultLines(result));
      ctx.ui.notify(formatResultLines(result).join("\n"), subagentSucceeded(result) ? "info" : "error");
    },
  });

  pi.registerTool({
    name: "den_run_subagent",
    label: "Den Run Subagent",
    description: "Run a fresh Pi sub-agent for bounded coder/reviewer/planner work, record Den ops, and optionally post the final output to a task thread.",
    parameters: Type.Object({
      role: Type.String({ description: "Sub-agent role, e.g. coder, reviewer, planner." }),
      prompt: Type.String({ description: "Bounded task prompt for the fresh Pi run." }),
      task_id: Type.Optional(Type.Number({ description: "Optional Den task ID to link ops and result messages." })),
      model: Type.Optional(Type.String({ description: "Optional Pi model pattern/provider id for the sub-agent." })),
      tools: Type.Optional(Type.String({ description: "Optional comma-separated Pi tool allowlist. Defaults by role." })),
      cwd: Type.Optional(Type.String({ description: "Optional working directory. Defaults to current Pi cwd." })),
      post_result: Type.Optional(Type.Boolean({ description: "Post final output back to Den task thread. Default true when task_id is provided." })),
      session_mode: Type.Optional(Type.String({ description: "fresh, continue, fork, or session. Defaults to fresh." })),
      session: Type.Optional(Type.String({ description: "Session path/id for fork or session modes." })),
    }),
    async execute(_toolCallId, params, signal, onUpdate, ctx) {
      const cfg = await resolveConfig(ctx);
      const result = await runDenSubagent(
        cfg,
        {
          role: params.role,
          prompt: params.prompt,
          taskId: optionalNumber(params.task_id),
          sessionMode: parseSessionMode(normalizeString(params.session_mode)),
          session: normalizeString(params.session),
          model: normalizeString(params.model),
          tools: normalizeString(params.tools),
          cwd: normalizeString(params.cwd),
          postResult: typeof params.post_result === "boolean" ? params.post_result : undefined,
        },
        ctx.cwd,
        signal,
        (partial) => {
          onUpdate?.({
            content: [{ type: "text", text: partial || "(sub-agent running...)" }],
            details: { role: params.role, task_id: optionalNumber(params.task_id) },
          });
        },
      );
      return resultTool(result);
    },
  });

  pi.registerTool({
    name: "den_run_coder",
    label: "Den Run Coder",
    description: "Load the Den-managed coder prompt template for a task and run a coder sub-agent.",
    parameters: Type.Object({
      task_id: Type.Number({ description: "Den task ID." }),
      extra_notes: Type.Optional(Type.String({ description: "Optional extra conductor notes." })),
      model: Type.Optional(Type.String({ description: "Optional Pi model pattern/provider id." })),
      tools: Type.Optional(Type.String({ description: "Optional comma-separated Pi tool allowlist." })),
      cwd: Type.Optional(Type.String({ description: "Optional working directory." })),
      post_result: Type.Optional(Type.Boolean({ description: "Post final output back to Den task thread. Default true." })),
      session_mode: Type.Optional(Type.String({ description: "fresh, continue, fork, or session. Defaults to fresh." })),
      session: Type.Optional(Type.String({ description: "Session path/id for fork or session modes." })),
    }),
    async execute(_toolCallId, params, signal, onUpdate, ctx) {
      const cfg = await resolveConfig(ctx);
      const result = await runPromptedSubagent(
        cfg,
        "coder",
        {
          taskId: params.task_id,
          extraNotes: normalizeString(params.extra_notes),
          model: normalizeString(params.model),
          tools: normalizeString(params.tools),
          cwd: normalizeString(params.cwd),
          postResult: typeof params.post_result === "boolean" ? params.post_result : undefined,
          sessionMode: parseSessionMode(normalizeString(params.session_mode)),
          session: normalizeString(params.session),
        },
        ctx.cwd,
        signal,
        onUpdateText(onUpdate, "coder", params.task_id),
      );
      return resultTool(result);
    },
  });

  pi.registerTool({
    name: "den_run_reviewer",
    label: "Den Run Reviewer",
    description: "Load the Den-managed reviewer prompt template for a task and run a reviewer sub-agent.",
    parameters: Type.Object({
      task_id: Type.Number({ description: "Den task ID." }),
      review_target: Type.Optional(Type.String({ description: "Branch, diff range, commit, or review target notes." })),
      model: Type.Optional(Type.String({ description: "Optional Pi model pattern/provider id." })),
      tools: Type.Optional(Type.String({ description: "Optional comma-separated Pi tool allowlist." })),
      cwd: Type.Optional(Type.String({ description: "Optional working directory." })),
      post_result: Type.Optional(Type.Boolean({ description: "Post final output back to Den task thread. Default true." })),
      session_mode: Type.Optional(Type.String({ description: "fresh, continue, fork, or session. Defaults to fresh." })),
      session: Type.Optional(Type.String({ description: "Session path/id for fork or session modes." })),
    }),
    async execute(_toolCallId, params, signal, onUpdate, ctx) {
      const cfg = await resolveConfig(ctx);
      const result = await runPromptedSubagent(
        cfg,
        "reviewer",
        {
          taskId: params.task_id,
          reviewTarget: normalizeString(params.review_target),
          model: normalizeString(params.model),
          tools: normalizeString(params.tools),
          cwd: normalizeString(params.cwd),
          postResult: typeof params.post_result === "boolean" ? params.post_result : undefined,
          sessionMode: parseSessionMode(normalizeString(params.session_mode)),
          session: normalizeString(params.session),
        },
        ctx.cwd,
        signal,
        onUpdateText(onUpdate, "reviewer", params.task_id),
      );
      return resultTool(result);
    },
  });
}

async function runDenConfigCommand(ctx: any) {
  if (!ctx.hasUI) {
    throw new Error("/den-config requires an interactive Pi UI.");
  }

  while (true) {
    const current = await loadMergedDenExtensionConfig(ctx.cwd);
    const projectPath = denConfigPath("project", ctx.cwd);
    const globalPath = denConfigPath("global", ctx.cwd);
    const choice = await ctx.ui.select("Den config", [
      `Sub-agent defaults (project-local: ${projectPath})`,
      `Sub-agent defaults (global: ${globalPath})`,
      `Fallback model (project-local: ${projectPath})`,
      `Fallback model (global: ${globalPath})`,
      "View current config",
      "Done",
    ]);

    if (!choice || choice === "Done") return;
    if (choice === "View current config") {
      ctx.ui.setWidget("den-config", formatConfigPreview(current, projectPath, globalPath));
      continue;
    }

    const scope: ConfigScope = choice.includes("project-local") ? "project" : "global";
    if (choice.startsWith("Fallback model")) {
      await configureFallbackModel(ctx, scope);
      continue;
    }
    await configureSubagentDefaults(ctx, scope);
  }
}

async function configureFallbackModel(ctx: any, scope: ConfigScope) {
  const model = await selectModel(ctx, `${scope} fallback model`);
  if (model === undefined) return;

  const config = await loadDenExtensionConfig(scope, ctx.cwd);
  if (model === null) delete config.fallback_model;
  else config.fallback_model = model;

  await saveDenExtensionConfig(scope, ctx.cwd, {
    ...config,
    version: 1,
  });
  ctx.ui.notify(
    model === null
      ? `Cleared ${scope} fallback model`
      : `Saved ${scope} fallback model: ${model}`,
    "info",
  );
}

async function configureSubagentDefaults(ctx: any, scope: ConfigScope) {
  const roleChoices = ["coder", "reviewer", "planner"];
  const current = await loadDenExtensionConfig(scope, ctx.cwd);
  const role = await ctx.ui.select(
    `Configure ${scope} sub-agent role`,
    roleChoices.map((candidate) => `${candidate}${current.subagents?.[candidate]?.model ? ` — ${current.subagents[candidate].model}` : ""}`),
  );
  if (!role) return;
  const roleName = role.split(" — ")[0];

  const model = await selectModel(ctx, `default model for ${roleName}`);
  if (model === undefined) return;

  const config = await loadDenExtensionConfig(scope, ctx.cwd);
  const subagents = { ...(config.subagents ?? {}) };
  const roleConfig = { ...(subagents[roleName] ?? {}) };
  if (model === null) delete roleConfig.model;
  else roleConfig.model = model;
  if (Object.keys(roleConfig).length === 0) delete subagents[roleName];
  else subagents[roleName] = roleConfig;

  await saveDenExtensionConfig(scope, ctx.cwd, {
    ...config,
    version: 1,
    subagents,
  });
  ctx.ui.notify(
    model === null
      ? `Cleared ${scope} default model for ${roleName}`
      : `Saved ${scope} ${roleName} model: ${model}`,
    "info",
  );
}

async function selectModel(ctx: any, label: string): Promise<string | null | undefined> {
  const models = await availableModels(ctx);
  const modelChoices = models.map((model) => ({
    id: providerQualifiedModelId(model),
    label: `${providerQualifiedModelId(model)}${model.name && model.name !== model.id ? ` — ${model.name}` : ""}`,
  }));
  const choices = [
    ...modelChoices.map((model) => model.label),
    "Clear configured model",
    "Cancel",
  ];
  const choice = await ctx.ui.select(`Select ${label}`, choices);
  if (!choice || choice === "Cancel") return undefined;
  if (choice === "Clear configured model") return null;
  return modelChoices.find((model) => model.label === choice)?.id;
}

async function availableModels(ctx: any): Promise<any[]> {
  if (ctx.modelRegistry?.getAvailable) {
    const available = await ctx.modelRegistry.getAvailable();
    if (Array.isArray(available) && available.length > 0) return sortModels(available);
  }
  const current = ctx.model ? [ctx.model] : [];
  return sortModels(current);
}

function sortModels(models: any[]): any[] {
  return [...models].sort((a, b) => providerQualifiedModelId(a).localeCompare(providerQualifiedModelId(b)));
}

function providerQualifiedModelId(model: any): string {
  const provider = String(model.provider ?? "").trim();
  const id = String(model.id ?? model.model ?? "").trim();
  return provider && id ? `${provider}/${id}` : id || provider;
}

function formatConfigPreview(config: DenExtensionConfig, projectPath: string, globalPath: string): string[] {
  const lines = [
    "Den config",
    `Project config: ${projectPath}`,
    `Global config: ${globalPath}`,
    "",
    `Fallback model: ${config.fallback_model ?? "(not configured)"}`,
    "",
    "Sub-agent defaults:",
  ];
  for (const role of ["coder", "reviewer", "planner"]) {
    const roleConfig = config.subagents?.[role];
    lines.push(`- ${role}: ${roleConfig?.model ?? "(not configured)"}`);
  }
  return lines;
}

async function runPromptedSubagent(
  cfg: DenConfig,
  role: "coder" | "reviewer",
  options: {
    taskId: number;
    extraNotes?: string;
    reviewTarget?: string;
    model?: string;
    tools?: string;
    cwd?: string;
    postResult?: boolean;
    sessionMode?: "fresh" | "continue" | "fork" | "session";
    session?: string;
  },
  defaultCwd: string,
  signal: AbortSignal | undefined,
  onUpdate: ((partial: string) => void) | undefined,
) {
  const task = await getTask(cfg, options.taskId);
  const promptDoc = await resolvePromptDoc(cfg, role === "coder" ? CODER_PROMPT_SLUG : REVIEWER_PROMPT_SLUG);
  const prompt = renderTemplate(promptDoc.content, {
    project_id: cfg.projectId,
    task_id: String(options.taskId),
    task_title: String(task.task?.title ?? task.title ?? ""),
    task_description: String(task.task?.description ?? task.description ?? "(no task description)"),
    task_context: summarizeTaskContext(task),
    review_target: options.reviewTarget ?? options.extraNotes ?? "(no review target provided)",
    extra_notes: options.extraNotes ?? "",
    role,
  });

  return runDenSubagent(
    cfg,
    {
      role,
      prompt,
      taskId: options.taskId,
      model: options.model,
      tools: options.tools,
      cwd: options.cwd,
      postResult: options.postResult ?? true,
      sessionMode: role === "reviewer" && !options.sessionMode ? "fresh" : options.sessionMode,
      session: options.session,
    },
    defaultCwd,
    signal,
    onUpdate,
  );
}

async function runDenSubagent(
  cfg: DenConfig,
  options: RunOptions,
  defaultCwd: string,
  signal: AbortSignal | undefined,
  onUpdate: ((partial: string) => void) | undefined,
): Promise<SubagentResult> {
  const cwd = options.cwd ? path.resolve(options.cwd) : defaultCwd;
  const effectiveOptions = await applyConfiguredDefaults(options, cwd);
  const runId = makeRunId(cfg, effectiveOptions);
  const backend = createSubagentBackend();
  const recorder = await createRunRecorder(runId);
  const artifacts = recorder.artifacts;
  const startedAt = new Date().toISOString();
  await recorder.writeStatus({
    state: "starting",
    run_id: runId,
    role: effectiveOptions.role,
    task_id: effectiveOptions.taskId ?? null,
    backend: backend.name,
    cwd,
    started_at: startedAt,
  });
  await appendOps(cfg, "subagent_started", {
    taskId: effectiveOptions.taskId,
    body: `Started ${effectiveOptions.role} sub-agent${effectiveOptions.taskId ? ` for task #${effectiveOptions.taskId}` : ""}.`,
    metadata: {
      run_id: runId,
      role: effectiveOptions.role,
      cwd,
      backend: backend.name,
      model: effectiveOptions.model ?? null,
      tools: effectiveOptions.tools ?? defaultToolsForRole(effectiveOptions.role),
      session_mode: effectiveOptions.sessionMode ?? "fresh",
      session: effectiveOptions.session ?? null,
      started_at: startedAt,
      artifacts,
    },
  });

  let result = await backend.run({
    cfg,
    options: effectiveOptions,
    cwd,
    runId,
    recorder,
    startedAt,
    signal,
    onUpdate,
  });
  const fallbackModel = shouldRetryWithFallback(options, effectiveOptions, result, signal)
    ? effectiveOptions.fallbackModel
    : undefined;

  if (fallbackModel) {
    await appendOps(cfg, "subagent_fallback_started", {
      taskId: effectiveOptions.taskId,
      body: `Retrying ${effectiveOptions.role} sub-agent with fallback model ${fallbackModel} after exit code ${result.exit_code}.`,
      metadata: {
        run_id: runId,
        role: effectiveOptions.role,
        cwd,
        failed_model: result.model ?? effectiveOptions.model ?? null,
        failed_exit_code: result.exit_code,
        fallback_model: fallbackModel,
        stderr_preview: result.stderr_tail,
        artifacts: result.artifacts,
      },
    });
    await recorder.appendEvent({
      type: "subagent.fallback_started",
      ts: Date.now(),
      failed_exit_code: result.exit_code,
      fallback_model: fallbackModel,
    });
    const failed = result;
    result = await backend.run({
      cfg,
      options: { ...effectiveOptions, model: fallbackModel },
      cwd,
      runId,
      recorder,
      startedAt,
      signal,
      onUpdate,
    });
    result.fallback_from_model = failed.model ?? effectiveOptions.model;
    result.fallback_from_exit_code = failed.exit_code;
  }

  const finalRequestedModel = fallbackModel ?? effectiveOptions.model;
  const completionEventType = subagentSucceeded(result)
    ? "subagent_completed"
    : result.aborted
      ? "subagent_aborted"
      : result.timeout_kind
        ? "subagent_timeout"
        : "subagent_failed";
  await appendOps(cfg, completionEventType, {
    taskId: effectiveOptions.taskId,
    body: formatCompletionOpsBody(result),
    metadata: {
      run_id: runId,
      role: effectiveOptions.role,
      cwd,
      backend: result.backend,
      model: result.model ?? finalRequestedModel ?? null,
      fallback_model: effectiveOptions.fallbackModel ?? null,
      fallback_from_model: result.fallback_from_model ?? null,
      fallback_from_exit_code: result.fallback_from_exit_code ?? null,
      session_mode: result.session_mode,
      session: result.session ?? null,
      exit_code: result.exit_code,
      signal: result.signal ?? null,
      pid: result.pid ?? null,
      started_at: result.started_at,
      ended_at: result.ended_at,
      duration_ms: result.duration_ms,
      aborted: result.aborted,
      timeout_kind: result.timeout_kind ?? null,
      forced_kill: result.forced_kill,
      assistant_final_found: result.assistant_final_found,
      prompt_echo_detected: result.prompt_echo_detected,
      output_status: result.output_status,
      message_count: result.message_count,
      assistant_message_count: result.assistant_message_count,
      child_error_message: result.child_error_message ?? null,
      stderr_preview: result.stderr_tail,
      artifacts: result.artifacts,
    },
  });

  const shouldPostResult = effectiveOptions.postResult ?? effectiveOptions.taskId !== undefined;
  if (shouldPostResult && effectiveOptions.taskId !== undefined) {
    const ok = subagentSucceeded(result);
    await sendTaskMessage(cfg, effectiveOptions.taskId, ok ? formatResultMessage(result) : formatFailureMessage(result), {
      type: ok ? "subagent_result" : "subagent_failure",
      role: effectiveOptions.role,
      run_id: runId,
      exit_code: result.exit_code,
      signal: result.signal ?? null,
      session_mode: result.session_mode,
      session: result.session ?? null,
      backend: result.backend,
      duration_ms: result.duration_ms,
      timeout_kind: result.timeout_kind ?? null,
      aborted: result.aborted,
      forced_kill: result.forced_kill,
      assistant_final_found: result.assistant_final_found,
      prompt_echo_detected: result.prompt_echo_detected,
      output_status: result.output_status,
      artifacts: result.artifacts,
      fallback_from_model: result.fallback_from_model ?? null,
      fallback_from_exit_code: result.fallback_from_exit_code ?? null,
    });
  }

  return result;
}

function shouldRetryWithFallback(
  originalOptions: RunOptions,
  effectiveOptions: RunOptions,
  result: SubagentResult,
  signal: AbortSignal | undefined,
): boolean {
  if (signal?.aborted) return false;
  if (originalOptions.model) return false;
  if (subagentSucceeded(result)) return false;
  if (isSubagentInfrastructureFailure(result)) return false;
  const fallback = normalizeString(effectiveOptions.fallbackModel);
  if (!fallback) return false;
  const attempted = normalizeString(result.model) ?? normalizeString(effectiveOptions.model);
  return attempted !== fallback;
}

async function getTask(cfg: DenConfig, taskId: number) {
  return denFetch(cfg, `/api/projects/${esc(cfg.projectId)}/tasks/${taskId}`);
}

async function resolvePromptDoc(cfg: DenConfig, slug: string): Promise<{ content: string }> {
  const projectDoc = await tryGetDocument(cfg, cfg.projectId, slug);
  if (projectDoc?.content) return projectDoc;
  const globalDoc = await tryGetDocument(cfg, "_global", `${slug}${GLOBAL_SUFFIX}`);
  if (globalDoc?.content) return globalDoc;
  return { content: fallbackPrompt(slug) };
}

async function tryGetDocument(cfg: DenConfig, projectId: string, slug: string): Promise<{ content: string } | undefined> {
  try {
    return await denFetch(cfg, `/api/projects/${esc(projectId)}/documents/${esc(slug)}`);
  } catch (error) {
    if (error instanceof Error && error.message.includes("failed with 404")) return undefined;
    throw error;
  }
}

function createSubagentBackend(): SubagentBackend {
  return piCliSubagentBackend;
}

const piCliSubagentBackend: SubagentBackend = {
  name: "pi-cli",
  run: runPiCliSubagent,
};

async function runPiCliSubagent(input: SubagentBackendInput): Promise<SubagentResult> {
  const { cfg, options, cwd, runId, recorder, startedAt, signal, onUpdate } = input;
  const artifacts = recorder.artifacts;
  const sessionMode = options.sessionMode ?? "fresh";
  const args = ["--mode", "json", "-p"];
  addSessionArgs(args, sessionMode, options.session);
  if (options.model) args.push("--model", options.model);
  const tools = options.tools ?? defaultToolsForRole(options.role);
  if (tools) args.push("--tools", tools);
  const prompt = buildSubagentPrompt(cfg, options);
  args.push(prompt);

  const env = {
    ...process.env,
    DEN_PI_AGENT: `${cfg.agent}-subagent`,
    DEN_PI_ROLE: options.role,
    DEN_PI_INSTANCE_ID: `pi-${cfg.projectId}-${safeId(options.role)}-${runId}`,
    DEN_PI_PARENT_INSTANCE_ID: cfg.instanceId,
  };

  let stderr = "";
  let buffer = "";
  let pid: number | undefined;
  let aborted = false;
  let timeoutKind: SubagentResult["timeout_kind"];
  let forcedKill = false;
  const outputExtractor = createSubagentOutputExtractor(prompt, recorder);

  const command = normalizeString(process.env.DEN_PI_SUBAGENT_PI_BIN) ?? "pi";
  const startupTimeoutMs = envMillis("DEN_PI_SUBAGENT_STARTUP_TIMEOUT_MS", DEFAULT_STARTUP_TIMEOUT_MS);
  const finalDrainMs = envMillis("DEN_PI_SUBAGENT_FINAL_DRAIN_MS", DEFAULT_FINAL_DRAIN_MS);
  const forceKillMs = envMillis("DEN_PI_SUBAGENT_FORCE_KILL_MS", DEFAULT_FORCE_KILL_MS);

  const termination = await new Promise<{ exitCode: number; signal?: string }>((resolve) => {
    const proc = spawn(command, args, {
      cwd,
      env,
      detached: process.platform !== "win32",
      shell: false,
      stdio: ["ignore", "pipe", "pipe"],
    });
    pid = proc.pid;
    let settled = false;
    let sawJsonEvent = false;
    let startupTimer: ReturnType<typeof setTimeout> | undefined;
    let finalDrainTimer: ReturnType<typeof setTimeout> | undefined;
    let forceKillTimer: ReturnType<typeof setTimeout> | undefined;
    let abortHandler: (() => void) | undefined;

    const clearTimer = (timer: ReturnType<typeof setTimeout> | undefined) => {
      if (timer) clearTimeout(timer);
    };

    const clearTimers = () => {
      clearTimer(startupTimer);
      clearTimer(finalDrainTimer);
      clearTimer(forceKillTimer);
      startupTimer = undefined;
      finalDrainTimer = undefined;
      forceKillTimer = undefined;
    };

    const resolveOnce = (code: number | null, closeSignal?: NodeJS.Signals | null) => {
      if (settled) return;
      settled = true;
      clearTimers();
      if (abortHandler && signal) signal.removeEventListener("abort", abortHandler);
      const exitCode = exitCodeFromProcess(code, closeSignal);
      resolve({ exitCode, signal: closeSignal ?? undefined });
    };

    const armForceKill = () => {
      clearTimer(forceKillTimer);
      forceKillTimer = setTimeout(() => {
        if (settled) return;
        forcedKill = true;
        killProcessTree(proc, "SIGKILL");
      }, forceKillMs);
      forceKillTimer.unref?.();
    };

    const terminate = (kind: SubagentResult["timeout_kind"], markAborted = false) => {
      if (settled) return;
      if (kind) timeoutKind = kind;
      if (markAborted) aborted = true;
      void recorder.appendEvent({
        type: kind ? `subagent.${kind}_timeout` : "subagent.abort",
        ts: Date.now(),
        pid: proc.pid ?? null,
      });
      killProcessTree(proc, "SIGTERM");
      armForceKill();
    };

    void recorder.writeStatus({
      state: "running",
      run_id: runId,
      role: options.role,
      task_id: options.taskId ?? null,
      backend: piCliSubagentBackend.name,
      cwd,
      pid: proc.pid ?? null,
      started_at: startedAt,
      command,
      model: options.model ?? null,
      tools: tools ?? null,
      session_mode: sessionMode,
      session: options.session ?? null,
      process_group: process.platform !== "win32" && proc.pid ? -proc.pid : null,
    });
    void recorder.appendEvent({
      type: "subagent.process_started",
      ts: Date.now(),
      pid: proc.pid ?? null,
      command,
      process_group: process.platform !== "win32" && proc.pid ? -proc.pid : null,
    });

    if (startupTimeoutMs > 0) {
      startupTimer = setTimeout(() => {
        if (settled || sawJsonEvent) return;
        stderr += `\n[den-subagent] Killed: no JSON output after ${startupTimeoutMs}ms (startup timeout).`;
        terminate("startup");
      }, startupTimeoutMs);
      startupTimer.unref?.();
    }

    proc.stdout.on("data", (chunk) => {
      buffer += chunk.toString();
      const lines = buffer.split("\n");
      buffer = lines.pop() ?? "";
      for (const line of lines) {
        const parsed = processStdoutLine(line);
        if (!parsed) continue;
        if (!sawJsonEvent) {
          sawJsonEvent = true;
          clearTimer(startupTimer);
          startupTimer = undefined;
        }
        const output = outputExtractor.updateFromEvent(parsed);
        if (output) onUpdate?.(output);
        if (isTerminalAssistantMessage(parsed.message)) {
          clearTimer(finalDrainTimer);
          finalDrainTimer = setTimeout(() => terminate("terminal_drain"), finalDrainMs);
          finalDrainTimer.unref?.();
        }
      }
    });

    proc.stderr.on("data", (chunk) => {
      const text = chunk.toString();
      stderr += text;
      void recorder.appendStderr(text);
    });

    proc.on("error", (error) => {
      stderr += `${error.message}\n`;
      outputExtractor.recordChildError(error.message);
      void recorder.appendEvent({
        type: "subagent.spawn_error",
        ts: Date.now(),
        error: error.message,
      });
      resolveOnce(1);
    });

    proc.on("close", (code, closeSignal) => {
      const parsed = processStdoutLine(buffer);
      if (parsed) outputExtractor.updateFromEvent(parsed);
      resolveOnce(code, closeSignal);
    });

    abortHandler = () => {
      terminate(undefined, true);
    };
    if (signal?.aborted) abortHandler();
    else signal?.addEventListener("abort", abortHandler, { once: true });
  });

  const endedAt = new Date().toISOString();
  const durationMs = Math.max(0, Date.parse(endedAt) - Date.parse(startedAt));
  const output = outputExtractor.snapshot();
  const outputStatus: SubagentResult["output_status"] = output.finalOutput
    ? "assistant_final"
    : output.promptEchoDetected
      ? "prompt_echo_only"
      : "no_assistant_final";
  const stderrTail = tail(stderr.trim(), 2000);
  const result: SubagentResult = {
    run_id: runId,
    role: options.role,
    task_id: options.taskId,
    session_mode: sessionMode,
    session: options.session,
    exit_code: termination.exitCode,
    signal: termination.signal,
    pid,
    backend: piCliSubagentBackend.name,
    started_at: startedAt,
    ended_at: endedAt,
    duration_ms: durationMs,
    aborted,
    timeout_kind: timeoutKind,
    forced_kill: forcedKill,
    final_output: output.finalOutput,
    assistant_final_found: Boolean(output.finalOutput),
    prompt_echo_detected: output.promptEchoDetected,
    output_status: outputStatus,
    stderr,
    stderr_tail: stderrTail,
    model: output.model,
    message_count: output.messageCount,
    assistant_message_count: output.assistantMessageCount,
    child_error_message: output.childErrorMessage,
    artifacts,
  };

  await recorder.writeStatus({
    state: subagentSucceeded(result) ? "complete" : "failed",
    run_id: runId,
    role: options.role,
    task_id: options.taskId ?? null,
    backend: result.backend,
    cwd,
    pid: pid ?? null,
    started_at: startedAt,
    ended_at: endedAt,
    duration_ms: durationMs,
    exit_code: result.exit_code,
    signal: result.signal ?? null,
    aborted: result.aborted,
    timeout_kind: result.timeout_kind ?? null,
    forced_kill: result.forced_kill,
    assistant_final_found: result.assistant_final_found,
    prompt_echo_detected: result.prompt_echo_detected,
    output_status: result.output_status,
    message_count: result.message_count,
    assistant_message_count: result.assistant_message_count,
    model: result.model ?? null,
    child_error_message: result.child_error_message ?? null,
  });
  await recorder.appendEvent({
    type: "subagent.process_finished",
    ts: Date.now(),
    exit_code: result.exit_code,
    signal: result.signal ?? null,
    timeout_kind: result.timeout_kind ?? null,
    forced_kill: result.forced_kill,
    output_status: result.output_status,
  });

  return result;

  function processStdoutLine(line: string): any | undefined {
    const parsed = parsePiStdoutLine(line);
    if (!parsed) return undefined;
    if (parsed.kind === "json") {
      void recorder.appendStdoutLine(parsed.line);
      return parsed.event;
    }

    void recorder.appendRawStdout(parsed.line);
    return undefined;
  }
}

function buildSubagentPrompt(cfg: DenConfig, options: RunOptions): string {
  const taskLine = options.taskId ? `Den task: #${options.taskId}\n` : "";
  return [
    `You are a fresh ${options.role} sub-agent launched by the Den Pi conductor.`,
    `Project: ${cfg.projectId}`,
    taskLine.trim(),
    "",
    "Work only on the bounded request below.",
    "Use Den as the durable record when Den tools are available, but keep final output concise.",
    "If you find ambiguity, report the question instead of broadening scope.",
    "",
    "Request:",
    options.prompt,
  ].filter((line) => line !== "").join("\n");
}

function parseRunCommand(args: string | undefined, cwd: string): RunOptions {
  const parsed = parseRunFlags(args?.trim() ?? "");
  const trimmed = parsed.rest;
  const match = trimmed.match(/^(\S+)\s+(\S+)\s+([\s\S]+)$/);
  if (!match) {
    throw new Error("Usage: /den-run-subagent [--continue|--fork <session>|--session <session>] <role> <task_id|-> <prompt>");
  }
  const taskId = match[2] === "-" ? undefined : Number(match[2]);
  if (taskId !== undefined && (!Number.isInteger(taskId) || taskId <= 0)) throw new Error("Expected task_id or '-'.");
  return {
    role: match[1],
    taskId,
    prompt: match[3].trim(),
    cwd,
    sessionMode: parsed.sessionMode,
    session: parsed.session,
  };
}

function parseTaskWrapperCommand(args: string | undefined, role: "coder" | "reviewer") {
  const parsed = parseRunFlags(args?.trim() ?? "");
  const match = parsed.rest.match(/^(\d+)(?:\s+([\s\S]*))?$/);
  if (!match) {
    throw new Error(
      role === "coder"
        ? "Usage: /den-run-coder [--continue|--fork <session>|--session <session>] <task_id> [extra notes]"
        : "Usage: /den-run-reviewer [--fork <session>|--session <session>] <task_id> [review target/notes]",
    );
  }
  const taskId = Number(match[1]);
  if (!Number.isInteger(taskId) || taskId <= 0) throw new Error("Expected task_id.");
  const notes = (match[2] ?? "").trim();
  return {
    taskId,
    extraNotes: role === "coder" ? notes || undefined : undefined,
    reviewTarget: role === "reviewer" ? notes || undefined : undefined,
    sessionMode: parsed.sessionMode,
    session: parsed.session,
  };
}

function parseRunFlags(input: string): { rest: string; sessionMode?: "fresh" | "continue" | "fork" | "session"; session?: string } {
  const parts = input.split(/\s+/).filter(Boolean);
  let sessionMode: "fresh" | "continue" | "fork" | "session" | undefined;
  let session: string | undefined;
  const rest: string[] = [];

  for (let i = 0; i < parts.length; i++) {
    const part = parts[i];
    if (part === "--fresh") {
      sessionMode = "fresh";
      continue;
    }
    if (part === "--continue") {
      sessionMode = "continue";
      continue;
    }
    if (part === "--fork" || part === "--session") {
      const value = parts[++i];
      if (!value) throw new Error(`${part} requires a session id or path.`);
      sessionMode = part === "--fork" ? "fork" : "session";
      session = value;
      continue;
    }
    rest.push(part, ...parts.slice(i + 1));
    break;
  }

  return { rest: rest.join(" "), sessionMode, session };
}

async function applyConfiguredDefaults(options: RunOptions, cwd: string): Promise<RunOptions> {
  const config = await loadMergedDenExtensionConfig(cwd);
  const roleConfig = config.subagents?.[options.role.toLowerCase()];
  return {
    ...options,
    model: options.model ?? normalizeString(roleConfig?.model),
    fallbackModel: options.fallbackModel ?? normalizeString(config.fallback_model),
    tools: options.tools ?? normalizeString(roleConfig?.tools),
  };
}

async function loadMergedDenExtensionConfig(cwd: string): Promise<DenExtensionConfig> {
  const globalConfig = await loadDenExtensionConfig("global", cwd);
  const projectConfig = await loadDenExtensionConfig("project", cwd);
  const roles = new Set([
    ...Object.keys(globalConfig.subagents ?? {}),
    ...Object.keys(projectConfig.subagents ?? {}),
  ]);
  const subagents: NonNullable<DenExtensionConfig["subagents"]> = {};
  for (const role of roles) {
    subagents[role] = {
      ...(globalConfig.subagents?.[role] ?? {}),
      ...(projectConfig.subagents?.[role] ?? {}),
    };
  }
  return {
    version: 1,
    ...globalConfig,
    ...projectConfig,
    subagents,
  };
}

async function loadDenExtensionConfig(scope: ConfigScope, cwd: string): Promise<DenExtensionConfig> {
  try {
    const text = await readFile(denConfigPath(scope, cwd), "utf8");
    const parsed = JSON.parse(text);
    if (parsed && typeof parsed === "object") return parsed;
  } catch (error: any) {
    if (error?.code !== "ENOENT") throw error;
  }
  return { version: 1, subagents: {} };
}

async function saveDenExtensionConfig(scope: ConfigScope, cwd: string, config: DenExtensionConfig) {
  const file = denConfigPath(scope, cwd);
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, `${JSON.stringify(config, null, 2)}\n`, "utf8");
}

function denConfigPath(scope: ConfigScope, cwd: string): string {
  if (scope === "global") return path.join(os.homedir(), ".pi", "agent", DEN_CONFIG_FILENAME);
  return path.join(cwd, ".pi", DEN_CONFIG_FILENAME);
}

function parseSessionMode(value: string | undefined): "fresh" | "continue" | "fork" | "session" | undefined {
  if (!value) return undefined;
  if (value === "fresh" || value === "continue" || value === "fork" || value === "session") return value;
  throw new Error("session_mode must be fresh, continue, fork, or session.");
}

function addSessionArgs(args: string[], sessionMode: string, session: string | undefined) {
  switch (sessionMode) {
    case "fresh":
      args.push("--no-session");
      return;
    case "continue":
      args.push("--continue");
      return;
    case "fork":
      if (!session) throw new Error("session is required for fork mode.");
      args.push("--fork", session);
      return;
    case "session":
      if (!session) throw new Error("session is required for session mode.");
      args.push("--session", session);
      return;
    default:
      throw new Error("session_mode must be fresh, continue, fork, or session.");
  }
}

function summarizeTaskContext(detail: any): string {
  const parts: string[] = [];
  const task = detail?.task ?? detail;
  if (task?.status) parts.push(`Status: ${task.status}`);
  if (task?.assigned_to) parts.push(`Assigned to: ${task.assigned_to}`);
  if (task?.tags?.length) parts.push(`Tags: ${task.tags.join(", ")}`);
  if (Array.isArray(detail?.dependencies) && detail.dependencies.length > 0) {
    parts.push(`Dependencies: ${detail.dependencies.map((dep: any) => `#${dep.id} ${dep.title}`).join("; ")}`);
  }
  if (Array.isArray(detail?.subtasks) && detail.subtasks.length > 0) {
    parts.push(`Subtasks: ${detail.subtasks.map((subtask: any) => `#${subtask.id} [${subtask.status}] ${subtask.title}`).join("; ")}`);
  }
  if (Array.isArray(detail?.messages) && detail.messages.length > 0) {
    parts.push("Recent messages:");
    for (const message of detail.messages.slice(0, 8)) {
      parts.push(`- #${message.id} ${message.sender} (${message.intent ?? "general"}): ${oneLine(message.content ?? "")}`);
    }
  }
  return parts.join("\n") || "(no additional Den context)";
}

function renderTemplate(template: string, values: Record<string, string>): string {
  return template.replace(/\{\{\s*([a-zA-Z0-9_]+)\s*\}\}/g, (_match, key) => values[key] ?? "");
}

function fallbackPrompt(slug: string): string {
  if (slug === CODER_PROMPT_SLUG) {
    return [
      "You are a fresh coder sub-agent launched by the Den Pi conductor.",
      "Project: {{project_id}}",
      "Task: #{{task_id}} {{task_title}}",
      "",
      "{{task_description}}",
      "",
      "{{task_context}}",
      "",
      "Work only on this bounded task. Report changed files, tests run, and known gaps.",
    ].join("\n");
  }
  return [
    "You are a fresh reviewer sub-agent launched by the Den Pi conductor.",
    "Project: {{project_id}}",
    "Task: #{{task_id}} {{task_title}}",
    "",
    "{{task_description}}",
    "",
    "{{task_context}}",
    "",
    "Review target: {{review_target}}",
    "",
    "Prioritize blocking bugs, acceptance gaps, regressions, and missing tests. Finish with a concise verdict.",
  ].join("\n");
}

function onUpdateText(onUpdate: any, role: string, taskId: number) {
  return (partial: string) => {
    onUpdate?.({
      content: [{ type: "text", text: partial || "(sub-agent running...)" }],
      details: { role, task_id: taskId },
    });
  };
}

function resultTool(result: SubagentResult) {
  const ok = subagentSucceeded(result);
  return {
    content: [{ type: "text", text: ok ? result.final_output : formatFailureSummary(result) }],
    details: result,
    isError: !ok,
  };
}

function defaultToolsForRole(_role: string): string | undefined {
  // Leave the tool allowlist open by default so MCP-provided Den tools remain
  // available. Users can pin stricter role-specific allowlists later via config.
  return undefined;
}

function formatResultLines(result: SubagentResult): string[] {
  const status = subagentSucceeded(result) ? "completed" : "failed";
  return [
    `${result.role} sub-agent ${status} (exit ${result.exit_code}${result.signal ? `, ${result.signal}` : ""})`,
    result.task_id ? `Task #${result.task_id}` : "No linked task",
    oneLine(subagentSucceeded(result) ? result.final_output : formatFailureSummary(result)),
  ];
}

function formatResultMessage(result: SubagentResult): string {
  const output = result.final_output || "(no output)";
  return [
    `Sub-agent result (${result.role})`,
    "",
    `Exit code: ${result.exit_code}`,
    result.signal ? `Signal: ${result.signal}` : null,
    `Duration: ${formatDuration(result.duration_ms)}`,
    result.model ? `Model: ${result.model}` : null,
    result.timeout_kind ? `Termination note: ${result.timeout_kind}${result.forced_kill ? " (forced kill)" : ""}` : null,
    result.fallback_from_model ? `Fallback retry from: ${result.fallback_from_model} (exit ${result.fallback_from_exit_code ?? "unknown"})` : null,
    `Artifacts: ${result.artifacts.dir}`,
    "",
    output,
    result.stderr_tail ? `\nStderr tail:\n${result.stderr_tail}` : null,
  ].filter((line): line is string => line !== null).join("\n");
}

function formatFailureMessage(result: SubagentResult): string {
  return [
    `Sub-agent failure (${result.role})`,
    "",
    formatFailureSummary(result),
    "",
    `Exit code: ${result.exit_code}`,
    result.signal ? `Signal: ${result.signal}` : null,
    `Duration: ${formatDuration(result.duration_ms)}`,
    result.timeout_kind ? `Timeout: ${result.timeout_kind}` : null,
    result.aborted ? "Aborted: true" : null,
    result.forced_kill ? "Forced kill: true" : null,
    result.child_error_message ? `Child error: ${result.child_error_message}` : null,
    result.fallback_from_model ? `Fallback retry from: ${result.fallback_from_model} (exit ${result.fallback_from_exit_code ?? "unknown"})` : null,
    `Artifacts: ${result.artifacts.dir}`,
    "",
    result.role === "reviewer"
      ? "No valid review verdict was produced; the review remains pending."
      : "No valid assistant final output was produced.",
    result.stderr_tail ? `\nStderr tail:\n${result.stderr_tail}` : null,
  ].filter((line): line is string => line !== null).join("\n");
}

function formatFailureSummary(result: SubagentResult): string {
  if (result.output_status === "prompt_echo_only") {
    return "Sub-agent did not produce an assistant final answer; only prompt-echo-like output was observed.";
  }
  if (result.timeout_kind === "startup") return "Sub-agent timed out before emitting JSON output.";
  if (result.aborted) return "Sub-agent was aborted before producing a usable final answer.";
  if (result.timeout_kind === "terminal_drain") return "Sub-agent produced output but did not exit cleanly after the terminal message.";
  if (!result.assistant_final_found) return "Sub-agent did not produce an assistant final answer.";
  return `Sub-agent process exited ${result.exit_code}${result.signal ? ` (${result.signal})` : ""}.`;
}

function formatCompletionOpsBody(result: SubagentResult): string {
  if (subagentSucceeded(result)) {
    const suffix = result.timeout_kind === "terminal_drain" ? " after forced terminal-drain cleanup" : "";
    return `Completed ${result.role} sub-agent with exit code ${result.exit_code}${suffix}.`;
  }
  return `${result.role} sub-agent failed: ${formatFailureSummary(result)}`;
}

function subagentSucceeded(result: SubagentResult): boolean {
  if (!result.assistant_final_found || result.aborted) return false;
  if (result.exit_code === 0) return true;
  return result.timeout_kind === "terminal_drain";
}

async function createRunArtifacts(runId: string): Promise<SubagentArtifacts> {
  const dir = path.join(runArtifactRoot(), runId);
  await mkdir(dir, { recursive: true });
  const artifacts = {
    dir,
    stdout_jsonl_path: path.join(dir, "stdout.jsonl"),
    stderr_log_path: path.join(dir, "stderr.log"),
    status_json_path: path.join(dir, "status.json"),
    events_jsonl_path: path.join(dir, "events.jsonl"),
  };
  await Promise.all([
    writeFile(artifacts.stdout_jsonl_path, "", "utf8"),
    writeFile(artifacts.stderr_log_path, "", "utf8"),
    writeFile(artifacts.events_jsonl_path, "", "utf8"),
  ]);
  return artifacts;
}

async function createRunRecorder(runId: string): Promise<SubagentRunRecorder> {
  const artifacts = await createRunArtifacts(runId);
  return {
    artifacts,
    writeStatus(payload: JsonObject) {
      return writeRunStatus(artifacts, payload);
    },
    appendEvent(event: JsonObject) {
      return appendRunEvent(artifacts, event);
    },
    appendStdoutLine(line: string) {
      return appendText(artifacts.stdout_jsonl_path, `${line}\n`);
    },
    appendRawStdout(line: string) {
      return appendJsonl(artifacts.stdout_jsonl_path, {
        type: "raw_stdout",
        ts: Date.now(),
        line,
      });
    },
    appendStderr(text: string) {
      return appendText(artifacts.stderr_log_path, text);
    },
  };
}

function runArtifactRoot(): string {
  const agentDir = normalizeString(process.env.PI_CODING_AGENT_DIR);
  return path.join(agentDir ?? path.join(os.homedir(), ".pi", "agent"), "den-subagent-runs");
}

async function writeRunStatus(artifacts: SubagentArtifacts, payload: JsonObject) {
  try {
    await writeFile(artifacts.status_json_path, `${JSON.stringify(payload, null, 2)}\n`, "utf8");
  } catch {
    // Artifact writes should not break the sub-agent run.
  }
}

async function appendRunEvent(artifacts: SubagentArtifacts, event: JsonObject) {
  return appendJsonl(artifacts.events_jsonl_path, event);
}

async function appendJsonl(filePath: string, payload: JsonObject) {
  return appendText(filePath, `${JSON.stringify(payload)}\n`);
}

async function appendText(filePath: string, text: string) {
  try {
    await appendFile(filePath, text, "utf8");
  } catch {
    // Artifact writes are best-effort.
  }
}

function exitCodeFromProcess(code: number | null, signal: NodeJS.Signals | null | undefined): number {
  if (typeof code === "number") return code;
  if (!signal) return 1;
  const signalNumber = signalToNumber(signal);
  return signalNumber ? 128 + signalNumber : 1;
}

function signalToNumber(signal: string): number | undefined {
  const signals: Record<string, number> = {
    SIGHUP: 1,
    SIGINT: 2,
    SIGQUIT: 3,
    SIGILL: 4,
    SIGTRAP: 5,
    SIGABRT: 6,
    SIGBUS: 7,
    SIGFPE: 8,
    SIGKILL: 9,
    SIGUSR1: 10,
    SIGSEGV: 11,
    SIGUSR2: 12,
    SIGPIPE: 13,
    SIGALRM: 14,
    SIGTERM: 15,
  };
  return signals[signal];
}

function envMillis(name: string, fallback: number): number {
  const value = Number(process.env[name]);
  return Number.isFinite(value) && value >= 0 ? value : fallback;
}

function killProcessTree(proc: { pid?: number; kill(signal?: NodeJS.Signals | number): boolean }, signal: NodeJS.Signals) {
  if (process.platform !== "win32" && proc.pid) {
    try {
      process.kill(-proc.pid, signal);
      return;
    } catch {
      // Fall through to the direct child; the process may not be a group leader.
    }
  }
  try {
    proc.kill(signal);
  } catch {
    // The process may already be gone.
  }
}

function tail(value: string, maxChars: number): string {
  if (value.length <= maxChars) return value;
  return value.slice(value.length - maxChars);
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  const minutes = Math.floor(ms / 60_000);
  const seconds = Math.floor((ms % 60_000) / 1000);
  return `${minutes}m${seconds}s`;
}

async function appendOps(
  cfg: DenConfig,
  eventType: string,
  options: { taskId?: number; body: string; metadata: JsonObject },
) {
  return denFetch(cfg, `/api/projects/${esc(cfg.projectId)}/agent-stream/ops`, {
    method: "POST",
    body: {
      sender: cfg.agent,
      sender_instance_id: cfg.instanceId,
      event_type: eventType,
      task_id: options.taskId,
      recipient_agent: cfg.agent,
      recipient_role: cfg.role,
      delivery_mode: "record_only",
      body: options.body,
      metadata: JSON.stringify(options.metadata),
    },
  });
}

async function sendTaskMessage(cfg: DenConfig, taskId: number, content: string, metadata: JsonObject) {
  return denFetch(cfg, `/api/projects/${esc(cfg.projectId)}/messages`, {
    method: "POST",
    body: {
      sender: cfg.agent,
      content,
      task_id: taskId,
      intent: "status_update",
      metadata: JSON.stringify(metadata),
    },
  });
}

async function denFetch(cfg: DenConfig, pathAndQuery: string, options: { method?: string; body?: JsonObject } = {}) {
  return denFetchBase(cfg.baseUrl, pathAndQuery, options);
}

async function denFetchBase(baseUrl: string, pathAndQuery: string, options: { method?: string; body?: JsonObject } = {}) {
  const response = await fetch(`${baseUrl}${pathAndQuery}`, {
    method: options.method ?? "GET",
    headers: options.body ? { "Content-Type": "application/json" } : undefined,
    body: options.body ? JSON.stringify(options.body) : undefined,
  });
  const text = await response.text();
  const payload = text ? JSON.parse(text) : null;
  if (!response.ok) {
    const detail = payload?.error ? `: ${payload.error}` : "";
    throw new Error(`${options.method ?? "GET"} ${pathAndQuery} failed with ${response.status}${detail}`);
  }
  return payload;
}

async function resolveConfig(ctx: any): Promise<DenConfig> {
  const baseUrl = baseUrlFromEnv();
  const projectId = await resolveProjectId(baseUrl, ctx.cwd);
  const agent = process.env.DEN_PI_AGENT ?? "pi";
  const role = process.env.DEN_PI_ROLE ?? "conductor";
  const cwdHash = createHash("sha256").update(`${projectId}:${ctx.cwd}`).digest("hex").slice(0, 12);
  const instanceId = process.env.DEN_PI_INSTANCE_ID ?? `pi-${projectId}-${cwdHash}`;
  return { baseUrl, projectId, agent, role, instanceId };
}

async function resolveProjectId(baseUrl: string, cwd: string): Promise<string> {
  const explicitProjectId = normalizeString(process.env.DEN_PI_PROJECT_ID);
  if (explicitProjectId) return explicitProjectId;

  const projects = await denFetchBase(baseUrl, "/api/projects/");
  const cwdPath = path.resolve(cwd);
  const matches = (Array.isArray(projects) ? projects : [])
    .map((project) => ({ project, rootPath: normalizeString(project.root_path ?? project.rootPath) }))
    .filter((entry) => entry.rootPath && isPathInside(cwdPath, entry.rootPath))
    .sort((a, b) => b.rootPath!.length - a.rootPath!.length);

  const projectId = normalizeString(matches[0]?.project?.id);
  if (projectId) return projectId;

  throw new Error(`Den is not bound to a project for cwd '${cwdPath}'. Start Pi inside a registered Den project root or set DEN_PI_PROJECT_ID explicitly.`);
}

function makeRunId(cfg: DenConfig, options: RunOptions): string {
  return createHash("sha256")
    .update(`${Date.now()}:${cfg.instanceId}:${options.role}:${options.taskId ?? ""}:${options.prompt}`)
    .digest("hex")
    .slice(0, 16);
}

function safeId(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9_.-]+/g, "-").replace(/^-+|-+$/g, "") || "subagent";
}

function normalizeString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value.trim() : undefined;
}

function optionalNumber(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function normalizeBaseUrl(value: string): string {
  return value.replace(/\/+$/, "");
}

function baseUrlFromEnv(): string {
  return normalizeBaseUrl(process.env.DEN_MCP_URL ?? process.env.DEN_MCP_BASE_URL ?? DEFAULT_BASE_URL);
}

function isPathInside(cwd: string, rootPath: string): boolean {
  const normalizedRoot = path.resolve(rootPath);
  return cwd === normalizedRoot || cwd.startsWith(`${normalizedRoot}${path.sep}`);
}

function oneLine(value: string): string {
  return value.replace(/\s+/g, " ").trim().slice(0, 180);
}

function esc(value: string): string {
  return encodeURIComponent(value);
}
