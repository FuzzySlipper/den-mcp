import { useCallback, useState } from 'react';
import type {
  DenCollaborationAnnotationType,
  DenCollaborationSegment,
  DenCollaborationAnnotation,
  DenCollaborationTurn,
} from '../desktop/denCollaborationApi';
import type { CollaborationState, CollaborationActions, CollaborationDeliveryResult } from '../desktop/useCollaborationState';

interface Props {
  state: CollaborationState;
  actions: CollaborationActions;
}

export function CollaborationPane({ state, actions }: Props) {
  const { sessions, selectedSession, selectedTurn, loading, error, compiledResponse, showCompiled, deliveryResult, delivering } = state;
  const { selectSession, selectTurn, addAnnotation, updateAnnotation, deleteAnnotation, toggleCompiled, deliverCompiledResponse, clearError } = actions;

  const activeSessions = sessions.filter((s) => s.status === 'active');
  const resolvedSessions = sessions.filter((s) => s.status !== 'active');

  return (
    <div className="collaboration-tab tab-stack">
      <section className="collaboration-hero panel surface-panel">
        <div>
          <p className="eyebrow">Collaboration</p>
          <h2>Annotation sessions</h2>
          <p className="muted">
            Den-backed collaboration sessions with inline segment annotations. Select a session to view
            segmented markdown turns and add annotations.
          </p>
        </div>
        <div className="hero-status">
          <span className="count-pill">{sessions.length} sessions</span>
          {loading && <span className="status-pill status-running">loading</span>}
        </div>
      </section>

      {error && (
        <div className="collaboration-error">
          <span className="error-note">{error}</span>
          <button className="btn-secondary" onClick={clearError}>dismiss</button>
        </div>
      )}

      <div className="collaboration-workbench">
        {/* Session list sidebar */}
        <aside className="collaboration-session-list panel">
          <div className="panel-heading">
            <p className="eyebrow">Sessions</p>
            {activeSessions.length > 0 && <span className="status-pill status-running">{activeSessions.length} active</span>}
          </div>
          {sessions.length === 0 && !loading && (
            <p className="muted">No collaboration sessions yet.</p>
          )}
          {activeSessions.map((session) => (
            <SessionCard
              key={session.id}
              session={session}
              active={selectedSession?.id === session.id}
              onSelect={() => selectSession(session.id)}
            />
          ))}
          {resolvedSessions.length > 0 && (
            <>
              <div className="collaboration-section-break">
                <span className="eyebrow">Resolved / archived</span>
              </div>
              {resolvedSessions.slice(0, 5).map((session) => (
                <SessionCard
                  key={session.id}
                  session={session}
                  active={selectedSession?.id === session.id}
                  onSelect={() => selectSession(session.id)}
                />
              ))}
            </>
          )}
        </aside>

        {/* Session detail area */}
        <main className="collaboration-detail">
          {!selectedSession ? (
            <div className="panel surface-panel collaboration-empty">
              <p className="muted">Select a session from the list to view turns and add annotations.</p>
            </div>
          ) : !selectedTurn ? (
            <div className="panel surface-panel collaboration-empty">
              <p className="muted">This session has no turns yet.</p>
            </div>
          ) : (
            <>
              {/* Turn selector */}
              {renderTurnSelector(selectedSession.turns ?? [], selectedTurn, selectTurn)}

              {/* Segments with annotation UI */}
              <div className="collaboration-segments">
                {(selectedTurn.segments ?? []).map((segment) => (
                  <SegmentRenderer
                    key={segment.id}
                    segment={segment}
                    annotations={(selectedSession.annotations ?? []).filter(
                      (a) => a.segment_id === segment.id && a.turn_id === selectedTurn.id,
                    )}
                    sessionId={selectedSession.id}
                    turnId={selectedTurn.id}
                    onAddAnnotation={(type, body) => addAnnotation(segment.id, type, body)}
                    onUpdateAnnotation={(ann, type, body) => updateAnnotation(ann, type, body)}
                    onDeleteAnnotation={(ann) => deleteAnnotation(ann)}
                  />
                ))}
              </div>

              {/* Toolbar with compile action */}
              <div className="collaboration-toolbar">
                <span className="collaboration-annotation-count">
                  {(selectedSession.annotations ?? []).filter((a) => a.turn_id === selectedTurn.id).length} annotation(s)
                </span>
                <div className="collaboration-toolbar-actions">
                  <button
                    className="btn"
                    onClick={toggleCompiled}
                    disabled={compiledResponse.length === 0}
                  >
                    {showCompiled ? 'hide compiled' : 'compile response →'}
                  </button>
                </div>
              </div>

              {/* Compiled response preview */}
              {showCompiled && compiledResponse.length > 0 && (
                <div className="collaboration-compiled panel">
                  <div className="panel-heading">
                    <p className="eyebrow">Compiled response</p>
                    <div className="panel-heading-actions">
                      <button className="btn" onClick={() => copyCompiled(compiledResponse)}>copy</button>
                      <button
                        className="btn btn-primary"
                        onClick={() => deliverCompiledResponse()}
                        disabled={delivering}
                        title="Save compiled response to Den as draft, and deliver to live session if available"
                      >
                        {delivering ? 'saving...' : 'deliver →'}
                      </button>
                    </div>
                  </div>
                  <pre className="collaboration-compiled-body">{compiledResponse}</pre>
                  {deliveryResult && (
                    <DeliveryStatus deliveryResult={deliveryResult} />
                  )}
                </div>
              )}
            </>
          )}
        </main>
      </div>
    </div>
  );
}

