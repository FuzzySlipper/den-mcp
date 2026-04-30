import { KeyboardEvent, MouseEvent, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { FitAddon } from '@xterm/addon-fit';
import { Terminal } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';
import {
  LocalGitSnapshot,
  LocalSessionSnapshot,
  onTerminalBackpressure,
  onTerminalLifecycle,
  onTerminalOutput,
  onTerminalSessionList,
  onTerminalStatus,
  terminalAckOutput,
  terminalAttach,
  terminalCreateSession,
  terminalDetach,
  terminalListSessions,
  terminalReconnect,
  terminalResize,
  terminalSendInput,
  terminalTerminate,
  TerminalAttachResponse,
  TerminalBackpressureEvent,
  TerminalLifecycleEvent,
  TerminalOutputEvent,
  TerminalSessionSummary,
  TerminalStatusEvent,
} from '../desktop/tauriApi';
import {
  buildTerminalSessionOverview,
  canAttachInline,
  relativeActivityLabel,
  terminalStatusLabel,
  type TerminalOverviewSession,
} from '../terminalSessionView';

interface Props {
  snapshots: LocalSessionSnapshot[];
  workspaces?: LocalGitSnapshot[];
}

interface TerminalAttachState {
  sessionId: string;
  streamId: string;
  lastCursor: string | null;
  ackAfterBytes: number;
  unackedBytes: number;
  status: string;
  message: string | null;
  canSendInput: boolean;
  canResize: boolean;
  canDetach: boolean;
  canTerminate: boolean;
}

interface ExternalAttachState {
  sessionId: string;
  command: string | null;
  description: string | null;
  copied: boolean;
}

export function SessionPane({ snapshots, workspaces = [] }: Props) {
  return <TerminalOverviewWorkbench snapshots={snapshots} workspaces={workspaces} />;
}

function TerminalOverviewWorkbench({ snapshots, workspaces = [] }: Props) {
  const [sessions, setSessions] = useState<TerminalSessionSummary[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [selectedWorkspaceKey, setSelectedWorkspaceKey] = useState<string>('');
  const [attach, setAttach] = useState<TerminalAttachState | null>(null);
  const [externalAttach, setExternalAttach] = useState<ExternalAttachState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const terminalHostRef = useRef<HTMLDivElement>(null);
  const terminalRef = useRef<Terminal | null>(null);
  const fitRef = useRef<FitAddon | null>(null);
  const attachRef = useRef<TerminalAttachState | null>(null);

  useEffect(() => { attachRef.current = attach; }, [attach]);

  const scopes = useMemo(() => workspaces.map((snapshot) => ({
    key: `${snapshot.scope.projectId}:${snapshot.scope.taskId ?? ''}:${snapshot.scope.workspaceId ?? ''}:${snapshot.scope.rootPath}`,
    label: `${snapshot.scope.projectId}${snapshot.scope.taskId ? ` #${snapshot.scope.taskId}` : ''} · ${snapshot.scope.rootPath}`,
    snapshot,
  })), [workspaces]);

  useEffect(() => {
    if (!selectedWorkspaceKey && scopes.length > 0) setSelectedWorkspaceKey(scopes[0].key);
  }, [scopes, selectedWorkspaceKey]);

  const overview = useMemo(() => buildTerminalSessionOverview(sessions, snapshots), [sessions, snapshots]);
  const selectedOverview = overview.find((session) => session.sessionId === selectedSessionId) ?? overview[0] ?? null;
  const selectedScope = scopes.find((scope) => scope.key === selectedWorkspaceKey)?.snapshot ?? scopes[0]?.snapshot ?? null;

  useEffect(() => {
    if (!selectedSessionId && overview.length > 0) {
      setSelectedSessionId(overview[0].sessionId);
    } else if (selectedSessionId && overview.length > 0 && !overview.some((session) => session.sessionId === selectedSessionId)) {
      setSelectedSessionId(overview[0].sessionId);
    }
  }, [overview, selectedSessionId]);

  const refreshSessions = useCallback(async () => {
    const response = await terminalListSessions();
    setSessions(response.sessions);
  }, []);

  useEffect(() => {
    void refreshSessions().catch((err) => setError(errorMessage(err)));
    let disposed = false;
    let disposeList: (() => void) | null = null;
    void onTerminalSessionList((event) => {
      if (disposed) return;
      setSessions(event.sessions);
    }).then((dispose) => { disposed ? dispose() : (disposeList = dispose); }).catch((err) => setError(errorMessage(err)));
    return () => { disposed = true; disposeList?.(); };
  }, [refreshSessions]);

  useEffect(() => {
    let disposed = false;
    const disposers: Array<() => void> = [];
    void onTerminalOutput((event) => {
      const current = attachRef.current;
      if (!current || event.stream_id !== current.streamId) return;
      terminalRef.current?.write(decodeTerminalData(event));
      setAttach((prev) => prev && prev.streamId === event.stream_id ? {
        ...prev,
        lastCursor: event.stream_cursor,
        unackedBytes: prev.unackedBytes + event.byte_count,
        message: event.truncated ? 'output chunk split by protocol bounds' : prev.message,
      } : prev);
      const nextBytes = current.unackedBytes + event.byte_count;
      if (nextBytes >= current.ackAfterBytes) {
        void terminalAckOutput({ session_id: current.sessionId, stream_id: current.streamId, ack_cursor: event.stream_cursor, received_bytes: nextBytes })
          .then(() => setAttach((prev) => prev && prev.streamId === event.stream_id ? { ...prev, unackedBytes: 0 } : prev))
          .catch((err) => setError(errorMessage(err)));
      }
    }).then((dispose) => { if (disposed) dispose(); else disposers.push(dispose); }).catch((err) => setError(errorMessage(err)));

    void onTerminalStatus((event: TerminalStatusEvent) => {
      setAttach((prev) => prev && prev.sessionId === event.session_id ? { ...prev, status: event.status ?? prev.status } : prev);
      void refreshSessions().catch(() => undefined);
    }).then((dispose) => { if (disposed) dispose(); else disposers.push(dispose); }).catch((err) => setError(errorMessage(err)));

    void onTerminalLifecycle((event: TerminalLifecycleEvent) => {
      setAttach((prev) => prev && prev.sessionId === event.session_id ? {
        ...prev,
        status: event.event === 'den.terminal.exit' ? 'exited' : prev.status,
        lastCursor: event.stream_cursor ?? prev.lastCursor,
        message: event.message ?? event.reason ?? (event.replay_gap ? 'replay gap detected' : prev.message),
      } : prev);
      void refreshSessions().catch(() => undefined);
    }).then((dispose) => { if (disposed) dispose(); else disposers.push(dispose); }).catch((err) => setError(errorMessage(err)));

    void onTerminalBackpressure((event: TerminalBackpressureEvent) => {
      setAttach((prev) => prev && prev.streamId === event.stream_id ? {
        ...prev,
        message: `backpressure ${event.state}: ${event.next_action ?? 'watching'} (${event.queue_bytes} queued bytes)`,
      } : prev);
      const current = attachRef.current;
      if (current && current.streamId === event.stream_id && event.next_action === 'ack_required') {
        void terminalAckOutput({ session_id: current.sessionId, stream_id: current.streamId, ack_cursor: current.lastCursor, received_bytes: current.unackedBytes })
          .then(() => setAttach((prev) => prev && prev.streamId === event.stream_id ? { ...prev, unackedBytes: 0 } : prev))
          .catch((err) => setError(errorMessage(err)));
      }
    }).then((dispose) => { if (disposed) dispose(); else disposers.push(dispose); }).catch((err) => setError(errorMessage(err)));

    return () => { disposed = true; disposers.forEach((dispose) => dispose()); };
  }, [refreshSessions]);

  const ensureTerminal = useCallback(() => {
    if (terminalRef.current || !terminalHostRef.current) return terminalRef.current;
    const terminal = new Terminal({ cursorBlink: true, convertEol: true, fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace', fontSize: 12, theme: { background: '#08111f' } });
    const fit = new FitAddon();
    terminal.loadAddon(fit);
    terminal.open(terminalHostRef.current);
    terminal.onData((data) => {
      const current = attachRef.current;
      if (!current || !current.canSendInput || current.status === 'exited' || current.status === 'failed') return;
      void terminalSendInput({ session_id: current.sessionId, stream_id: current.streamId, data, byte_count: new TextEncoder().encode(data).length })
        .catch((err) => setError(errorMessage(err)));
    });
    terminalRef.current = terminal;
    fitRef.current = fit;
    window.setTimeout(() => fit.fit(), 0);
    return terminal;
  }, []);

  const attachToSession = useCallback(async (sessionId: string, reconnect = false) => {
    const target = overview.find((session) => session.sessionId === sessionId);
    if (target && !canAttachInline(target)) {
      setSelectedSessionId(sessionId);
      setError('This session is read-only or does not expose a raw terminal stream.');
      return;
    }
    setSelectedSessionId(sessionId);
    setExternalAttach(null);
    setError(null);
    const terminal = ensureTerminal();
    if (!terminal) return;
    terminal.clear();
    fitRef.current?.fit();
    const viewport = { cols: terminal.cols || 120, rows: terminal.rows || 32 };
    const previous = attachRef.current;
    let response: TerminalAttachResponse;
    if (reconnect && previous?.lastCursor && previous.sessionId === sessionId) {
      response = await terminalReconnect({ session_id: sessionId, previous_stream_id: previous.streamId, last_seen_cursor: previous.lastCursor, viewport });
    } else {
      response = await terminalAttach({ session_id: sessionId, mode: 'terminal_stream', viewport, replay: { after_cursor: previous?.sessionId === sessionId ? previous.lastCursor : null, max_bytes: 262144, max_chunks: 200 }, client_id: 'den-desktop-renderer' });
    }
    setAttach({
      sessionId,
      streamId: response.stream_id,
      lastCursor: response.start_cursor ?? null,
      ackAfterBytes: response.limits?.ack_after_bytes ?? 262144,
      unackedBytes: 0,
      status: 'attached',
      message: response.replay_gap ? 'replay gap detected; showing latest bounded tail' : null,
      canSendInput: response.capabilities?.can_send_input ?? target?.capabilities.canSendInput ?? false,
      canResize: response.capabilities?.can_resize ?? target?.capabilities.canResize ?? false,
      canDetach: response.capabilities?.can_detach ?? target?.capabilities.canDetach ?? false,
      canTerminate: response.capabilities?.can_terminate ?? target?.capabilities.canTerminate ?? false,
    });
    await refreshSessions();
    terminal.focus();
  }, [ensureTerminal, overview, refreshSessions]);

  const requestExternalAttach = useCallback(async (session: TerminalOverviewSession) => {
    setSelectedSessionId(session.sessionId);
    setExternalAttach(null);
    setError(null);
    try {
      const response = await terminalAttach({ session_id: session.sessionId, mode: 'external_attach_info', client_id: 'den-desktop-renderer' });
      setExternalAttach({
        sessionId: session.sessionId,
        command: response.external_attach?.command ?? null,
        description: response.external_attach?.description ?? 'External attach information was requested from the sidecar.',
        copied: false,
      });
    } catch (err) {
      setError(errorMessage(err));
    }
  }, []);

  const createDirectPty = useCallback(async () => {
    if (!selectedScope) {
      setError('Select a synced workspace/git scope before creating a direct PTY.');
      return;
    }
    setError(null);
    const response = await terminalCreateSession({
      project_id: selectedScope.scope.projectId,
      task_id: selectedScope.scope.taskId,
      workspace_id: selectedScope.scope.workspaceId,
      cwd: selectedScope.scope.rootPath,
      title: `${selectedScope.scope.projectId}${selectedScope.scope.taskId ? ` #${selectedScope.scope.taskId}` : ''}`,
      backend: 'direct_pty',
    });
    await refreshSessions();
    setSelectedSessionId(response.session.session_id);
    await attachToSession(response.session.session_id);
  }, [attachToSession, refreshSessions, selectedScope]);

  const detachCurrent = useCallback(async () => {
    const current = attachRef.current;
    if (!current) return;
    await terminalDetach({ session_id: current.sessionId, stream_id: current.streamId, reason: 'operator_detached' });
    setAttach(null);
    await refreshSessions();
  }, [refreshSessions]);

  const terminateSession = useCallback(async (sessionId: string) => {
    const current = attachRef.current;
    await terminalTerminate({ session_id: sessionId, stream_id: current?.sessionId === sessionId ? current.streamId : null, mode: 'graceful', reason: 'operator_requested', requested_by: 'desktop_renderer' });
    await refreshSessions();
  }, [refreshSessions]);

  const copyExternalCommand = useCallback(async () => {
    if (!externalAttach?.command || typeof navigator === 'undefined' || !navigator.clipboard) return;
    await navigator.clipboard.writeText(externalAttach.command);
    setExternalAttach((current) => current ? { ...current, copied: true } : current);
  }, [externalAttach]);

  useEffect(() => {
    if (!terminalHostRef.current) return;
    const observer = new ResizeObserver(() => {
      if (!fitRef.current || !terminalRef.current) return;
      fitRef.current.fit();
      const current = attachRef.current;
      if (!current || !current.canResize) return;
      void terminalResize({ session_id: current.sessionId, stream_id: current.streamId, cols: terminalRef.current.cols, rows: terminalRef.current.rows }).catch(() => undefined);
    });
    observer.observe(terminalHostRef.current);
    return () => observer.disconnect();
  }, []);

  useEffect(() => () => { terminalRef.current?.dispose(); }, []);

  return (
    <section className="panel session-panel terminals-overview-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Terminal/session control</p>
          <h2>Operator sessions</h2>
        </div>
        <div className="terminal-counts">
          <span className="count-pill">{overview.length} sessions</span>
          <span className="count-pill">{overview.filter((session) => canAttachInline(session)).length} raw streams</span>
        </div>
      </div>
      <p className="muted">
        Overview-first control surface for direct PTY, tmux-backed, and observed-only sessions. Controls stay disabled unless the sidecar reports the matching OperatorSession capability.
      </p>

      <div className="terminal-create-strip">
        <label>
          <span>New direct PTY workspace</span>
          <select value={selectedWorkspaceKey} onChange={(event) => setSelectedWorkspaceKey(event.target.value)} aria-label="Direct terminal workspace">
            {scopes.length === 0 ? <option value="">No synced git workspace</option> : scopes.map((scope) => <option key={scope.key} value={scope.key}>{scope.label}</option>)}
          </select>
        </label>
        <button type="button" onClick={() => void createDirectPty()} disabled={!selectedScope}>New direct PTY</button>
      </div>

      <div className="terminal-workbench-grid">
        <div className="terminal-session-list" aria-label="Known operator sessions">
          {overview.length === 0 ? (
            <div className="empty-state">
              <strong>No known sessions observed yet.</strong>
              <p>Create a direct PTY or run Pi/sub-agent work under a synced project to populate this list.</p>
            </div>
          ) : overview.map((session) => (
            <TerminalSessionCard
              key={session.key}
              session={session}
              active={session.sessionId === selectedOverview?.sessionId}
              attached={attach?.sessionId === session.sessionId}
              onSelect={() => setSelectedSessionId(session.sessionId)}
              onAttach={() => void attachToSession(session.sessionId)}
              onReconnect={() => void attachToSession(session.sessionId, true)}
              onDetach={() => void detachCurrent()}
              onTerminate={() => void terminateSession(session.sessionId).catch((err) => setError(errorMessage(err)))}
              onExternalAttach={() => void requestExternalAttach(session)}
            />
          ))}
        </div>

        <aside className="terminal-attach-panel panel surface-panel" aria-live="polite">
          <div className="panel-heading">
            <div>
              <p className="eyebrow">Inline attach</p>
              <h2>{selectedOverview?.displayName ?? 'No session selected'}</h2>
            </div>
            {selectedOverview ? <span className={`status-pill status-${selectedOverview.statusTone}`}>{terminalStatusLabel(selectedOverview.status)}</span> : null}
          </div>

          {selectedOverview ? (
            <div className="terminal-selected-meta">
              <span>project <strong>{selectedOverview.projectId ?? '—'}</strong></span>
              <span>task <strong>{selectedOverview.taskId ? `#${selectedOverview.taskId}` : '—'}</strong></span>
              <span>workspace <strong>{selectedOverview.workspaceId ?? '—'}</strong></span>
              <span>backend <strong>{selectedOverview.backend}</strong></span>
              <span>last activity <strong>{relativeActivityLabel(selectedOverview.lastActivityAt ?? selectedOverview.lastObservedAt)}</strong></span>
              <span>authority <strong>{selectedOverview.authority === 'local' ? 'local sidecar' : 'observed only'}</strong></span>
            </div>
          ) : null}

          <div className="terminal-state-line">
            <span>{attach ? `stream ${attach.streamId.slice(0, 16)} · ${attach.status}` : 'no attached terminal stream'}</span>
            {attach?.message ? <strong>{attach.message}</strong> : null}
            {error ? <strong className="terminal-error">{error}</strong> : null}
          </div>

          {externalAttach ? (
            <div className="external-attach-box">
              <div>
                <strong>External attach command</strong>
                <p>{externalAttach.description ?? 'Copy this opaque command into a trusted terminal.'}</p>
              </div>
              {externalAttach.command ? <code>{externalAttach.command}</code> : <span className="muted">No external command returned for this session.</span>}
              <button type="button" className="secondary" onClick={() => void copyExternalCommand()} disabled={!externalAttach.command}>{externalAttach.copied ? 'Copied' : 'Copy command'}</button>
            </div>
          ) : null}

          {selectedOverview && !canAttachInline(selectedOverview) ? (
            <div className="empty-state calm-state">
              <strong>{selectedOverview.readOnly ? 'Read-only observed session' : 'No raw terminal stream'}</strong>
              <p>
                {selectedOverview.readOnly
                  ? 'This Pi artifact/session observation is preserved for context and structured activity, but direct PTY controls are intentionally unavailable.'
                  : 'The sidecar did not report both can_attach and can_stream_terminal, so inline xterm attach remains disabled.'}
              </p>
            </div>
          ) : null}

          <div className="xterm-shell" ref={terminalHostRef} />
        </aside>
      </div>
    </section>
  );
}

function TerminalSessionCard({
  session,
  active,
  attached,
  onSelect,
  onAttach,
  onReconnect,
  onDetach,
  onTerminate,
  onExternalAttach,
}: {
  session: TerminalOverviewSession;
  active: boolean;
  attached: boolean;
  onSelect: () => void;
  onAttach: () => void;
  onReconnect: () => void;
  onDetach: () => void;
  onTerminate: () => void;
  onExternalAttach: () => void;
}) {
  const inlineAttach = canAttachInline(session);
  const handleKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    event.preventDefault();
    if (inlineAttach) onAttach();
    else onSelect();
  };
  const stop = (event: MouseEvent<HTMLButtonElement>) => event.stopPropagation();

  return (
    <article
      className={`terminal-session-card ${active ? 'active' : ''} ${session.stale ? 'calm' : ''}`}
      role="button"
      tabIndex={0}
      onClick={() => { inlineAttach ? onAttach() : onSelect(); }}
      onKeyDown={handleKeyDown}
      aria-pressed={active}
      aria-label={`${session.displayName}, ${terminalStatusLabel(session.status)}, ${inlineAttach ? 'attachable' : 'read only'}`}
    >
      <div className="terminal-card-topline">
        <div>
          <h3>{session.displayName}</h3>
          <p className="path-line">{session.cwd ?? 'session root unknown'}</p>
        </div>
        <div className="pill-stack">
          {attached ? <span className="chip accent">attached</span> : null}
          {session.readOnly ? <span className="chip">read-only</span> : null}
          <span className={`status-pill status-${session.statusTone}`}>{terminalStatusLabel(session.status)}</span>
        </div>
      </div>

      <div className="snapshot-meta terminal-card-meta">
        <span>project <strong>{session.projectId ?? '—'}</strong></span>
        <span>task <strong>{session.taskId ? `#${session.taskId}` : '—'}</strong></span>
        <span>workspace <strong>{session.workspaceId ?? '—'}</strong></span>
        <span>backend <strong>{session.backend}</strong></span>
        <span>kind <strong>{session.kind}</strong></span>
        <span>last activity <strong>{relativeActivityLabel(session.lastActivityAt ?? session.lastObservedAt)}</strong></span>
      </div>

      <div className="terminal-capability-row">
        {session.capabilityLabels.map((label) => <span key={label} className="chip">{label}</span>)}
      </div>

      {session.currentCommand ? <p className="terminal-command-line">{session.currentCommand}</p> : null}

      {session.warnings.length > 0 ? (
        <ul className="warning-list terminal-warning-list">
          {session.warnings.map((warning, index) => <li key={`${warning}:${index}`}>{warning}</li>)}
        </ul>
      ) : null}

      {session.recentActivity.length > 0 ? (
        <ol className="activity-list terminal-card-activity">
          {session.recentActivity.slice(0, 2).map((item, index) => (
            <li key={`${item.timestamp ?? index}:${item.summary ?? item.tool ?? item.kind}`}>
              <span>{item.kind ?? item.role ?? 'activity'}</span>
              <p>{item.tool ? `${item.tool}: ` : ''}{item.summary ?? 'activity observed'}</p>
            </li>
          ))}
        </ol>
      ) : null}

      <div className="terminal-card-actions">
        <button type="button" onClick={(event) => { stop(event); onAttach(); }} disabled={!inlineAttach}>Attach inline</button>
        <button type="button" className="secondary" onClick={(event) => { stop(event); onReconnect(); }} disabled={!attached || !session.capabilities.canReconnect}>Reconnect</button>
        <button type="button" className="secondary" onClick={(event) => { stop(event); onDetach(); }} disabled={!attached || !session.capabilities.canDetach}>Detach</button>
        <button type="button" className="secondary" onClick={(event) => { stop(event); onTerminate(); }} disabled={!session.capabilities.canTerminate}>Terminate</button>
        <button type="button" className="secondary" onClick={(event) => { stop(event); onExternalAttach(); }} disabled={!session.capabilities.canOpenExternalAttach}>External attach</button>
      </div>
    </article>
  );
}

function decodeTerminalData(event: TerminalOutputEvent): Uint8Array | string {
  if (event.encoding !== 'base64') return event.data;
  const binary = window.atob(event.data);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
