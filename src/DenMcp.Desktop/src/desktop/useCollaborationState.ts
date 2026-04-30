import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  createDenCollaborationApi,
  type DenCollaborationAnnotation,
  type DenCollaborationAnnotationType,
  type DenCollaborationSession,
  type DenCollaborationTurn,
  type DenCollaborationApi,
} from './denCollaborationApi.ts';
import { compileResponse } from './collaborationCompileResponse.ts';

export interface CollaborationState {
  /** List of sessions for the current project. */
  sessions: DenCollaborationSession[];
  /** Currently selected session ID, or null. */
  selectedSessionId: number | null;
  /** Full session detail for the selected session (includes turns, segments, annotations). */
  selectedSession: DenCollaborationSession | null;
  /** Currently selected turn within the session. */
  selectedTurn: DenCollaborationTurn | null;
  /** Local annotation mutations not yet persisted. */
  localAnnotations: Map<number, DenCollaborationAnnotation[]>;
  /** Loading state. */
  loading: boolean;
  /** Error message if any. */
  error: string | null;
  /** Compiled response text (computed from annotations). */
  compiledResponse: string;
  /** True when the compiled response panel is visible. */
  showCompiled: boolean;
}

export interface CollaborationActions {
  /** Select a session and load its full detail. */
  selectSession: (sessionId: number) => Promise<void>;
  /** Select a turn within the current session. */
  selectTurn: (turn: DenCollaborationTurn) => void;
  /** Refresh the session list. */
  refreshSessions: () => Promise<void>;
  /** Create a new annotation on the selected session/turn/segment. */
  addAnnotation: (segmentId: number, annotationType: DenCollaborationAnnotationType, body: string | null) => Promise<void>;
  /** Update an existing annotation. */
  updateAnnotation: (annotation: DenCollaborationAnnotation, annotationType: DenCollaborationAnnotationType, body: string | null) => Promise<void>;
  /** Delete an annotation. */
  deleteAnnotation: (annotation: DenCollaborationAnnotation) => Promise<void>;
  /** Toggle compiled response preview. */
  toggleCompiled: () => void;
  /** Clear the current error. */
  clearError: () => void;
}

const POLL_INTERVAL_MS = 15_000;

