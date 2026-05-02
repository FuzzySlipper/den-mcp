/**
 * Runtime hook for the Den Desktop app-agent bridge.
 *
 * Manages context/tool state for the Agent tab in observe/suggest mode.
 * All agent authority lives in .NET app-core; this hook only surfaces
 * the typed bridge facade for UI consumption.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  type AppAgentBuildContextResponse,
  type AppAgentCancelResponse,
  type AppAgentContextPacket,
  type AppAgentInvokeToolResponse,
  type AppAgentListToolsResponse,
  type AppAgentRunStateEvent,
  type AppAgentSelection,
  type AppAgentToolCallStateEvent,
  type AppAgentToolDefinition,
  appAgentBuildContext,
  appAgentCancelRequest,
  appAgentInvokeTool,
  appAgentListTools,
  onAppAgentRunState,
  onAppAgentToolCallState,
} from './sidecarBridgeApi';
import type { AppAgentBuildContextRequest, AppAgentInvokeToolRequest } from '../electron/sidecarProtocol.ts';
import type { JsonValue } from '../bridge/contract.ts';
import { normalizeAppAgentSelection } from './appAgentSelection.ts';

// ── Agent action log entry ──

export type AgentActionKind = 'context_loaded' | 'tool_invoked' | 'tool_completed' | 'tool_failed' | 'run_state' | 'suggestion' | 'cancel_requested' | 'error';

export interface AgentActionEntry {
  id: string;
  kind: AgentActionKind;
  label: string;
  detail?: string;
  toolName?: string;
  toolCallId?: string;
  status?: string;
  timestamp: string;
  cancellable?: boolean;
}

// ── Hook state ──

export interface AppAgentRuntimeState {
  /** Whether the initial context load is pending. */
  loading: boolean;
  /** Current context packet from the app-core bridge. */
  context: AppAgentContextPacket | null;
  /** Available tools from the authority model. */
  tools: AppAgentToolDefinition[];
  /** Chronological action log for observability. */
  actions: AgentActionEntry[];
  /** Most recent error message. */
  error: string | null;
  /** Whether an agent request is currently in-flight. */
  running: boolean;
  /** Whether there is an active cancellable request. */
  cancellable: boolean;
  /** Active request id for cancellation. */
  activeRequestId: string | null;
  /** Current run state from bridge events. */
  runState: string | null;
  /** Current tool call state from bridge events. */
  activeToolCall: AppAgentToolCallStateEvent | null;
  /** Load or refresh the context packet. */
  refreshContext: () => Promise<void>;
  /** Invoke a read/draft/summary tool in observe mode. */
  invokeTool: (toolName: string, input?: Record<string, JsonValue>) => Promise<AppAgentInvokeToolResponse>;
  /** Cancel the active request. */
  cancelActive: () => Promise<void>;
}

let actionSeq = 0;

function nextActionId(): string {
  actionSeq += 1;
  return `act_${Date.now()}_${actionSeq}`;
}

