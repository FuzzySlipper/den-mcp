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
  sessionId: string;
};

let heartbeatTimer: ReturnType<typeof setInterval> | undefined;
let config: DenConfig | undefined;
let lastInboxLines: string[] = [];

const DEFAULT_BASE_URL = "http://192.168.1.10:5199";
const HEARTBEAT_SECONDS = 60;

export default function denExtension(pi: ExtensionAPI) {
  pi.on("session_start", async (_event, ctx) => {
    try {
      config = await resolveConfig(ctx);
      await checkIn(config, ctx, "idle");
      startHeartbeat(config, ctx);
      ctx.ui.setStatus("den", `Den ${config.projectId}/${config.role}`);
      ctx.ui.notify(`Den connected: ${config.projectId} (${config.instanceId})`, "info");
    } catch (error) {
      ctx.ui.setStatus("den", "Den offline");
      ctx.ui.notify(`Den check-in failed: ${errorMessage(error)}`, "error");
    }
  });

  pi.on("agent_start", async (_event, ctx) => {
    const cfg = await ensureConfig(ctx);
    if (!cfg) return;
    ctx.ui.setStatus("den", `Den ${cfg.projectId}/${cfg.role}: busy`);
    await checkInQuietly(cfg, ctx, "busy");
  });

  pi.on("agent_end", async (_event, ctx) => {
    const cfg = await ensureConfig(ctx);
    if (!cfg) return;
    ctx.ui.setStatus("den", `Den ${cfg.projectId}/${cfg.role}: idle`);
    await checkInQuietly(cfg, ctx, "idle");
    try {
      lastInboxLines = await buildInboxLines(cfg);
      if (lastInboxLines.length > 0) ctx.ui.setWidget("den-inbox", lastInboxLines);
    } catch {
      // Inbox refresh is advisory; do not disturb the main agent turn.
    }
  });

  pi.on("session_shutdown", async (_event, ctx) => {
    if (heartbeatTimer) clearInterval(heartbeatTimer);
    heartbeatTimer = undefined;
    const cfg = await ensureConfig(ctx);
    if (!cfg) return;
    try {
      await denFetch(cfg, "/api/agents/checkout", {
        method: "POST",
        body: {
          agent: cfg.agent,
          project_id: cfg.projectId,
          session_id: cfg.sessionId,
          instance_id: cfg.instanceId,
        },
      });
    } catch {
      // Shutdown is best-effort; the server ages out stale bindings.
    }
  });

  pi.registerCommand("den-status", {
    description: "Show the current Den Pi binding.",
    handler: async (_args, ctx) => {
      const cfg = await requireConfig(ctx);
      const bindings = await denFetch(cfg, `/api/agents/bindings?${query({
        projectId: cfg.projectId,
        agentIdentity: cfg.agent,
        role: cfg.role,
        transportKind: "pi_extension",
      })}`);
      ctx.ui.setWidget("den-status", [
        `Project: ${cfg.projectId}`,
        `Agent: ${cfg.agent}`,
        `Role: ${cfg.role}`,
        `Instance: ${cfg.instanceId}`,
      ]);
      ctx.ui.notify(`Den binding active (${Array.isArray(bindings) ? bindings.length : 0} matching bindings).`, "info");
    },
  });

  pi.registerCommand("den-inbox", {
    description: "Show pending Den work for this Pi conductor.",
    handler: async (_args, ctx) => {
      const cfg = await requireConfig(ctx);
      lastInboxLines = await buildInboxLines(cfg);
      ctx.ui.setWidget("den-inbox", lastInboxLines);
      ctx.ui.notify(lastInboxLines.join("\n"), "info");
    },
  });

  pi.registerCommand("den-next", {
    description: "Show the next unblocked Den task for this project.",
    handler: async (args, ctx) => {
      const cfg = await requireConfig(ctx);
      const assignedTo = args?.trim() || undefined;
      const next = await getNextTask(cfg, assignedTo);
      const lines = formatNextTask(next);
      ctx.ui.setWidget("den-next", lines);
      ctx.ui.notify(lines.join("\n"), "info");
    },
  });

  pi.registerTool({
    name: "den_get_task",
    label: "Den Task",
    description: "Fetch a Den task detail, including dependencies, subtasks, and recent messages.",
    parameters: Type.Object({
      task_id: Type.Number({ description: "Den task ID." }),
      project_id: Type.Optional(Type.String({ description: "Den project ID. Defaults to the current Pi project binding." })),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
      const cfg = await requireConfig(ctx);
      const projectId = normalizeString(params.project_id) ?? cfg.projectId;
      const task = await denFetch(cfg, `/api/projects/${esc(projectId)}/tasks/${params.task_id}`);
      return toolJson(task);
    },
  });

  pi.registerTool({
    name: "den_next_task",
    label: "Den Next Task",
    description: "Get the next unblocked Den task for a project.",
    parameters: Type.Object({
      project_id: Type.Optional(Type.String({ description: "Den project ID. Defaults to the current Pi project binding." })),
      assigned_to: Type.Optional(Type.String({ description: "Optional assignee filter." })),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
      const cfg = await requireConfig(ctx);
      const projectId = normalizeString(params.project_id) ?? cfg.projectId;
      const assignedTo = normalizeString(params.assigned_to);
      const next = await getNextTask({ ...cfg, projectId }, assignedTo);
      return toolJson(next);
    },
  });

  pi.registerTool({
    name: "den_inbox",
    label: "Den Inbox",
    description: "Summarize pending dispatches, unread messages, wakeable stream messages, and next task for this Pi binding.",
    parameters: Type.Object({
      project_id: Type.Optional(Type.String({ description: "Den project ID. Defaults to the current Pi project binding." })),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
      const cfg = await requireConfig(ctx);
      const projectId = normalizeString(params.project_id) ?? cfg.projectId;
      const lines = await buildInboxLines({ ...cfg, projectId });
      return toolJson({ project_id: projectId, lines });
    },
  });
}

async function resolveConfig(ctx: any): Promise<DenConfig> {
  const baseUrl = normalizeBaseUrl(process.env.DEN_MCP_URL ?? process.env.DEN_MCP_BASE_URL ?? DEFAULT_BASE_URL);
  const projectId = process.env.DEN_PI_PROJECT_ID ?? await inferProjectId(baseUrl, ctx.cwd);
  const agent = process.env.DEN_PI_AGENT ?? "pi";
  const role = process.env.DEN_PI_ROLE ?? "conductor";
  const cwdHash = createHash("sha256").update(`${projectId}:${ctx.cwd}`).digest("hex").slice(0, 12);
  const instanceId = process.env.DEN_PI_INSTANCE_ID ?? `pi-${projectId}-${cwdHash}`;
  const sessionFile = ctx.sessionManager?.getSessionFile?.() ?? "ephemeral";
  const sessionId = `pi:${projectId}:${instanceId}:${sessionFile}`;
  return { baseUrl, projectId, agent, role, instanceId, sessionId };
}

async function ensureConfig(ctx: any): Promise<DenConfig | undefined> {
  if (config) return config;
  try {
    config = await resolveConfig(ctx);
    return config;
  } catch {
    return undefined;
  }
}

async function requireConfig(ctx: any): Promise<DenConfig> {
  const cfg = await ensureConfig(ctx);
  if (!cfg) throw new Error("Den project could not be resolved. Set DEN_PI_PROJECT_ID or register this repo in Den.");
  return cfg;
}

async function inferProjectId(baseUrl: string, cwd: string): Promise<string> {
  const response = await fetch(`${baseUrl}/api/projects`);
  if (!response.ok) throw new Error(`GET /api/projects failed with ${response.status}`);
  const projects = await response.json() as Array<{ id: string; root_path?: string | null }>;
  const cwdResolved = path.resolve(cwd);
  const matches = projects
    .filter((project) => project.root_path && isWithin(path.resolve(project.root_path), cwdResolved))
    .sort((a, b) => (b.root_path?.length ?? 0) - (a.root_path?.length ?? 0));
  if (matches[0]?.id) return matches[0].id;
  throw new Error(`No Den project root matched ${cwd}`);
}

function isWithin(root: string, candidate: string): boolean {
  const relative = path.relative(root, candidate);
  return relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative));
}

function startHeartbeat(cfg: DenConfig, ctx: any) {
  if (heartbeatTimer) clearInterval(heartbeatTimer);
  heartbeatTimer = setInterval(() => {
    denFetch(cfg, "/api/agents/heartbeat", {
      method: "POST",
      body: {
        agent: cfg.agent,
        project_id: cfg.projectId,
        instance_id: cfg.instanceId,
      },
    }).catch((error) => {
      ctx.ui.setStatus("den", `Den heartbeat failed: ${errorMessage(error)}`);
    });
  }, HEARTBEAT_SECONDS * 1000);
}

async function checkInQuietly(cfg: DenConfig, ctx: any, state: string) {
  try {
    await checkIn(cfg, ctx, state);
  } catch {
    // State updates should not interrupt an active agent turn.
  }
}

async function checkIn(cfg: DenConfig, ctx: any, state: string) {
  await denFetch(cfg, "/api/agents/checkin", {
    method: "POST",
    body: {
      agent: cfg.agent,
      project_id: cfg.projectId,
      session_id: cfg.sessionId,
      instance_id: cfg.instanceId,
      agent_family: "pi",
      role: cfg.role,
      transport_kind: "pi_extension",
      binding_status: "active",
      metadata: JSON.stringify({
        cwd: ctx.cwd,
        state,
        session_file: ctx.sessionManager?.getSessionFile?.() ?? null,
        model: ctx.model ? `${ctx.model.provider}/${ctx.model.id}` : null,
      }),
    },
  });
}

async function buildInboxLines(cfg: DenConfig): Promise<string[]> {
  const [dispatches, unread, stream, next] = await Promise.all([
    denFetch(cfg, `/api/dispatch?${query({ projectId: cfg.projectId, targetAgent: cfg.agent, status: "approved" })}`),
    denFetch(cfg, `/api/projects/${esc(cfg.projectId)}/messages?${query({ unreadFor: cfg.agent, limit: 10 })}`),
    denFetch(cfg, `/api/agent-stream?${query({ projectId: cfg.projectId, streamKind: "message", limit: 50 })}`),
    getNextTask(cfg),
  ]);

  const wakeable = Array.isArray(stream) ? stream.filter((entry) => isForThisBinding(entry, cfg)) : [];
  const lines = [
    `Den inbox for ${cfg.projectId}/${cfg.agent}/${cfg.role}`,
    `Approved dispatches: ${Array.isArray(dispatches) ? dispatches.length : 0}`,
    `Unread messages: ${Array.isArray(unread) ? unread.length : 0}`,
    `Targeted stream messages: ${wakeable.length}`,
    ...formatNextTask(next),
  ];

  for (const dispatch of take(dispatches, 3)) {
    lines.push(`Dispatch #${dispatch.id}: ${oneLine(dispatch.summary ?? dispatch.trigger_type ?? "pending dispatch")}`);
  }
  for (const message of take(unread, 3)) {
    lines.push(`Message #${message.id}: ${oneLine(message.content ?? "")}`);
  }
  for (const entry of take(wakeable, 3)) {
    lines.push(`Stream #${entry.id} ${entry.event_type}: ${oneLine(entry.body ?? "")}`);
  }
  return lines;
}

function isForThisBinding(entry: any, cfg: DenConfig): boolean {
  if (!entry || entry.delivery_mode === "record_only") return false;
  if (entry.recipient_instance_id) return entry.recipient_instance_id === cfg.instanceId;
  if (entry.recipient_agent && entry.recipient_agent !== cfg.agent) return false;
  if (entry.recipient_role && entry.recipient_role !== cfg.role) return false;
  return Boolean(entry.recipient_agent || entry.recipient_role);
}

async function getNextTask(cfg: DenConfig, assignedTo?: string) {
  return denFetch(cfg, `/api/projects/${esc(cfg.projectId)}/tasks/next?${query({ assignedTo })}`);
}

function formatNextTask(next: any): string[] {
  if (next?.message) return [`Next task: ${next.message}`];
  if (next?.id) return [`Next task: #${next.id} [P${next.priority}] ${next.title}`];
  return ["Next task: unavailable"];
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

function query(values: Record<string, string | number | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    if (value !== undefined && value !== "") params.set(key, String(value));
  }
  return params.toString();
}

function toolJson(value: unknown) {
  return {
    content: [{ type: "text", text: JSON.stringify(value, null, 2) }],
    details: {},
  };
}

function take(value: unknown, count: number): any[] {
  return Array.isArray(value) ? value.slice(0, count) : [];
}

function oneLine(value: string): string {
  return value.replace(/\s+/g, " ").trim().slice(0, 140);
}

function normalizeString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value.trim() : undefined;
}

function normalizeBaseUrl(value: string): string {
  return value.replace(/\/+$/, "");
}

function esc(value: string): string {
  return encodeURIComponent(value);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