export function useCollaborationState(
  denBaseUrl: string | null,
  projectId: string | null,
  taskId?: number | null,
): CollaborationState & CollaborationActions {
  const [sessions, setSessions] = useState<DenCollaborationSession[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<number | null>(null);
  const [selectedSession, setSelectedSession] = useState<DenCollaborationSession | null>(null);
  const [selectedTurn, setSelectedTurn] = useState<DenCollaborationTurn | null>(null);
  const [localAnnotations, setLocalAnnotations] = useState<Map<number, DenCollaborationAnnotation[]>>(new Map());
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showCompiled, setShowCompiled] = useState(false);
  const mountedRef = useRef(true);
  const apiRef = useRef<DenCollaborationApi | null>(null);

  useEffect(() => {
    mountedRef.current = true;
    return () => { mountedRef.current = false; };
  }, []);

  // Reset when project changes
  useEffect(() => {
    setSessions([]);
    setSelectedSessionId(null);
    setSelectedSession(null);
    setSelectedTurn(null);
    setLocalAnnotations(new Map());
    setShowCompiled(false);
    setError(null);
    apiRef.current = (denBaseUrl && projectId) ? createDenCollaborationApi(denBaseUrl, projectId) : null;
  }, [denBaseUrl, projectId]);

  const refreshSessions = useCallback(async () => {
    if (!apiRef.current) return;
    setLoading(true);
    try {
      const list = await apiRef.current.listSessions(taskId ?? null, null);
      if (mountedRef.current) {
        setSessions(list);
        setError(null);
      }
    } catch (err) {
      if (mountedRef.current) {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      if (mountedRef.current) setLoading(false);
    }
  }, [taskId]);

  // Initial load + polling
  useEffect(() => {
    void refreshSessions();
    const intervalId = window.setInterval(() => void refreshSessions(), POLL_INTERVAL_MS);
    return () => window.clearInterval(intervalId);
  }, [refreshSessions]);

  const selectSession = useCallback(async (sessionId: number) => {
    if (!apiRef.current) return;
    setSelectedSessionId(sessionId);
    setSelectedTurn(null);
    setShowCompiled(false);
    try {
      const session = await apiRef.current.getSession(sessionId);
      if (mountedRef.current) {
        setSelectedSession(session);
        setError(null);
        // Select the most recent turn by default
        const turns = session.turns ?? [];
        if (turns.length > 0) {
          setSelectedTurn(turns[turns.length - 1]);
        }
      }
    } catch (err) {
      if (mountedRef.current) {
        setError(err instanceof Error ? err.message : String(err));
      }
    }
  }, []);

  const selectTurn = useCallback((turn: DenCollaborationTurn) => {
    setSelectedTurn(turn);
    setShowCompiled(false);
  }, []);

  const addAnnotation = useCallback(async (segmentId: number, annotationType: DenCollaborationAnnotationType, body: string | null) => {
    if (!apiRef.current || !selectedSessionId || !selectedTurn) return;
    try {
      const created = await apiRef.current.createAnnotation(selectedSessionId, selectedTurn.id, {
        segment_id: segmentId,
        annotation_type: annotationType,
        body,
        created_by: 'desktop-operator',
      });
      if (mountedRef.current) {
        // Refresh the session to get updated annotations
        const session = await apiRef.current!.getSession(selectedSessionId);
        if (mountedRef.current) {
          setSelectedSession(session);
        }
      }
    } catch (err) {
      if (mountedRef.current) {
        setError(err instanceof Error ? err.message : String(err));
      }
      throw err;
    }
  }, [selectedSessionId, selectedTurn]);

  const updateAnnotationAction = useCallback(async (annotation: DenCollaborationAnnotation, annotationType: DenCollaborationAnnotationType, body: string | null) => {
    if (!apiRef.current || !selectedSessionId) return;
    try {
      await apiRef.current.updateAnnotation(selectedSessionId, annotation.id, {
        expected_revision: annotation.revision,
        annotation_type: annotationType,
        body,
        updated_by: 'desktop-operator',
      });
      if (mountedRef.current) {
        const session = await apiRef.current!.getSession(selectedSessionId);
        if (mountedRef.current) setSelectedSession(session);
      }
    } catch (err) {
      if (mountedRef.current) setError(err instanceof Error ? err.message : String(err));
      throw err;
    }
  }, [selectedSessionId]);

  const deleteAnnotationAction = useCallback(async (annotation: DenCollaborationAnnotation) => {
    if (!apiRef.current || !selectedSessionId) return;
    try {
      await apiRef.current.deleteAnnotation(selectedSessionId, annotation.id, annotation.revision);
      if (mountedRef.current) {
        const session = await apiRef.current!.getSession(selectedSessionId);
        if (mountedRef.current) setSelectedSession(session);
      }
    } catch (err) {
      if (mountedRef.current) setError(err instanceof Error ? err.message : String(err));
      throw err;
    }
  }, [selectedSessionId]);

  // Compute compiled response from current session's annotations
  const compiledResponse = useMemo(() => {
    if (!selectedSession || !selectedTurn) return '';
    const segments = selectedTurn.segments ?? [];
    const turnAnnotations = (selectedSession.annotations ?? []).filter(
      (a) => a.turn_id === selectedTurn.id,
    );
    return compileResponse(segments, turnAnnotations);
  }, [selectedSession, selectedTurn]);

  return {
    sessions,
    selectedSessionId,
    selectedSession,
    selectedTurn,
    localAnnotations,
    loading,
    error,
    compiledResponse,
    showCompiled,
    selectSession,
    selectTurn,
    refreshSessions,
    addAnnotation,
    updateAnnotation: updateAnnotationAction,
    deleteAnnotation: deleteAnnotationAction,
    toggleCompiled: () => setShowCompiled((prev) => !prev),
    clearError: () => setError(null),
  };
}
