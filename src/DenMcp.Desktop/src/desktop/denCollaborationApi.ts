/**
 * Fetch-based Den REST API client for collaboration sessions, turns, and annotations.
 *
 * Uses the Den base URL from the operator runtime (status.denBaseUrl) to call
 * the existing Den collaboration REST endpoints. This is a renderer-side
 * convenience — the durable source of truth remains Den's collaboration
 * repository, and local UI draft state is non-durable.
 *
 * The Den base URL is expected to be provided by the caller (from operator
 * runtime status) so this module has no ambient dependency on shell state.
 */

// ── Type aliases matching the Den REST API snake_case JSON shape ──

export type DenCollaborationSessionStatus = 'active' | 'resolved' | 'archived';
export type DenCollaborationSegmentType = 'heading' | 'paragraph' | 'code_block' | 'list' | 'block_quote';
export type DenCollaborationAnnotationType = 'note' | 'skip' | 'done' | 'flag';

export interface DenCollaborationSession {
  id: number;
  project_id: string;
  task_id: number | null;
  message_id: number | null;
  agent_stream_entry_id: number | null;
  pi_run_id: string | null;
  pi_session_id: string | null;
  desktop_operator_session_id: string | null;
  title: string | null;
  status: DenCollaborationSessionStatus;
  created_by: string | null;
  created_at: string;
  updated_at: string;
  turns?: DenCollaborationTurn[];
  annotations?: DenCollaborationAnnotation[];
  drafts?: DenCollaborationResponseDraft[];
}

export interface DenCollaborationTurn {
  id: number;
  session_id: number;
  turn_order: number;
  role: string | null;
  source_kind: string | null;
  source_ref: string | null;
  source_label: string | null;
  source_uri: string | null;
  raw_markdown: string;
  source_content_hash: string;
  segmenter_version: string;
  created_at: string;
  segments?: DenCollaborationSegment[];
}

export interface DenCollaborationSegment {
  id: number;
  turn_id: number;
  sequence_number: number;
  segment_hash: string;
  segment_type: DenCollaborationSegmentType;
  raw_markdown: string;
  text: string | null;
  heading_level: number | null;
  code_language: string | null;
  created_at: string;
}

export interface DenCollaborationAnnotation {
  id: number;
  session_id: number;
  turn_id: number;
  segment_id: number;
  segment_hash: string;
  annotation_type: DenCollaborationAnnotationType;
  body: string | null;
  created_by: string | null;
  updated_by: string | null;
  revision: number;
  created_at: string;
  updated_at: string;
}

export interface DenCollaborationResponseDraft {
  id: number;
  session_id: number;
  turn_id: number | null;
  content: string;
  created_by: string | null;
  updated_by: string | null;
  revision: number;
  created_at: string;
  updated_at: string;
}

export interface CreateCollaborationAnnotationRequest {
  segment_id: number;
  annotation_type: DenCollaborationAnnotationType;
  body: string | null;
  created_by: string | null;
}

export interface UpdateCollaborationAnnotationRequest {
  expected_revision: number;
  annotation_type: DenCollaborationAnnotationType;
  body: string | null;
  updated_by: string | null;
}

export interface CreateCollaborationDraftRequest {
  turn_id: number | null;
  content: string;
  created_by: string | null;
}

export interface UpdateCollaborationDraftRequest {
  expected_revision: number;
  content: string;
  updated_by: string | null;
}

// ── API client ──

function buildUrl(baseUrl: string, path: string): string {
  const normalized = baseUrl.replace(/\/+$/, '');
  return `${normalized}${path}`;
}

function formatUnexpectedResponse(url: string, text: string, contentType: string | null): string {
  const trimmed = text.trim();
  const kind = contentType?.split(';', 1)[0] || (trimmed.startsWith('<') ? 'text/html' : 'non-json');
  if (trimmed.startsWith('<!doctype') || trimmed.startsWith('<html') || kind === 'text/html') {
    return `Den collaboration API returned HTML instead of JSON for ${url}. Check that the Den server REST endpoint is reachable.`;
  }

  return `Den collaboration API returned ${kind} instead of JSON for ${url}.`;
}

