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
  model?: string;
  tools?: string;
  cwd?: string;
  postResult?: boolean;
};

type SubagentResult = {
  role: string;
  task_id?: number;
  exit_code: number;
  final_output: string;
  stderr: string;
  model?: string;
  message_count: number;
};

const DEFAULT_BASE_URL = "http://192.168.1.10:5199";

export default function denSubagent(pi: ExtensionAPI) {
  pi.registerCommand("den-run-subagent", {
    description: "Run a fresh Pi sub-agent. Usage: /den-run-subagent <role> <task_id|-> <prompt>",
    handler: async (args, ctx) => {
      const cfg = resolveConfig(ctx);
      const options = parseRunCommand(args, ctx.cwd);
      const result = await runDenSubagent(cfg, options, ctx.cwd, undefined, undefined);
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
    }),
    async execute(_toolCallId, params, signal, onUpdate, ctx) {
      const cfg = resolveConfig(ctx);
      const result = await runDenSubagent(
        cfg,
        {
          role: params.role,
          prompt: params.prompt,
          taskId: optionalNumber(params.task_id),
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
    });
  }

  return result;
}

async function spawnPiSubagent(
  cfg: DenConfig,
  options: RunOptions,
  cwd: string,
  runId: string,
  signal: AbortSignal | undefined,
  onUpdate: ((partial: string) => void) | undefined,
): Promise<SubagentResult> {
  const args = ["--mode", "json", "-p", "--no-session"];
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
  const trimmed = args?.trim() ?? "";
  const match = trimmed.match(/^(\S+)\s+(\S+)\s+([\s\S]+)$/);
  if (!match) throw new Error("Usage: /den-run-subagent <role> <task_id|-> <prompt>");
  const taskId = match[2] === "-" ? undefined : Number(match[2]);
  if (taskId !== undefined && (!Number.isInteger(taskId) || taskId <= 0)) throw new Error("Expected task_id or '-'.");
  return {
    role: match[1],
    taskId,
    prompt: match[3].trim(),
    cwd,
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