function renderTurnSelector(turns: DenCollaborationTurn[], selected: DenCollaborationTurn, onSelect: (t: DenCollaborationTurn) => void) {
  return (
    <div className="collaboration-turn-selector">
      {turns.map((turn) => (
        <button
          key={turn.id}
          className={`turn-chip ${turn.id === selected.id ? 'active' : ''}`}
          onClick={() => onSelect(turn)}
        >
          <span className="turn-chip-role">{turn.role ?? 'assistant'}</span>
          <span className="turn-chip-order">Turn {turn.turn_order}</span>
        </button>
      ))}
    </div>
  );
}

// ── Segment renderer with inline annotation editor ──

interface SegmentRendererProps {
  segment: DenCollaborationSegment;
  annotations: DenCollaborationAnnotation[];
  sessionId: number;
  turnId: number;
  onAddAnnotation: (type: DenCollaborationAnnotationType, body: string | null) => Promise<void>;
  onUpdateAnnotation: (annotation: DenCollaborationAnnotation, type: DenCollaborationAnnotationType, body: string | null) => Promise<void>;
  onDeleteAnnotation: (annotation: DenCollaborationAnnotation) => void;
}

function SegmentRenderer({
  segment,
  annotations,
  onAddAnnotation,
  onUpdateAnnotation,
  onDeleteAnnotation,
}: SegmentRendererProps) {
  const [editing, setEditing] = useState(false);
  const [pendingType, setPendingType] = useState<DenCollaborationAnnotationType>('note');
  const [pendingBody, setPendingBody] = useState('');
  const [editingAnnotation, setEditingAnnotation] = useState<DenCollaborationAnnotation | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const handleSave = useCallback(async () => {
    setSaveError(null);
    setSaving(true);
    try {
      if (editingAnnotation) {
        await onUpdateAnnotation(editingAnnotation, pendingType, pendingBody || null);
      } else {
        await onAddAnnotation(pendingType, pendingBody || null);
      }
      // Only clear/close the editor after async save succeeds
      setEditing(false);
      setEditingAnnotation(null);
      setPendingBody('');
    } catch (err) {
      // Keep editor open with typed content on failure
      setSaveError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }, [editingAnnotation, pendingType, pendingBody, onAddAnnotation, onUpdateAnnotation]);

  const handleCancel = useCallback(() => {
    setEditing(false);
    setEditingAnnotation(null);
    setPendingBody('');
    setPendingType('note');
    setSaveError(null);
  }, []);

  const handleOpenEdit = useCallback((ann: DenCollaborationAnnotation) => {
    setEditing(true);
    setEditingAnnotation(ann);
    setPendingType(ann.annotation_type);
    setPendingBody(ann.body ?? '');
  }, []);

  const typeClass = (t: DenCollaborationAnnotationType) =>
    pendingType === t ? `type-btn active-${t}` : 'type-btn';

  return (
    <div
      className={`collaboration-segment ${editing ? 'selected' : ''} ${annotations.length > 0 ? 'has-annotations' : ''}`}
    >
      {/* Segment content */}
      {segment.segment_type === 'heading' ? (
        <div className="segment-heading">
          {segment.text ?? segment.raw_markdown}
        </div>
      ) : segment.segment_type === 'code_block' ? (
        <pre className="segment-code">{segment.raw_markdown}</pre>
      ) : segment.segment_type === 'block_quote' ? (
        <blockquote className="segment-blockquote">{segment.raw_markdown}</blockquote>
      ) : (
        <div className="segment-text">{segment.raw_markdown}</div>
      )}

      {/* Hover annotate hint */}
      {!editing && (
        <button
          className="segment-annotate-hint"
          onClick={() => { setEditing(true); setEditingAnnotation(null); setPendingBody(''); setPendingType('note'); setSaveError(null); }}
          title="Annotate this segment"
        >
          + annotate
        </button>
      )}

      {/* Inline annotation editor */}
      {editing && (
        <div className="annotation-input">
          <div className="annotation-type-row">
            {(['note', 'skip', 'done', 'flag'] as const).map((t) => (
              <button
                key={t}
                className={typeClass(t)}
                onClick={() => setPendingType(t)}
              >
                {t}
              </button>
            ))}
          </div>
          {pendingType !== 'skip' && (
            <textarea
              className="annotation-textarea"
              rows={2}
              placeholder={pendingType === 'note' ? 'your comment...' : pendingType === 'done' ? 'what was already handled...' : 'needs discussion...'}
              value={pendingBody}
              onChange={(e) => setPendingBody(e.target.value)}
              autoFocus
            />
          )}
          {saveError && <p className="annotation-error">{saveError}</p>}
          <div className="annotation-actions">
            <button className="annotation-cancel" onClick={handleCancel} disabled={saving}>cancel</button>
            <button className="annotation-save" onClick={handleSave} disabled={saving}>{saving ? 'saving...' : 'save'}</button>
          </div>
        </div>
      )}

      {/* Existing annotation chips */}
      {annotations.length > 0 && (
        <div className="annotations-list">
          {annotations.map((ann) => (
            <div key={ann.id} className="annotation-chip">
              <span className={`chip-type chip-${ann.annotation_type}`}>{ann.annotation_type}</span>
              {ann.annotation_type !== 'skip' && ann.body && (
                <span className="chip-text">{ann.body}</span>
              )}
              <button
                className="chip-edit"
                onClick={() => handleOpenEdit(ann)}
                title="Edit annotation"
              >✎</button>
              <button
                className="chip-del"
                onClick={() => onDeleteAnnotation(ann)}
                title="Delete annotation"
              >×</button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function SessionCard({
  session,
  active,
  onSelect,
}: {
  session: { id: number; title: string | null; status: string; created_at: string; task_id: number | null };
  active: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      className={`collaboration-session-card ${active ? 'active' : ''} ${session.status !== 'active' ? 'resolved' : ''}`}
      onClick={onSelect}
    >
      <div className="session-card-topline">
        <span className={`status-dot status-${session.status === 'active' ? 'running' : 'stopped'}`} />
        <strong>{session.title ?? `Session #${session.id}`}</strong>
      </div>
      <div className="session-card-meta">
        <span>{session.status}</span>
        <span>task {session.task_id ?? '—'}</span>
        <span>{compactDate(session.created_at)}</span>
      </div>
    </button>
  );
}

async function copyCompiled(text: string) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // Clipboard API not available; silently ignore.
  }
}

function DeliveryStatus({ deliveryResult }: { deliveryResult: CollaborationDeliveryResult }) {
  const { den_post, delivery } = deliveryResult;

  const statusLabel = (status: string) => {
    switch (status) {
      case 'delivered': return 'delivered to session';
      case 'no_live_session': return 'no live session';
      case 'session_stale': return 'session stale';
      case 'session_offline': return 'session offline';
      case 'capability_denied': return 'capability denied';
      case 'skipped': return 'skipped';
      case 'failed': return 'delivery failed';
      case 'draft_only_fallback': return 'saved as draft (too large for live delivery)';
      default: return status;
    }
  };

  const statusClass = (status: string) => {
    switch (status) {
      case 'delivered': return 'status-running';
      case 'failed': return 'status-stopped';
      case 'draft_only_fallback': return 'status-idle';
      case 'no_live_session': case 'skipped': return 'status-idle';
      default: return 'status-stopped';
    }
  };

  return (
    <div className="collaboration-delivery-status">
      {den_post.posted && (
        <span className="status-pill status-running">
          saved to Den{den_post.draft_id ? ` (draft #${den_post.draft_id})` : ''}
        </span>
      )}
      {den_post.error && (
        <span className="status-pill status-stopped" title={den_post.error}>Den save failed</span>
      )}
      <span className={`status-pill ${statusClass(delivery.status)}`} title={delivery.reason ?? delivery.error ?? ''}>
        {statusLabel(delivery.status)}
        {delivery.target_session_id && ` → ${delivery.target_session_id}`}
      </span>
    </div>
  );
}

function compactDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}