async function apiFetch<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    headers: { 'Content-Type': 'application/json' },
    ...options,
  });

  const text = await response.text();

  if (!response.ok) {
    const message = text.trim().startsWith('<')
      ? formatUnexpectedResponse(url, text, response.headers.get('content-type'))
      : (text || response.statusText);
    throw new Error(`Den API error ${response.status} for ${url}: ${message}`);
  }

  if (!text) return undefined as T;

  const contentType = response.headers.get('content-type');
  if (contentType && !contentType.toLowerCase().includes('application/json')) {
    throw new Error(formatUnexpectedResponse(url, text, contentType));
  }

  try {
    return JSON.parse(text) as T;
  } catch (err) {
    throw new Error(formatUnexpectedResponse(url, text, contentType));
  }
}

export function createDenCollaborationApi(denBaseUrl: string, projectId: string) {
  const base = (path: string) => buildUrl(denBaseUrl, `/api/projects/${encodeURIComponent(projectId)}/collaboration${path}`);

  return {
    /** List collaboration sessions for the current project. */
    listSessions(taskId?: number | null, status?: DenCollaborationSessionStatus | null): Promise<DenCollaborationSession[]> {
      const params = new URLSearchParams();
      if (taskId != null) params.set('taskId', String(taskId));
      if (status) params.set('status', status);
      const qs = params.toString();
      return apiFetch<DenCollaborationSession[]>(base(`/sessions${qs ? '?' + qs : ''}`));
    },

    /** Get a single session with turns, segments, and annotations. */
    getSession(sessionId: number): Promise<DenCollaborationSession> {
      return apiFetch<DenCollaborationSession>(base(`/sessions/${sessionId}`));
    },

    /** Create an annotation on a segment. */
    createAnnotation(
      sessionId: number,
      turnId: number,
      request: CreateCollaborationAnnotationRequest,
    ): Promise<DenCollaborationAnnotation> {
      return apiFetch<DenCollaborationAnnotation>(
        base(`/sessions/${sessionId}/turns/${turnId}/annotations`),
        {
          method: 'POST',
          body: JSON.stringify(request),
        },
      );
    },

    /** Update an existing annotation (optimistic concurrency via expected_revision). */
    updateAnnotation(
      sessionId: number,
      annotationId: number,
      request: UpdateCollaborationAnnotationRequest,
    ): Promise<DenCollaborationAnnotation> {
      return apiFetch<DenCollaborationAnnotation>(
        base(`/sessions/${sessionId}/annotations/${annotationId}`),
        {
          method: 'PUT',
          body: JSON.stringify(request),
        },
      );
    },

    /** Delete an annotation. */
    deleteAnnotation(sessionId: number, annotationId: number, expectedRevision: number): Promise<{ id: number; deleted: boolean }> {
      const params = new URLSearchParams({ expectedRevision: String(expectedRevision) });
      return apiFetch<{ id: number; deleted: boolean }>(
        base(`/sessions/${sessionId}/annotations/${annotationId}?${params.toString()}`),
        { method: 'DELETE' },
      );
    },

    /** List annotations for a session/turn. */
    listAnnotations(sessionId: number, turnId?: number): Promise<DenCollaborationAnnotation[]> {
      const params = new URLSearchParams();
      if (turnId != null) params.set('turnId', String(turnId));
      const qs = params.toString();
      return apiFetch<DenCollaborationAnnotation[]>(base(`/sessions/${sessionId}/annotations${qs ? '?' + qs : ''}`));
    },

    /** Create or update a compiled response draft. */
    createDraft(sessionId: number, request: CreateCollaborationDraftRequest): Promise<DenCollaborationResponseDraft> {
      return apiFetch<DenCollaborationResponseDraft>(
        base(`/sessions/${sessionId}/drafts`),
        { method: 'POST', body: JSON.stringify(request) },
      );
    },

    /** Update an existing draft. */
    updateDraft(sessionId: number, draftId: number, request: UpdateCollaborationDraftRequest): Promise<DenCollaborationResponseDraft> {
      return apiFetch<DenCollaborationResponseDraft>(
        base(`/sessions/${sessionId}/drafts/${draftId}`),
        { method: 'PUT', body: JSON.stringify(request) },
      );
    },
  };
}

export type DenCollaborationApi = ReturnType<typeof createDenCollaborationApi>;
