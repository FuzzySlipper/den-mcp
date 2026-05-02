/**
 * Agent tab pane — observe/suggest mode.
 *
 * Displays curated context from the .NET app-core agent bridge,
 * visible tool calls and action log, and propose-only suggestions.
 * All agent authority lives in app-core; this component never
 * directly executes commands or writes to Den.
 */
import { useMemo, useState } from 'react';
import {
  type AppAgentContextPacket,
  type AppAgentSelection,
  type AppAgentToolCallStateEvent,
  type AppAgentToolDefinition,
} from '../desktop/sidecarBridgeApi';
import type { JsonValue } from '../bridge/contract.ts';
import {
  type AgentActionEntry,
  useAppAgentRuntime,
} from '../desktop/useAppAgentRuntime';

interface AgentPaneProps {
  selection: AppAgentSelection | null;
}

export function AgentPane({ selection }: AgentPaneProps) {
  const runtime = useAppAgentRuntime(selection);
  const [showContextDetail, setShowContextDetail] = useState(false);

  return (
    <div className="agent-tab tab-stack">
      <section className="tab-intro panel surface-panel">
        <div>
          <p className="eyebrow">Agent · observe/suggest</p>
          <h2>App-level agent context and suggestions</h2>
          <p className="muted">
            Curated context for the active project/task/workspace, visible tool calls,
            and read-only suggestions. Execution controls are disabled until session backends are ready.
          </p>
        </div>
        <AgentStatusBar
          runState={runtime.runState}
          running={runtime.running}
          loading={runtime.loading}
          error={runtime.error}
          onRefresh={runtime.refreshContext}
          onCancel={runtime.cancelActive}
          cancellable={runtime.cancellable}
        />
      </section>

      {runtime.loading && !runtime.context ? (
        <AgentLoadingState />
      ) : runtime.context ? (
        <div className="agent-content-grid">
          <AgentContextSummary
            context={runtime.context}
            showDetail={showContextDetail}
            onToggleDetail={() => setShowContextDetail((prev) => !prev)}
          />
          <AgentToolPanel
            tools={runtime.tools}
            activeToolCall={runtime.activeToolCall}
            onInvokeTool={runtime.invokeTool}
            running={runtime.running}
          />
          <AgentActionLog actions={runtime.actions} />
          <AgentSuggestionPanel
            context={runtime.context}
            tools={runtime.tools}
            onInvokeTool={runtime.invokeTool}
            running={runtime.running}
          />
        </div>
      ) : (
        <AgentEmptyState error={runtime.error} onRefresh={runtime.refreshContext} />
      )}
    </div>
  );
}

// ── Status bar ──

function AgentStatusBar({
  runState,
  running,
  loading,
  error,
  onRefresh,
  onCancel,
  cancellable,
}: {
  runState: string | null;
  running: boolean;
  loading: boolean;
  error: string | null;
  onRefresh: () => void;
  onCancel: () => void;
  cancellable: boolean;
}) {
  const stateLabel = runState
    ? `run: ${runState}`
    : loading
      ? 'loading context'
      : 'idle';
  const stateClass = runState === 'failed' || error
    ? 'err'
    : running
      ? 'running'
      : loading
        ? 'running'
        : 'ok';

  return (
    <div className="agent-status-bar">
      <span className={`agent-status-pill ${stateClass}`}>
        <span className="pill-dot" />
        {stateLabel}
      </span>
      {error ? <span className="agent-status-error" title={error}>{error}</span> : null}
      <div className="agent-status-actions">
        <button
          type="button"
          className="agent-action-btn"
          onClick={() => void onRefresh()}
          disabled={loading}
          title="Refresh context packet"
        >
          ⟳ Refresh
        </button>
        {cancellable ? (
          <button
            type="button"
            className="agent-action-btn agent-action-cancel"
            onClick={() => void onCancel()}
            title="Cancel active request"
          >
            ■ Cancel
          </button>
        ) : null}
      </div>
    </div>
  );
}

// ── Context summary ──

