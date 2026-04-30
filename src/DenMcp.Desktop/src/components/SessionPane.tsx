import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
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
import { capabilitySummary, isSessionIdle, phaseLabel, recentActivityItems, sessionKey, sessionTitle } from '../sessionView';

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
}

export function SessionPane({ snapshots, workspaces = [] }: Props) {
  const sorted = [...snapshots].sort((a, b) => b.request.observed_at.localeCompare(a.request.observed_at));

  return (
    <section className="panel session-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Terminal/session control</p>
          <h2>Operator sessions</h2>
        </div>
        <span className="count-pill">{snapshots.length}</span>
      </div>
      <p className="muted">
        Direct PTY sessions are app-owned by the .NET sidecar and rendered locally with xterm.js. Raw terminal bytes stay on the local bridge.
      </p>
      <DirectTerminalWorkbench workspaces={workspaces} />
      {sorted.length === 0 ? (
        <div className="empty-state">
          <strong>No published session snapshots observed yet.</strong>
          <p>Create a direct PTY below or run Pi/sub-agent work under a synced project to populate this list.</p>
        </div>
      ) : (
        <div className="session-card-list">
          {sorted.slice(0, 12).map((snapshot) => <SessionCard key={sessionKey(snapshot)} snapshot={snapshot} />)}
        </div>
      )}
    </section>
  );
}