export function useAppAgentRuntime(
  selection: AppAgentSelection | null,
): AppAgentRuntimeState {
  const [loading, setLoading] = useState(true);
  const [context, setContext] = useState<AppAgentContextPacket | null>(null);
  const [tools, setTools] = useState<AppAgentToolDefinition[]>([]);
  const [actions, setActions] = useState<AgentActionEntry[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const [runState, setRunState] = useState<string | null>(null);
  const [activeToolCall, setActiveToolCall] = useState<AppAgentToolCallStateEvent | null>(null);
  const activeRequestRef = useRef<string | null>(null);
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const appendAction = useCallback((entry: Omit<AgentActionEntry, 'id' | 'timestamp'>) => {
    if (!mountedRef.current) return;
    setActions((prev) => [
      ...prev,
      { ...entry, id: nextActionId(), timestamp: new Date().toISOString() },
    ]);
  }, []);

  // ── Subscribe to bridge run/tool state events ──
  useEffect(() => {
    let disposed = false;
    let disposeRunState: (() => void) | null = null;
    let disposeToolCallState: (() => void) | null = null;

    void onAppAgentRunState((event: AppAgentRunStateEvent) => {
      if (disposed || !mountedRef.current) return;
      setRunState(event.status);
      appendAction({
        kind: 'run_state',
        label: `Agent run: ${event.status}`,
        detail: event.message ?? undefined,
        toolName: event.tool_name ?? undefined,
        status: event.status,
      });
      if (event.status === 'complete' || event.status === 'failed' || event.status === 'cancelled') {
        setRunning(false);
        activeRequestRef.current = null;
      }
    }).then((dispose) => {
      if (disposed) dispose();
      else disposeRunState = dispose;
    }).catch(() => {
      // Event subscription is optional; UI works without it
    });

    void onAppAgentToolCallState((event: AppAgentToolCallStateEvent) => {
      if (disposed || !mountedRef.current) return;
      setActiveToolCall(event.status === 'running' ? event : null);
      appendAction({
        kind: event.status === 'completed' ? 'tool_completed' : event.status === 'failed' ? 'tool_failed' : 'tool_invoked',
        label: `Tool: ${event.tool_name} — ${event.status}`,
        detail: event.target_summary ?? undefined,
        toolName: event.tool_name,
        toolCallId: event.tool_call_id,
        status: event.status,
        cancellable: event.cancellable,
      });
    }).then((dispose) => {
      if (disposed) dispose();
      else disposeToolCallState = dispose;
    }).catch(() => {
      // Event subscription is optional; UI works without it
    });

    return () => {
      disposed = true;
      disposeRunState?.();
      disposeToolCallState?.();
    };
  }, [appendAction]);

  // ── Load context and tools ──
  const refreshContext = useCallback(async () => {
    if (!selection) return;
    setLoading(true);
    setError(null);

    try {
      const normalizedSelection = normalizeAppAgentSelection(selection);
      const request: AppAgentBuildContextRequest = {
        selection: normalizedSelection,
        message_limit: 10,
      };
      const [contextResult, toolsResult] = await Promise.allSettled([
        appAgentBuildContext(request),
        appAgentListTools({ selection: normalizedSelection }),
      ]);

      if (!mountedRef.current) return;

      if (contextResult.status === 'fulfilled') {
        const response = contextResult.value;
        setContext(response.context);
        appendAction({
          kind: 'context_loaded',
          label: 'Context packet loaded',
          detail: response.context.task_summary
            ? `Task: ${response.context.task_summary.title} (${response.context.task_summary.status})`
            : `Built at ${response.context.built_at}`,
        });
      } else {
        const msg = contextResult.reason instanceof Error ? contextResult.reason.message : String(contextResult.reason);
        setError(msg);
        appendAction({ kind: 'error', label: 'Context load failed', detail: msg });
      }

      if (toolsResult.status === 'fulfilled') {
        const response = toolsResult.value;
        setTools(response.tools);
      }
    } catch (err) {
      if (mountedRef.current) {
        const msg = err instanceof Error ? err.message : String(err);
        setError(msg);
        appendAction({ kind: 'error', label: 'Context refresh failed', detail: msg });
      }
    } finally {
      if (mountedRef.current) {
        setLoading(false);
      }
    }
  }, [selection, appendAction]);

  // Stable ref to refreshContext so the auto-load effect can call the latest
  // version without adding refreshContext to its dependency array, which would
  // cause unnecessary re-triggers whenever non-key selection fields change.
  const refreshContextRef = useRef(refreshContext);
  refreshContextRef.current = refreshContext;

  // Auto-load when selection key fields change
  useEffect(() => {
    if (selection) {
      void refreshContextRef.current();
    }
  }, [selection?.project_id, selection?.task_id, selection?.workspace_id, selection?.session_id]);

  // ── Invoke tool ──
  const invokeTool = useCallback(async (
    toolName: string,
    input: Record<string, JsonValue> = {},
  ): Promise<AppAgentInvokeToolResponse> => {
    if (!selection) throw new Error('No selection context available.');
    setRunning(true);
    setError(null);

    const requestId = nextActionId();
    activeRequestRef.current = requestId;

    try {
      const request: AppAgentInvokeToolRequest = {
        tool_name: toolName,
        input,
        selection: normalizeAppAgentSelection(selection),
      };
      const response = await appAgentInvokeTool(request);

      if (!mountedRef.current) return response;

      appendAction({
        kind: response.status === 'completed' ? 'tool_completed' : 'tool_failed',
        label: `${toolName}: ${response.status}`,
        detail: formatToolResult(response),
        toolName: response.tool_name,
        toolCallId: response.tool_call_id,
        status: response.status,
      });

      return response;
    } catch (err) {
      if (mountedRef.current) {
        const msg = err instanceof Error ? err.message : String(err);
        setError(msg);
        appendAction({ kind: 'tool_failed', label: `Tool ${toolName} failed`, detail: msg, toolName });
      }
      throw err;
    } finally {
      if (mountedRef.current && activeRequestRef.current === requestId) {
        setRunning(false);
        activeRequestRef.current = null;
      }
    }
  }, [selection, appendAction]);

  // ── Cancel active request ──
  const cancelActive = useCallback(async () => {
    if (!activeRequestRef.current) return;
    try {
      const result = await appAgentCancelRequest({
        request_id: activeRequestRef.current,
        reason: 'Operator cancelled from Agent tab',
      });

      appendAction({
        kind: 'cancel_requested',
        label: `Cancel requested: ${result.status}`,
        detail: result.accepted ? 'Cancellation accepted.' : 'Request not found or already completed.',
      });
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      appendAction({ kind: 'error', label: 'Cancel failed', detail: msg });
    }
  }, [appendAction]);

  return useMemo(() => ({
    loading,
    context,
    tools,
    actions,
    error,
    running,
    cancellable: running && activeToolCall?.cancellable === true,
    activeRequestId: activeRequestRef.current,
    runState,
    activeToolCall,
    refreshContext,
    invokeTool,
    cancelActive,
  }), [loading, context, tools, actions, error, running, runState, activeToolCall, refreshContext, invokeTool, cancelActive]);
}

// ── Helpers ──

function formatToolResult(response: AppAgentInvokeToolResponse): string {
  if (response.status !== 'completed') return response.status;
  const result = response.result as Record<string, unknown> | undefined;
  if (!result) return 'completed (no result)';
  // Try to produce a brief summary from the result
  if (typeof result.summary === 'string') return result.summary;
  if (typeof result.draft_only === 'boolean' && result.draft_only) return `Draft produced (${response.tool_name})`;
  if (typeof result.count === 'number') return `${result.count} items`;
  const keys = Object.keys(result);
  if (keys.length <= 3) return JSON.stringify(result).slice(0, 200);
  return `Result with ${keys.length} fields`;
}