function AgentContextSummary({
  context,
  showDetail,
  onToggleDetail,
}: {
  context: AppAgentContextPacket;
  showDetail: boolean;
  onToggleDetail: () => void;
}) {
  const task = context.task_summary;
  const snapshot = context.git_snapshot.selected_snapshot;

  return (
    <section className="agent-panel agent-context-panel panel surface-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Context packet</p>
          <h3>Curated agent context</h3>
        </div>
        <span className="chip">v{context.context_version}</span>
      </div>

      {/* Selection */}
      <div className="agent-context-section">
        <h4>Selection</h4>
        <div className="agent-context-kv">
          {context.selection.project_id ? <span><strong>Project:</strong> {context.selection.project_id}</span> : null}
          {context.selection.task_id != null ? <span><strong>Task:</strong> #{context.selection.task_id}</span> : null}
          {context.selection.workspace_id ? <span><strong>Workspace:</strong> {context.selection.workspace_id}</span> : null}
          {context.selection.session_id ? <span><strong>Session:</strong> {context.selection.session_id}</span> : null}
          {context.selection.selected_file_path ? <span><strong>File:</strong> {context.selection.selected_file_path}</span> : null}
          {!context.selection.project_id && !context.selection.task_id ? (
            <span className="muted">No active selection — context is general.</span>
          ) : null}
        </div>
      </div>

      {/* Task summary */}
      {task ? (
        <div className="agent-context-section">
          <h4>Task</h4>
          <div className="agent-task-header">
            <strong>{task.title}</strong>
            <span className={`agent-task-status status-${task.status}`}>{task.status}</span>
          </div>
          <div className="agent-context-kv">
            <span><strong>Priority:</strong> {task.priority}</span>
            {task.tags.length > 0 ? <span><strong>Tags:</strong> {task.tags.join(', ')}</span> : null}
            <span><strong>Review:</strong> {task.review_state}</span>
          </div>
          {task.dependencies.length > 0 ? (
            <div className="agent-deps">
              <h5>Dependencies ({task.dependencies.length})</h5>
              {task.dependencies.map((dep) => (
                <span key={dep.task_id} className="agent-dep-chip">
                  #{dep.task_id} {dep.title ?? ''} <span className={`dep-status ${dep.status ?? ''}`}>{dep.status ?? '?'}</span>
                </span>
              ))}
            </div>
          ) : null}
          {task.open_review_findings.length > 0 ? (
            <div className="agent-findings">
              <h5>Open review findings ({task.open_review_findings.length})</h5>
              {task.open_review_findings.map((finding) => (
                <div key={finding.id ?? finding.summary} className="agent-finding-row">
                  <span className={`finding-cat ${finding.category ?? ''}`}>{finding.category ?? 'unknown'}</span>
                  <span>{finding.summary}</span>
                </div>
              ))}
            </div>
          ) : null}
        </div>
      ) : null}

      {/* Git snapshot */}
      {snapshot ? (
        <div className="agent-context-section">
          <h4>Git snapshot</h4>
          <div className="agent-context-kv">
            <span><strong>Branch:</strong> {snapshot.request.branch ?? '(detached)'}</span>
            {snapshot.request.head_sha ? <span><strong>Head:</strong> <code>{snapshot.request.head_sha.slice(0, 8)}</code></span> : null}
            <span><strong>State:</strong> {snapshot.request.state}</span>
            <span><strong>Dirty:</strong> {snapshot.request.dirty_counts.total} files</span>
          </div>
        </div>
      ) : null}

      {/* Sessions */}
      {context.session_summaries.length > 0 ? (
        <div className="agent-context-section">
          <h4>Sessions ({context.session_summaries.length})</h4>
          {context.session_summaries.map((session) => (
            <div key={session.session_id} className="agent-session-row">
              <strong>{session.title ?? session.display_name ?? session.session_id}</strong>
              <span className={`session-status ${session.status}`}>{session.status}</span>
              <span className="muted">{session.kind} · {session.backend}</span>
            </div>
          ))}
        </div>
      ) : null}

      {/* Messages */}
      {task && task.recent_messages.length > 0 ? (
        <div className="agent-context-section">
          <h4>Recent messages ({task.recent_messages.length})</h4>
          {task.recent_messages.map((msg) => (
            <div key={msg.id} className="agent-message-row">
              <span className="msg-sender">{msg.sender}</span>
              {msg.metadata_type ? <span className={`msg-type ${msg.metadata_type}`}>{msg.metadata_type}</span> : null}
              <span className="msg-preview">{msg.content_summary}</span>
            </div>
          ))}
        </div>
      ) : null}

      {/* Warnings */}
      {context.warnings.length > 0 ? (
        <div className="agent-context-section agent-warnings">
          <h4>Warnings</h4>
          {context.warnings.map((warning, i) => (
            <span key={i} className="agent-warning-chip">⚠ {warning}</span>
          ))}
        </div>
      ) : null}

      {/* Detail toggle */}
      <div className="agent-context-section">
        <button type="button" className="agent-detail-toggle" onClick={onToggleDetail}>
          {showDetail ? '▾ Hide authority & audit' : '▸ Show authority & audit'}
        </button>
        {showDetail ? (
          <div className="agent-context-detail">
            <AgentAuthorityPanel authority={context.authority} />
            <AgentAuditPanel audit={context.audit} />
          </div>
        ) : null}
      </div>

      <div className="agent-context-footer">
        <span className="muted">Built at {new Date(context.built_at).toLocaleString()}</span>
      </div>
    </section>
  );
}

// ── Authority panel ──

function AgentAuthorityPanel({
  authority,
}: {
  authority: AppAgentContextPacket['authority'];
}) {
  return (
    <div className="agent-authority-subpanel">
      <h5>Authority hints</h5>
      <div className="agent-context-kv">
        <span><strong>Sandbox:</strong> {authority.sandbox_scope}</span>
        <span><strong>Cancel:</strong> {authority.cancel_available ? 'available' : 'unavailable'}</span>
        <span><strong>Stop:</strong> {authority.stop_available ? 'available' : 'unavailable'}</span>
      </div>
      {authority.allowed_tools.length > 0 ? (
        <div className="agent-tools-list">
          <h6>Allowed tools ({authority.allowed_tools.length})</h6>
          {authority.allowed_tools.map((tool) => (
            <span key={tool.name} className={`agent-tool-chip cat-${tool.category}`}>
              {tool.name}
              {tool.destructive ? <span className="tool-destructive-badge">destructive</span> : null}
            </span>
          ))}
        </div>
      ) : null}
      {authority.disabled_tools.length > 0 ? (
        <div className="agent-tools-list disabled">
          <h6>Disabled tools ({authority.disabled_tools.length})</h6>
          {authority.disabled_tools.map((tool) => (
            <span key={tool.name} className="agent-tool-chip disabled" title={tool.reason}>
              {tool.name}
            </span>
          ))}
        </div>
      ) : null}
    </div>
  );
}

// ── Audit panel ──

function AgentAuditPanel({
  audit,
}: {
  audit: AppAgentContextPacket['audit'];
}) {
  return (
    <div className="agent-audit-subpanel">
      <h5>Audit correlation</h5>
      <div className="agent-context-kv">
        <span><strong>Run ID:</strong> <code>{audit.agent_run_id}</code></span>
        <span><strong>Trace:</strong> <code>{audit.trace_id}</code></span>
        {audit.project_id ? <span><strong>Project:</strong> {audit.project_id}</span> : null}
        {audit.task_id != null ? <span><strong>Task:</strong> #{audit.task_id}</span> : null}
        {audit.parent_request_id ? <span><strong>Parent:</strong> <code>{audit.parent_request_id}</code></span> : null}
      </div>
    </div>
  );
}

// ── Tool panel ──

function AgentToolPanel({
  tools,
  activeToolCall,
  onInvokeTool,
  running,
}: {
  tools: AppAgentToolDefinition[];
  activeToolCall: AppAgentToolCallStateEvent | null;
  onInvokeTool: (toolName: string, input?: Record<string, JsonValue>) => Promise<unknown>;
  running: boolean;
}) {
  // Group tools by category
  const byCategory = useMemo(() => {
    const groups = new Map<string, AppAgentToolDefinition[]>();
    for (const tool of tools) {
      const list = groups.get(tool.category) ?? [];
      list.push(tool);
      groups.set(tool.category, list);
    }
    return groups;
  }, [tools]);

  return (
    <section className="agent-panel agent-tools-panel panel surface-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Tool surface</p>
          <h3>Available tools</h3>
        </div>
        <span className="chip">{tools.filter((t) => t.enabled).length} enabled</span>
      </div>

      {activeToolCall ? (
        <div className="agent-active-tool-call">
          <span className="agent-tool-spinner" aria-hidden="true">⟳</span>
          <span><strong>{activeToolCall.tool_name}</strong> running</span>
          {activeToolCall.target_summary ? <span className="muted">{activeToolCall.target_summary}</span> : null}
        </div>
      ) : null}

      {[...byCategory.entries()].map(([category, categoryTools]) => (
        <div key={category} className="agent-tool-category">
          <h4>{category}</h4>
          {categoryTools.map((tool) => (
            <AgentToolRow
              key={tool.name}
              tool={tool}
              onInvoke={onInvokeTool}
              disabled={running || !tool.enabled}
            />
          ))}
        </div>
      ))}

      {tools.length === 0 ? (
        <div className="agent-empty-hint">No tools available — refresh context to load the tool registry.</div>
      ) : null}
    </section>
  );
}

function AgentToolRow({
  tool,
  onInvoke,
  disabled,
}: {
  tool: AppAgentToolDefinition;
  onInvoke: (toolName: string, input?: Record<string, JsonValue>) => Promise<unknown>;
  disabled: boolean;
}) {
  const isSafeReadOnly = tool.category === 'read' || tool.category === 'draft';

  const handleInvoke = () => {
    if (!tool.enabled || disabled) return;
    void onInvoke(tool.name, {});
  };

  return (
    <div className={`agent-tool-row ${tool.enabled ? '' : 'disabled'} ${tool.destructive ? 'destructive' : ''}`}>
      <div className="agent-tool-info">
        <strong>{tool.display_name}</strong>
        <span className="agent-tool-name">({tool.name})</span>
        <span className="muted">{tool.description}</span>
      </div>
      <div className="agent-tool-meta">
        {tool.destructive ? <span className="tool-badge destructive">destructive</span> : null}
        {tool.requires_confirmation ? <span className="tool-badge confirm">confirm</span> : null}
        {tool.requires_explicit_target ? <span className="tool-badge target">needs target</span> : null}
        {!tool.enabled ? <span className="tool-badge disabled" title={tool.disabled_reason ?? 'disabled'}>disabled</span> : null}
      </div>
      <div className="agent-tool-actions">
        {tool.enabled && isSafeReadOnly ? (
          <button
            type="button"
            className="agent-tool-btn"
            onClick={handleInvoke}
            disabled={disabled}
            title={`Invoke ${tool.name}`}
          >
            Run
          </button>
        ) : tool.enabled && !isSafeReadOnly ? (
          <span className="agent-tool-gated" title="Action tools are gated in observe/suggest mode">
            🔒 gated
          </span>
        ) : null}
      </div>
    </div>
  );
}

// ── Action log ──

function AgentActionLog({ actions }: { actions: AgentActionEntry[] }) {
  if (actions.length === 0) {
    return (
      <section className="agent-panel agent-log-panel panel surface-panel">
        <div className="panel-heading">
          <div>
            <p className="eyebrow">Observability</p>
            <h3>Action log</h3>
          </div>
        </div>
        <div className="agent-empty-hint">Agent actions, tool calls, and events will appear here.</div>
      </section>
    );
  }

  return (
    <section className="agent-panel agent-log-panel panel surface-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Observability</p>
          <h3>Action log</h3>
        </div>
        <span className="chip">{actions.length}</span>
      </div>
      <div className="agent-log-entries" aria-live="polite">
        {actions.slice().reverse().map((entry) => (
          <div key={entry.id} className={`agent-log-entry kind-${entry.kind}`}>
            <span className="agent-log-time">{formatTimestamp(entry.timestamp)}</span>
            <span className={`agent-log-kind ${entry.kind}`}>{kindIcon(entry.kind)}</span>
            <span className="agent-log-label">{entry.label}</span>
            {entry.toolName ? <span className="agent-log-tool">{entry.toolName}</span> : null}
            {entry.detail ? <span className="agent-log-detail" title={entry.detail}>{entry.detail}</span> : null}
          </div>
        ))}
      </div>
    </section>
  );
}

// ── Suggestion panel ──

function AgentSuggestionPanel({
  context,
  tools,
  onInvokeTool,
  running,
}: {
  context: AppAgentContextPacket;
  tools: AppAgentToolDefinition[];
  onInvokeTool: (toolName: string, input?: Record<string, JsonValue>) => Promise<unknown>;
  running: boolean;
}) {
  const suggestions = useMemo(() => {
    const result: { id: string; label: string; detail: string; tool: string; input?: Record<string, JsonValue> }[] = [];

    const hasTask = context.task_summary != null;
    const hasMessages = hasTask && (context.task_summary!.recent_messages.length > 0);
    const hasSessions = context.session_summaries.length > 0;
    const hasCommands = context.command_summaries.length > 0;

    // Context summary suggestion
    if (hasTask) {
      result.push({
        id: 'summarize-context',
        label: 'Summarize current task context',
        detail: `Summarize the state of task #${context.task_summary!.id}: ${context.task_summary!.title}`,
        tool: 'get_context',
      });
    }

    // List sessions suggestion
    if (hasSessions) {
      result.push({
        id: 'list-sessions',
        label: 'List active sessions',
        detail: `Show ${context.session_summaries.length} active session(s) and their capabilities`,
        tool: 'list_sessions',
      });
    }

    // Draft message suggestion
    if (hasTask && hasMessages) {
      result.push({
        id: 'draft-message',
        label: 'Draft a task thread message',
        detail: 'Produce a draft message for the task thread without sending it',
        tool: 'draft_den_message',
        input: { content: '' },
      });
    }

    // List console commands
    if (hasCommands) {
      result.push({
        id: 'list-commands',
        label: 'List available console commands',
        detail: `Show ${context.command_summaries.length} registered console commands`,
        tool: 'list_console_commands',
      });
    }

    // Den messages suggestion
    if (hasTask && hasMessages) {
      result.push({
        id: 'list-messages',
        label: 'Read recent Den messages',
        detail: `Load the latest messages for ${context.selection.project_id ?? 'project'} / task #${context.task_summary!.id}`,
        tool: 'list_den_messages',
        input: { project_id: context.selection.project_id ?? null, task_id: context.selection.task_id ?? null },
      });
    }

    // Draft task update suggestion
    if (hasTask) {
      result.push({
        id: 'draft-update',
        label: 'Draft task status update',
        detail: 'Suggest task status/description changes without applying them',
        tool: 'draft_task_update',
        input: {},
      });
    }

    return result;
  }, [context]);

  if (suggestions.length === 0) {
    return (
      <section className="agent-panel agent-suggest-panel panel surface-panel">
        <div className="panel-heading">
          <div>
            <p className="eyebrow">Suggestions</p>
            <h3>Proposed next steps</h3>
          </div>
        </div>
        <div className="agent-empty-hint">Load a task context to see agent suggestions.</div>
      </section>
    );
  }

  return (
    <section className="agent-panel agent-suggest-panel panel surface-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Suggestions</p>
          <h3>Proposed next steps</h3>
        </div>
        <span className="chip accent">read-only</span>
      </div>
      <div className="agent-suggestions">
        {suggestions.map((suggestion) => {
          const toolDef = tools.find((t) => t.name === suggestion.tool);
          const canRun = toolDef?.enabled === true && !running;
          return (
            <div key={suggestion.id} className="agent-suggestion-row">
              <div className="agent-suggestion-info">
                <strong>{suggestion.label}</strong>
                <span className="muted">{suggestion.detail}</span>
              </div>
              <button
                type="button"
                className="agent-suggestion-btn"
                onClick={() => void onInvokeTool(suggestion.tool, suggestion.input)}
                disabled={!canRun}
                title={canRun ? `Run ${suggestion.tool}` : 'Tool not available'}
              >
                {running ? '…' : 'Run'}
              </button>
            </div>
          );
        })}
      </div>
    </section>
  );
}

// ── Empty states ──

function AgentLoadingState() {
  return (
    <section className="panel surface-panel agent-loading-state">
      <div className="agent-loading-spinner" aria-hidden="true">◎</div>
      <p>Loading agent context…</p>
    </section>
  );
}

function AgentEmptyState({ error, onRefresh }: { error: string | null; onRefresh: () => void }) {
  return (
    <section className="panel surface-panel agent-empty-state">
      <p className="eyebrow">Agent</p>
      <h2>No context loaded</h2>
      <p className="muted">
        {error
          ? `Context load failed: ${error}`
          : 'Select a project/task/workspace to load agent context, or refresh to build the context packet.'}
      </p>
      <button type="button" className="agent-action-btn" onClick={() => void onRefresh()}>
        ⟳ Refresh context
      </button>
    </section>
  );
}

// ── Helpers ──

function formatTimestamp(isoString: string): string {
  const d = new Date(isoString);
  if (Number.isNaN(d.getTime())) return '--:--:--';
  return d.toLocaleTimeString();
}

function kindIcon(kind: AgentActionEntry['kind']): string {
  switch (kind) {
    case 'context_loaded': return '◉';
    case 'tool_invoked': return '▶';
    case 'tool_completed': return '✓';
    case 'tool_failed': return '✗';
    case 'run_state': return '◈';
    case 'suggestion': return '💡';
    case 'cancel_requested': return '■';
    case 'error': return '⚠';
  }
}
