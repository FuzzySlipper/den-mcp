import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  documentsList,
  documentGet,
  documentStore,
  type DocumentsListItem,
} from '../desktop/sidecarBridgeApi.ts';

interface Props {
  projectId: string | null;
}

type ViewMode = 'list' | 'view' | 'edit';

const REFRESH_INTERVAL_MS = 60_000;

export function DocsPane({ projectId }: Props) {
  const [documents, setDocuments] = useState<DocumentsListItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [viewMode, setViewMode] = useState<ViewMode>('list');
  const [selectedDoc, setSelectedDoc] = useState<{
    slug: string;
    title: string;
    content: string;
    doc_type: string;
    tags: string[];
  } | null>(null);
  const [editContent, setEditContent] = useState('');
  const [editTitle, setEditTitle] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const mountedRef = useRef(true);

  useEffect(() => { mountedRef.current = true; return () => { mountedRef.current = false; }; }, []);

  const fetchDocuments = useCallback(async () => {
    if (!projectId) return;
    setLoading(true);
    setError(null);
    try {
      const result = await documentsList({ project_id: projectId });
      if (mountedRef.current) {
        setDocuments(result.documents ?? []);
      }
    } catch (err) {
      if (mountedRef.current) {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      if (mountedRef.current) setLoading(false);
    }
  }, [projectId]);

  useEffect(() => {
    void fetchDocuments();
    const interval = window.setInterval(() => void fetchDocuments(), REFRESH_INTERVAL_MS);
    return () => window.clearInterval(interval);
  }, [fetchDocuments]);

  const filteredDocuments = useMemo(() => {
    if (!searchQuery.trim()) return documents;
    const q = searchQuery.toLowerCase();
    return documents.filter(
      (doc) => doc.title.toLowerCase().includes(q) || doc.slug.toLowerCase().includes(q),
    );
  }, [documents, searchQuery]);

  const openDocument = useCallback(async (slug: string) => {
    if (!projectId) return;
    try {
      const doc = await documentGet({ project_id: projectId, slug });
      if (mountedRef.current) {
        setSelectedDoc({
          slug: doc.slug,
          title: doc.title,
          content: doc.content,
          doc_type: doc.doc_type,
          tags: doc.tags ?? [],
        });
        setEditContent(doc.content);
        setEditTitle(doc.title);
        setViewMode('view');
        setSaveError(null);
      }
    } catch (err) {
      if (mountedRef.current) {
        setError(err instanceof Error ? err.message : String(err));
      }
    }
  }, [projectId]);

  const startEditing = useCallback(() => {
    setViewMode('edit');
  }, []);

  const cancelEditing = useCallback(() => {
    if (selectedDoc) {
      setEditContent(selectedDoc.content);
      setEditTitle(selectedDoc.title);
    }
    setViewMode('view');
    setSaveError(null);
  }, [selectedDoc]);

  const saveDocument = useCallback(async () => {
    if (!projectId || !selectedDoc) return;
    setSaving(true);
    setSaveError(null);
    try {
      await documentStore({
        project_id: projectId,
        slug: selectedDoc.slug,
        title: editTitle,
        content: editContent,
      });
      if (mountedRef.current) {
        // Update the selected doc with new content
        setSelectedDoc((prev) => prev ? { ...prev, title: editTitle, content: editContent } : null);
        setViewMode('view');
        // Refresh the document list
        void fetchDocuments();
      }
    } catch (err) {
      if (mountedRef.current) {
        setSaveError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      if (mountedRef.current) setSaving(false);
    }
  }, [projectId, selectedDoc, editTitle, editContent, fetchDocuments]);

  const backToList = useCallback(() => {
    setViewMode('list');
    setSelectedDoc(null);
    setEditContent('');
    setEditTitle('');
    setSaveError(null);
  }, []);

  // No project selected
  if (!projectId) {
    return (
      <section className="panel docs-pane">
        <p className="eyebrow">Documents</p>
        <h2>Den documents</h2>
        <div className="empty-state">
          <strong>No project selected.</strong>
          <p>Select a project from the left rail to load documents.</p>
        </div>
      </section>
    );
  }

  // Viewer mode
  if (viewMode === 'view' && selectedDoc) {
    return (
      <section className="panel docs-pane">
        <DocsHeader
          title={selectedDoc.title}
          slug={selectedDoc.slug}
          docType={selectedDoc.doc_type}
          tags={selectedDoc.tags}
          onBack={backToList}
        />
        <div className="docs-viewer">
          <pre className="docs-viewer-content">{selectedDoc.content}</pre>
        </div>
        <div className="docs-actions">
          <button type="button" onClick={startEditing}>Edit</button>
          <button type="button" className="secondary" onClick={backToList}>Back to list</button>
        </div>
      </section>
    );
  }

  // Editor mode
  if (viewMode === 'edit' && selectedDoc) {
    return (
      <section className="panel docs-pane">
        <DocsHeader
          title={selectedDoc.title}
          slug={selectedDoc.slug}
          docType={selectedDoc.doc_type}
          tags={selectedDoc.tags}
          onBack={backToList}
        />
        <div className="docs-editor-group">
          <label className="docs-editor-label">
            <span>Title</span>
            <input
              type="text"
              value={editTitle}
              onChange={(e) => setEditTitle(e.target.value)}
              className="docs-editor-title-input"
            />
          </label>
          <label className="docs-editor-label">
            <span>Content (markdown)</span>
            <textarea
              className="docs-editor"
              value={editContent}
              onChange={(e) => setEditContent(e.target.value)}
              rows={24}
              spellCheck={false}
            />
          </label>
        </div>
        {saveError && <div className="error-note">{saveError}</div>}
        <div className="docs-actions">
          <button type="button" onClick={saveDocument} disabled={saving}>
            {saving ? 'Saving…' : 'Save'}
          </button>
          <button type="button" className="secondary" onClick={cancelEditing}>Cancel</button>
        </div>
      </section>
    );
  }

  // List view
  return (
    <section className="panel docs-pane">
      <div className="docs-header">
        <div className="docs-header-title">
          <p className="eyebrow">Documents · {projectId}</p>
          <h2>Den documents</h2>
        </div>
        <div className="docs-header-metrics">
          <span className="docs-count"><strong>{documents.length}</strong> documents</span>
        </div>
      </div>

      <div className="docs-search-bar">
        <input
          type="text"
          placeholder="Search by title or slug…"
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
        />
      </div>

      {error && <div className="error-note">{error}</div>}

      {loading && documents.length === 0 ? (
        <div className="empty-state">
          <strong>Loading documents…</strong>
          <p>Fetching document list from the Den Desktop bridge.</p>
        </div>
      ) : filteredDocuments.length === 0 ? (
        <div className="empty-state">
          <strong>{searchQuery ? 'No matching documents found.' : 'No documents found.'}</strong>
          <p>{searchQuery ? 'Try a different search query.' : 'Documents will appear here once they exist in Den for this project.'}</p>
        </div>
      ) : (
        <div className="docs-list">
          {filteredDocuments.map((doc) => (
            <DocCard
              key={doc.slug}
              slug={doc.slug}
              title={doc.title}
              docType={doc.doc_type}
              tags={doc.tags}
              onClick={() => void openDocument(doc.slug)}
            />
          ))}
        </div>
      )}
    </section>
  );
}

// ── Sub-components ──────────────────────────────────────────────────

function DocsHeader({
  title,
  slug,
  docType,
  tags,
  onBack,
}: {
  title: string;
  slug: string;
  docType: string;
  tags: string[];
  onBack: () => void;
}) {
  return (
    <div className="docs-viewer-header">
      <div className="docs-viewer-header-title">
        <p className="eyebrow">{slug}</p>
        <h2>{title}</h2>
        <div className="docs-meta-pills">
          <span className="pill">{docType}</span>
          {tags.map((tag) => (
            <span key={tag} className="chip">{tag}</span>
          ))}
        </div>
      </div>
      <button type="button" className="secondary" onClick={onBack}>← Back</button>
    </div>
  );
}

function DocCard({
  slug,
  title,
  docType,
  tags,
  onClick,
}: {
  slug: string;
  title: string;
  docType: string;
  tags: string[];
  onClick: () => void;
}) {
  return (
    <article
      className="docs-card"
      role="button"
      tabIndex={0}
      onClick={onClick}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onClick(); } }}
      aria-label={`Document: ${title}`}
    >
      <div className="docs-card-topline">
        <span className="docs-card-title">{title}</span>
        <span className="pill">{docType}</span>
      </div>
      <div className="docs-card-slug">{slug}</div>
      {tags.length > 0 && (
        <div className="docs-card-tags">
          {tags.map((tag) => (
            <span key={tag} className="chip">{tag}</span>
          ))}
        </div>
      )}
    </article>
  );
}
