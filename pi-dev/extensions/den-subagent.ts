import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import path from "node:path";
import type { ExtensionAPI } from "@mariozechner/pi-coding-agent";
import { Type } from "typebox";

type JsonObject = Record<string, unknown>;

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
  tools?: string;
  cwd?: string;
  postResult?: boolean;
};

type SubagentResult = {
  role: string;
  task_id?: number;
  session_mode: string;
  session?: string;
  exit_code: number;
  final_output: string;
  stderr: string;
  model?: string;
  message_count: number;
};

const DEFAULT_BASE_URL = "http://192.168.1.10:5199";
const CODER_PROMPT_SLUG = "pi-coder-subagent-prompt";
const REVIEWER_PROMPT_SLUG = "pi-reviewer-subagent-prompt";
const GLOBAL_SUFFIX = "-default";

export default function denSubagent(pi: ExtensionAPI) {
  pi.registerCommand("den-run-subagent", {
    description: "Run a Pi sub-agent. Usage: /den-run-subagent [--continue|--fork <session>|--session <session>] <role> <task_id|-> <prompt>",
    handler: async (args, ctx) => {
      const cfg = resolveConfig(ctx);
      const options = parseRunCommand(args, ctx.cwd);
      const result = await runDenSubagent(cfg, options, ctx.cwd, undefined, undefined);
      ctx.ui.setWidget("den-subagent", formatResultLines(result));
      ctx.ui.notify(formatResultLines(result).join("\n"), result.exit_code === 0 ? "info" : "error");
    },
  });

  pi.registerCommand("den-run-coder", {
    description: "Run a coder sub-agent using the Den-managed coder prompt. Usage: /den-run-coder [--continue|--fork <session>|--session <session>] <task_id> [extra notes]",
    handler: async (args, ctx) => {
      const cfg = resolveConfig(ctx);
      const parsed = parseTaskWrapperCommand(args, "coder");
      const result = await runPromptedSubagent(cfg, "coder", parsed, ctx.cwd, undefined, undefined);
      ctx.ui.setWidget("den-subagent", formatResultLines(result));
      ctx.ui.notify(formatResultLines(result).join("\n"), result.exit_code === 0 ? "info" : "error");
    },
  });

  pi.registerCommand("den-run-reviewer", {
    description: "Run a reviewer sub-agent using the Den-managed reviewer prompt. Usage: /den-run-reviewer [--fork <session>|--session <session>] <task_id> [review target/notes]",
    handler: async (args, ctx) => {
      const cfg = resolveConfig(ctx);
      const parsed = parseTaskWrapperCommand(args, "reviewer");
      const result = await runPromptedSubagent(cfg, "reviewer", parsed, ctx.cwd, undefined, undefined);
      ctx.ui.setWidget("den-subagent", formatResultLines(result));
      ctx.ui.notify(formatResultLines(result).join("\n"), result.exit_code === 0 ? "info" : "error");
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
      const cfg = resolveConfig(ctx);
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
      return {
        content: [{ type: "text", text: result.final_output || "(no output)" }],
        details: result,
        isError: result.exit_code !== 0,
      };
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
      const cfg = resolveConfig(ctx);
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
      const cfg = resolveConfig(ctx);
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
  const runId = makeRunId(cfg, options);
  const cwd = options.cwd ? path.resolve(options.cwd) : defaultCwd;
  await appendOps(cfg, "subagent_started", {
    taskId: options.taskId,
    body: `Started ${options.role} sub-agent${options.taskId ? ` for task #${options.taskId}` : ""}.`,
    metadata: {
      run_id: runId,
      role: options.role,
      cwd,
      model: options.model ?? null,
      tools: options.tools ?? defaultToolsForRole(options.role),
      session_mode: options.sessionMode ?? "fresh",
      session: options.session ?? null,
    },
  });

  const result = await spawnPiSubagent(cfg, options, cwd, runId, signal, onUpdate);

  await appendOps(cfg, "subagent_completed", {
    taskId: options.taskId,
    body: `Completed ${options.role} sub-agent with exit code ${result.exit_code}.`,
    metadata: {
      run_id: runId,
      role: options.role,
      cwd,
      model: result.model ?? options.model ?? null,
      session_mode: result.session_mode,
      session: result.session ?? null,
      exit_code: result.exit_code,
      message_count: result.message_count,
      stderr_preview: result.stderr.slice(0, 1000),
    },
  });

  const shouldPostResult = options.postResult ?? options.taskId !== undefined;
  if (shouldPostResult && options.taskId !== undefined) {
    await sendTaskMessage(cfg, options.taskId, formatResultMessage(result), {
      type: "subagent_result",
      role: options.role,
      run_id: runId,
      exit_code: result.exit_code,
      session_mode: result.session_mode,
      session: result.session ?? null,
    });
  }

  return result;
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

async function spawnPiSubagent(
  cfg: DenConfig,
  options: RunOptions,
  cwd: string,
  runId: string,
  signal: AbortSignal | undefined,
  onUpdate: ((partial: string) => void) | undefined,
): Promise<SubagentResult> {
  const sessionMode = options.sessionMode ?? "fresh";
  const args = ["--mode", "json", "-p"];
  addSessionArgs(args, sessionMode, options.session);
  if (options.model) args.push("--model", options.model);
  const tools = options.tools ?? defaultToolsForRole(options.role);
  if (tools) args.push("--tools", tools);
  args.push(buildSubagentPrompt(cfg, options));

  const env = {
    ...process.env,
    DEN_PI_AGENT: `${cfg.agent}-subagent`,
    DEN_PI_ROLE: options.role,
    DEN_PI_INSTANCE_ID: `pi-${cfg.projectId}-${safeId(options.role)}-${runId}`,
    DEN_PI_PARENT_INSTANCE_ID: cfg.instanceId,
  };

  let stderr = "";
  let buffer = "";
  let finalOutput = "";
  let model: string | undefined;
  let messageCount = 0;

  const exitCode = await new Promise<number>((resolve) => {
    const proc = spawn("pi", args, {
      cwd,
      env,
      shell: false,
      stdio: ["ignore", "pipe", "pipe"],
    });

    proc.stdout.on("data", (chunk) => {
      buffer += chunk.toString();
      const lines = buffer.split("\n");
      buffer = lines.pop() ?? "";
      for (const line of lines) {
        const parsed = parseJsonLine(line);
        if (!parsed) continue;
        const output = updateFromEvent(parsed);
        if (output) onUpdate?.(output);
      }
    });

    proc.stderr.on("data", (chunk) => {
      stderr += chunk.toString();
    });

    proc.on("error", (error) => {
      stderr += `${error.message}\n`;
      resolve(1);
    });

    proc.on("close", (code) => {
      const parsed = parseJsonLine(buffer);
      if (parsed) updateFromEvent(parsed);
      resolve(code ?? 0);
    });

    const abort = () => {
      proc.kill("SIGTERM");
      setTimeout(() => {
        if (!proc.killed) proc.kill("SIGKILL");
      }, 5000);
    };
    if (signal?.aborted) abort();
    else signal?.addEventListener("abort", abort, { once: true });
  });

  return {
    role: options.role,
    task_id: options.taskId,
    session_mode: sessionMode,
    session: options.session,
    exit_code: exitCode,
    final_output: finalOutput,
    stderr,
    model,
    message_count: messageCount,
  };

  function updateFromEvent(event: any): string | undefined {
    const message = event.message;
    if (!message) return undefined;
    if (event.type !== "message_end" && event.type !== "tool_result_end") return undefined;
    messageCount++;
    if (message.model && typeof message.model === "string") model = message.model;
    const text = extractText(message);
    if (text) {
      finalOutput = text;
      return text;
    }
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
  return {
    content: [{ type: "text", text: result.final_output || "(no output)" }],
    details: result,
    isError: result.exit_code !== 0,
  };
}

function defaultToolsForRole(role: string): string | undefined {
  switch (role.toLowerCase()) {
    case "reviewer":
      return "read,grep,find,ls,bash,den_get_task,den_inbox,den_next_task";
    case "planner":
      return "read,grep,find,ls,den_get_task,den_inbox,den_next_task";
    default:
      return undefined;
  }
}

function formatResultLines(result: SubagentResult): string[] {
  return [
    `${result.role} sub-agent exited ${result.exit_code}`,
    result.task_id ? `Task #${result.task_id}` : "No linked task",
    oneLine(result.final_output || result.stderr || "(no output)"),
  ];
}

function formatResultMessage(result: SubagentResult): string {
  const output = result.final_output || "(no output)";
  const stderr = result.stderr.trim();
  return [
    `Sub-agent result (${result.role})`,
    "",
    `Exit code: ${result.exit_code}`,
    result.model ? `Model: ${result.model}` : null,
    "",
    output,
    stderr ? `\nStderr:\n${stderr.slice(0, 2000)}` : null,
  ].filter((line): line is string => line !== null).join("\n");
}

function extractText(message: any): string | undefined {
  if (!Array.isArray(message.content)) return undefined;
  for (let i = message.content.length - 1; i >= 0; i--) {
    const part = message.content[i];
    if (part?.type === "text" && typeof part.text === "string") return part.text;
  }
  return undefined;
}

function parseJsonLine(line: string): any | undefined {
  if (!line.trim()) return undefined;
  try {
    return JSON.parse(line);
  } catch {
    return undefined;
  }
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
  const response = await fetch(`${cfg.baseUrl}${pathAndQuery}`, {
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

function resolveConfig(ctx: any): DenConfig {
  const baseUrl = normalizeBaseUrl(process.env.DEN_MCP_URL ?? process.env.DEN_MCP_BASE_URL ?? DEFAULT_BASE_URL);
  const projectId = process.env.DEN_PI_PROJECT_ID ?? path.basename(path.resolve(ctx.cwd));
  const agent = process.env.DEN_PI_AGENT ?? "pi";
  const role = process.env.DEN_PI_ROLE ?? "conductor";
  const cwdHash = createHash("sha256").update(`${projectId}:${ctx.cwd}`).digest("hex").slice(0, 12);
  const instanceId = process.env.DEN_PI_INSTANCE_ID ?? `pi-${projectId}-${cwdHash}`;
  return { baseUrl, projectId, agent, role, instanceId };
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

function oneLine(value: string): string {
  return value.replace(/\s+/g, " ").trim().slice(0, 180);
}

function esc(value: string): string {
  return encodeURIComponent(value);
}