function DirectTerminalWorkbench({ workspaces }: { workspaces: LocalGitSnapshot[] }) {
  const [sessions, setSessions] = useState<TerminalSessionSummary[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [selectedWorkspaceKey, setSelectedWorkspaceKey] = useState<string>('');
  const [attach, setAttach] = useState<TerminalAttachState | null>(null);
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

  const refreshSessions = useCallback(async () => {
    const response = await terminalListSessions({ kind: 'terminal' });
    setSessions(response.sessions);
    setSelectedSessionId((current) => current ?? response.sessions.find((session) => session.backend === 'direct_pty')?.session_id ?? null);
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
    }).then((dispose) => { if (disposed) dispose(); else disposers.push(dispose); }).catch((err) => setError(errorMessage(err)));

    void onTerminalLifecycle((event: TerminalLifecycleEvent) => {
      setAttach((prev) => prev && prev.sessionId === event.session_id ? {
        ...prev,
        status: event.event === 'den.terminal.exit' ? 'exited' : prev.status,
        lastCursor: event.stream_cursor ?? prev.lastCursor,
        message: event.message ?? event.reason ?? (event.replay_gap ? 'replay gap detected' : prev.message),
      } : prev);
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
  }, []);

  const selectedSession = sessions.find((session) => session.session_id === selectedSessionId) ?? null;
  const selectedScope = scopes.find((scope) => scope.key === selectedWorkspaceKey)?.snapshot ?? scopes[0]?.snapshot ?? null;

  const ensureTerminal = useCallback(() => {
    if (terminalRef.current || !terminalHostRef.current) return terminalRef.current;
    const terminal = new Terminal({ cursorBlink: true, convertEol: true, fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace', fontSize: 12, theme: { background: '#08111f' } });
    const fit = new FitAddon();
    terminal.loadAddon(fit);
    terminal.open(terminalHostRef.current);
    terminal.onData((data) => {
      const current = attachRef.current;
      if (!current || current.status === 'exited' || current.status === 'failed') return;
      void terminalSendInput({ session_id: current.sessionId, stream_id: current.streamId, data, byte_count: new TextEncoder().encode(data).length })
        .catch((err) => setError(errorMessage(err)));
    });
    terminalRef.current = terminal;
    fitRef.current = fit;
    window.setTimeout(() => fit.fit(), 0);
    return terminal;
  }, []);

  const attachToSession = useCallback(async (sessionId: string, reconnect = false) => {
    setError(null);
    const terminal = ensureTerminal();
    if (!terminal) return;
    fitRef.current?.fit();
    const viewport = { cols: terminal.cols || 120, rows: terminal.rows || 32 };
    let response: TerminalAttachResponse;
    if (reconnect && attachRef.current?.lastCursor) {
      response = await terminalReconnect({ session_id: sessionId, previous_stream_id: attachRef.current.streamId, last_seen_cursor: attachRef.current.lastCursor, viewport });
    } else {
      response = await terminalAttach({ session_id: sessionId, mode: 'terminal_stream', viewport, replay: { after_cursor: attachRef.current?.lastCursor ?? null, max_bytes: 262144, max_chunks: 200 }, client_id: 'den-desktop-renderer' });
    }
    setAttach({
      sessionId,
      streamId: response.stream_id,
      lastCursor: response.start_cursor ?? null,
      ackAfterBytes: response.limits?.ack_after_bytes ?? 262144,
      unackedBytes: 0,
      status: 'attached',
      message: response.replay_gap ? 'replay gap detected; showing latest bounded tail' : null,
    });
    terminal.focus();
  }, [ensureTerminal]);

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
  }, []);

  const terminateCurrent = useCallback(async () => {
    const current = attachRef.current;
    if (!current) return;
    await terminalTerminate({ session_id: current.sessionId, stream_id: current.streamId, mode: 'graceful', reason: 'operator_requested', requested_by: 'desktop_renderer' });
  }, []);

  useEffect(() => {
    if (!terminalHostRef.current) return;
    const observer = new ResizeObserver(() => {
      if (!fitRef.current || !terminalRef.current) return;
      fitRef.current.fit();
      const current = attachRef.current;
      if (!current) return;
      void terminalResize({ session_id: current.sessionId, stream_id: current.streamId, cols: terminalRef.current.cols, rows: terminalRef.current.rows }).catch(() => undefined);
    });
    observer.observe(terminalHostRef.current);
    return () => observer.disconnect();
  }, []);

  useEffect(() => () => { terminalRef.current?.dispose(); }, []);

  return (
    <div className="direct-terminal-workbench">
      <div className="direct-terminal-toolbar">
        <select value={selectedWorkspaceKey} onChange={(event) => setSelectedWorkspaceKey(event.target.value)} aria-label="Direct terminal workspace">
          {scopes.length === 0 ? <option value="">No synced git workspace</option> : scopes.map((scope) => <option key={scope.key} value={scope.key}>{scope.label}</option>)}
        </select>
        <button type="button" onClick={() => void createDirectPty()} disabled={!selectedScope}>New direct PTY</button>
        <select value={selectedSessionId ?? ''} onChange={(event) => setSelectedSessionId(event.target.value || null)} aria-label="Operator session">
          <option value="">Select session…</option>
          {sessions.map((session) => <option key={session.session_id} value={session.session_id}>{session.display_name ?? session.title ?? session.session_id} · {session.backend} · {session.status}</option>)}
        </select>
        <button type="button" onClick={() => selectedSession && void attachToSession(selectedSession.session_id)} disabled={!selectedSession || !selectedSession.can_attach}>Attach</button>
        <button type="button" onClick={() => attach && void attachToSession(attach.sessionId, true)} disabled={!attach}>Reconnect</button>
        <button type="button" onClick={() => void detachCurrent()} disabled={!attach}>Detach</button>
        <button type="button" onClick={() => void terminateCurrent()} disabled={!attach}>Terminate</button>
      </div>
      <div className="terminal-state-line">
        <span>{attach ? `stream ${attach.streamId.slice(0, 16)} · ${attach.status}` : 'no attached terminal stream'}</span>
        {attach?.message ? <strong>{attach.message}</strong> : null}
        {error ? <strong className="terminal-error">{error}</strong> : null}
      </div>
      <div className="xterm-shell" ref={terminalHostRef} />
    </div>
  );
}

function SessionCard({ snapshot }: { snapshot: LocalSessionSnapshot }) {
  const activity = recentActivityItems(snapshot);
  const idle = isSessionIdle(snapshot);
  return (
    <article className={`session-card ${idle ? 'session-idle' : ''}`}>
      <div className="snapshot-topline">
        <div>
          <h3>{sessionTitle(snapshot)}</h3>
          <p className="path-line">{snapshot.request.cwd ?? snapshot.artifactRoot ?? 'session root unknown'}</p>
        </div>
        <div className="pill-stack">
          <span className={`status-pill status-${snapshot.request.status ?? snapshot.request.current_phase ?? 'observed'}`}>{phaseLabel(snapshot.request.status ?? snapshot.request.current_phase)}</span>
          <span className={`publish-pill publish-${snapshot.lastPublishStatus}`}>{snapshot.lastPublishStatus}</span>
        </div>
      </div>

      <div className="snapshot-meta">
        <span>project <strong>{snapshot.projectId}</strong></span>
        <span>workspace <strong>{snapshot.request.workspace_id ?? '—'}</strong></span>
        <span>backend <strong>{snapshot.request.backend ?? '—'}</strong></span>
        <span>command <strong>{snapshot.request.current_command ?? '—'}</strong></span>
        <span>capabilities <strong>{capabilitySummary(snapshot)}</strong></span>
        <span>observed <strong>{new Date(snapshot.request.observed_at).toLocaleTimeString()}</strong></span>
      </div>

      {snapshot.request.warnings.length > 0 && (
        <ul className="warning-list">
          {snapshot.request.warnings.map((warning, index) => <li key={`${warning}:${index}`}>{warning}</li>)}
        </ul>
      )}
      {snapshot.lastPublishError && <p className="error-note">{snapshot.lastPublishError}</p>}

      {activity.length > 0 && (
        <ol className="activity-list">
          {activity.map((item, index) => (
            <li key={`${item.timestamp ?? index}:${item.summary ?? item.tool ?? item.kind}`}>
              <span>{item.kind ?? item.role ?? 'activity'}</span>
              <p>{item.tool ? `${item.tool}: ` : ''}{item.summary ?? 'activity observed'}</p>
            </li>
          ))}
        </ol>
      )}
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
