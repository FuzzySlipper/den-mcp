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
let currentTaskId: number | undefined;

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

  pi.registerCommand("den-claim-next", {
    description: "Claim the next unblocked Den task and mark it in progress.",
    handler: async (args, ctx) => {
      const cfg = await requireConfig(ctx);
      const assignedTo = args?.trim() || undefined;
      const result = await claimNextTask(cfg, assignedTo);
      const lines = formatClaimResult(result);
      ctx.ui.setWidget("den-task", lines);
      ctx.ui.notify(lines.join("\n"), "info");
    },
  });

  pi.registerCommand("den-task", {
    description: "Show a Den task detail and make it the current task for note/done commands.",
    handler: async (args, ctx) => {
      const cfg = await requireConfig(ctx);
      const taskId = parseRequiredId(args, "task id");
      const detail = await getTask(cfg, taskId);
      currentTaskId = taskId;
      const lines = formatTaskDetail(detail);
      ctx.ui.setWidget("den-task", lines);
      ctx.ui.notify(lines.join("\n"), "info");
    },
  });

  pi.registerCommand("den-note", {
    description: "Post a note to the current Den task, or /den-note <task_id> <text>.",
    handler: async (args, ctx) => {
      const cfg = await requireConfig(ctx);
      const scoped = parseTaskScopedText(args, currentTaskId, "note text");
      const message = await sendTaskMessage(cfg, {
        taskId: scoped.taskId,
        content: scoped.text,
        intent: "note",
        metadata: { type: "note" },
      });
      currentTaskId = scoped.taskId;
      ctx.ui.notify(`Posted note #${message.id} on task #${scoped.taskId}.`, "info");
    },
  });

  pi.registerCommand("den-done", {
    description: "Mark the current Den task done, or /den-done <task_id> [note].",
    handler: async (args, ctx) => {
      const cfg = await requireConfig(ctx);
      const scoped = parseOptionalTaskScopedText(args, currentTaskId);
      if (scoped.text) {
        await sendTaskMessage(cfg, {
          taskId: scoped.taskId,
          content: scoped.text,
          intent: "status_update",
          metadata: { type: "status_update" },
        });
      }
      const updated = await updateTask(cfg, scoped.taskId, { status: "done" });
      currentTaskId = scoped.taskId;
      ctx.ui.notify(`Marked task #${updated.id} done.`, "info");
    },
  });

  pi.registerCommand("den-blocked", {
    description: "Mark the current Den task blocked, or /den-blocked <task_id> <reason>.",
    handler: async (args, ctx) => {
      const cfg = await requireConfig(ctx);
      const scoped = parseTaskScopedText(args, currentTaskId, "block reason");
      await sendTaskMessage(cfg, {
        taskId: scoped.taskId,
        content: scoped.text,
        intent: "task_blocked",
        metadata: { type: "task_blocked" },
      });
      const updated = await updateTask(cfg, scoped.taskId, { status: "blocked" });
      currentTaskId = scoped.taskId;
      ctx.ui.notify(`Marked task #${updated.id} blocked.`, "info");
    },
  });

  pi.registerCommand("den-mark-read", {
    description: "Mark Den messages read. Usage: /den-mark-read <id> [id...]",
    handler: async (args, ctx) => {
      const cfg = await requireConfig(ctx);
      const messageIds = parseIds(args);
      const result = await markMessagesRead(cfg, messageIds);
      ctx.ui.notify(`Marked ${result.marked ?? messageIds.length} message(s) read.`, "info");
    },
  });

  pi.registerCommand("den-complete-dispatch", {
    description: "Mark a Den dispatch complete. Usage: /den-complete-dispatch <dispatch_id>",
    handler: async (args, ctx) => {
      const cfg = await requireConfig(ctx);
      const dispatchId = parseRequiredId(args, "dispatch id");
      const result = await completeDispatch(cfg, dispatchId);
      ctx.ui.notify(`Completed dispatch #${result.id ?? dispatchId}.`, "info");
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

  pi.registerTool({
    name: "den_claim_next_task",
    label: "Den Claim Next Task",
    description: "Claim the next unblocked Den task by assigning it to this Pi agent and marking it in progress.",
    parameters: Type.Object({
      project_id: Type.Optional(Type.String({ description: "Den project ID. Defaults to the current Pi project binding." })),
      assigned_to: Type.Optional(Type.String({ description: "Optional filter for the next task lookup." })),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
      const cfg = await requireConfig(ctx);
      const projectId = normalizeString(params.project_id) ?? cfg.projectId;
      const assignedTo = normalizeString(params.assigned_to);
      const result = await claimNextTask({ ...cfg, projectId }, assignedTo);
      return toolJson(result);
    },
  });

  pi.registerTool({
    name: "den_update_task",
    label: "Den Update Task",
    description: "Update Den task fields such as status, assigned_to, priority, title, description, tags, or parent_id.",
    parameters: Type.Object({
      task_id: Type.Number({ description: "Den task ID." }),
      project_id: Type.Optional(Type.String({ description: "Den project ID. Defaults to the current Pi project binding." })),
      status: Type.Optional(Type.String({ description: "Optional status: planned, in_progress, review, blocked, done, cancelled." })),
      assigned_to: Type.Optional(Type.String({ description: "Optional assignee." })),
      title: Type.Optional(Type.String({ description: "Optional new title." })),
      description: Type.Optional(Type.String({ description: "Optional new description." })),
      priority: Type.Optional(Type.Number({ description: "Optional priority 1-5." })),
      tags: Type.Optional(Type.Array(Type.String(), { description: "Optional full replacement tag list." })),
      parent_id: Type.Optional(Type.Number({ description: "Optional parent task ID." })),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
      const cfg = await requireConfig(ctx);
      const projectId = normalizeString(params.project_id) ?? cfg.projectId;
      const updated = await updateTask({ ...cfg, projectId }, params.task_id, buildTaskChanges(params));
      return toolJson(updated);
    },
  });

  pi.registerTool({
    name: "den_send_message",
    label: "Den Send Message",
    description: "Post a Den task-thread or project message.",
    parameters: Type.Object({
      content: Type.String({ description: "Message body." }),
      project_id: Type.Optional(Type.String({ description: "Den project ID. Defaults to the current Pi project binding." })),
      task_id: Type.Optional(Type.Number({ description: "Optional task ID." })),
      thread_id: Type.Optional(Type.Number({ description: "Optional thread/root message ID." })),
      intent: Type.Optional(Type.String({ description: "Optional intent, e.g. note, status_update, question, answer, handoff, task_blocked." })),
      metadata_json: Type.Optional(Type.String({ description: "Optional metadata JSON string." })),
      sender: Type.Optional(Type.String({ description: "Optional sender. Defaults to current Pi agent." })),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
      const cfg = await requireConfig(ctx);
      const projectId = normalizeString(params.project_id) ?? cfg.projectId;
      const message = await sendMessage({ ...cfg, projectId }, {
        content: params.content,
        taskId: optionalNumber(params.task_id),
        threadId: optionalNumber(params.thread_id),
        intent: normalizeString(params.intent),
        metadataJson: normalizeString(params.metadata_json),
        sender: normalizeString(params.sender),
      });
      return toolJson(message);
    },
  });

  pi.registerTool({
    name: "den_mark_read",
    label: "Den Mark Read",
    description: "Mark Den messages as read for this Pi agent.",
    parameters: Type.Object({
      message_ids: Type.Array(Type.Number(), { description: "Message IDs to mark read." }),
      agent: Type.Optional(Type.String({ description: "Agent identity. Defaults to current Pi agent." })),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
      const cfg = await requireConfig(ctx);
      const result = await markMessagesRead(cfg, params.message_ids, normalizeString(params.agent));
      return toolJson(result);
    },
  });

  pi.registerTool({
    name: "den_complete_dispatch",
    label: "Den Complete Dispatch",
    description: "Mark a Den dispatch completed.",
    parameters: Type.Object({
      dispatch_id: Type.Number({ description: "Dispatch ID." }),
      completed_by: Type.Optional(Type.String({ description: "Completer identity. Defaults to current Pi agent." })),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
      const cfg = await requireConfig(ctx);
      const result = await completeDispatch(cfg, params.dispatch_id, normalizeString(params.completed_by));
      return toolJson(result);
    },
  });
}

async function resolveConfig(ctx: any): Promise<DenConfig> {
  const baseUrl = normalizeBaseUrl(process.env.DEN_MCP_URL ?? process.env.DEN_MCP_BASE_URL ?? DEFAULT_BASE_URL);
  const projectId = process.env.DEN_PI_PROJECT_ID ?? inferProjectIdFromCwd(ctx.cwd);
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
  if (!cfg) throw new Error("Den project could not be resolved. Set DEN_PI_PROJECT_ID or start Pi from a project directory.");
  return cfg;
}

function inferProjectIdFromCwd(cwd: string): string {
  const projectId = path.basename(path.resolve(cwd)).trim();
  if (!projectId) throw new Error(`Could not infer Den project id from cwd: ${cwd}`);
  return projectId;
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

async function getTask(cfg: DenConfig, taskId: number) {
  return denFetch(cfg, `/api/projects/${esc(cfg.projectId)}/tasks/${taskId}`);
}

async function claimNextTask(cfg: DenConfig, assignedTo?: string) {
  const next = await getNextTask(cfg, assignedTo);
  if (next?.message || !next?.id) return { claimed: false, next };
  const task = await updateTask(cfg, next.id, {
    status: "in_progress",
    assigned_to: cfg.agent,
  });
  currentTaskId = task.id;
  const detail = await getTask(cfg, task.id);
  return { claimed: true, task, detail };
}

async function updateTask(cfg: DenConfig, taskId: number, changes: JsonObject) {
  return denFetch(cfg, `/api/projects/${esc(cfg.projectId)}/tasks/${taskId}`, {
    method: "PUT",
    body: {
      agent: cfg.agent,
      ...changes,
    },
  });
}

async function sendTaskMessage(
  cfg: DenConfig,
  options: { taskId: number; content: string; intent?: string; metadata?: JsonObject },
) {
  return sendMessage(cfg, {
    taskId: options.taskId,
    content: options.content,
    intent: options.intent,
    metadataJson: options.metadata ? JSON.stringify(options.metadata) : undefined,
  });
}

async function sendMessage(
  cfg: DenConfig,
  options: {
    content: string;
    taskId?: number;
    threadId?: number;
    intent?: string;
    metadataJson?: string;
    sender?: string;
  },
) {
  return denFetch(cfg, `/api/projects/${esc(cfg.projectId)}/messages`, {
    method: "POST",
    body: {
      sender: options.sender ?? cfg.agent,
      content: options.content,
      task_id: options.taskId,
      thread_id: options.threadId,
      intent: options.intent,
      metadata: options.metadataJson,
    },
  });
}

async function markMessagesRead(cfg: DenConfig, messageIds: number[], agent?: string) {
  return denFetch(cfg, "/api/messages/mark-read", {
    method: "POST",
    body: {
      agent: agent ?? cfg.agent,
      message_ids: messageIds,
    },
  });
}

async function completeDispatch(cfg: DenConfig, dispatchId: number, completedBy?: string) {
  return denFetch(cfg, `/api/dispatch/${dispatchId}/complete`, {
    method: "POST",
    body: {
      completed_by: completedBy ?? cfg.agent,
    },
  });
}

function formatNextTask(next: any): string[] {
  if (next?.message) return [`Next task: ${next.message}`];
  if (next?.id) return [`Next task: #${next.id} [P${next.priority}] ${next.title}`];
  return ["Next task: unavailable"];
}

function formatClaimResult(result: any): string[] {
  if (!result?.claimed) return ["No task claimed.", ...formatNextTask(result?.next)];
  return formatTaskDetail(result.detail ?? { task: result.task });
}

function formatTaskDetail(detail: any): string[] {
  const task = detail?.task ?? detail;
  if (!task?.id) return ["Task detail unavailable."];
  const lines = [
    `Task #${task.id} [${task.status ?? "unknown"}] P${task.priority ?? "?"}: ${task.title}`,
  ];
  if (task.assigned_to) lines.push(`Assigned: ${task.assigned_to}`);
  if (task.description) lines.push(oneLine(task.description));
  const messages = Array.isArray(detail?.messages) ? detail.messages : [];
  if (messages.length > 0) lines.push(`Recent messages: ${messages.length}`);
  return lines;
}

function buildTaskChanges(params: any): JsonObject {
  const changes: JsonObject = {};
  copyDefined(changes, "status", normalizeString(params.status));
  copyDefined(changes, "assigned_to", normalizeString(params.assigned_to));
  copyDefined(changes, "title", normalizeString(params.title));
  copyDefined(changes, "description", normalizeString(params.description));
  copyDefined(changes, "priority", optionalNumber(params.priority));
  copyDefined(changes, "tags", Array.isArray(params.tags) ? params.tags : undefined);
  copyDefined(changes, "parent_id", optionalNumber(params.parent_id));
  return changes;
}

function copyDefined(target: JsonObject, key: string, value: unknown) {
  if (value !== undefined) target[key] = value;
}

function parseRequiredId(args: string | undefined, label: string): number {
  const first = args?.trim().split(/\s+/, 1)[0];
  const value = Number(first);
  if (!Number.isInteger(value) || value <= 0) throw new Error(`Expected ${label}.`);
  return value;
}

function parseIds(args: string | undefined): number[] {
  const values = (args ?? "")
    .split(/[,\s]+/)
    .map((value) => Number(value))
    .filter((value) => Number.isInteger(value) && value > 0);
  if (values.length === 0) throw new Error("Expected at least one message id.");
  return values;
}

function parseTaskScopedText(args: string | undefined, fallbackTaskId: number | undefined, label: string) {
  const scoped = parseOptionalTaskScopedText(args, fallbackTaskId);
  if (!scoped.text) throw new Error(`Expected ${label}.`);
  return scoped;
}

function parseOptionalTaskScopedText(args: string | undefined, fallbackTaskId: number | undefined) {
  const trimmed = args?.trim() ?? "";
  const match = trimmed.match(/^(\d+)(?:\s+([\s\S]*))?$/);
  if (match) {
    const taskId = Number(match[1]);
    if (!Number.isInteger(taskId) || taskId <= 0) throw new Error("Expected task id.");
    return { taskId, text: (match[2] ?? "").trim() };
  }
  if (!fallbackTaskId) throw new Error("No current Den task. Run /den-task <id> or pass a task id.");
  return { taskId: fallbackTaskId, text: trimmed };
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

function optionalNumber(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
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
