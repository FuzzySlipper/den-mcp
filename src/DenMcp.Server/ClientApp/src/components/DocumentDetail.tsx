import { useEffect, useState } from 'react';
import type { DocumentSummary, Document } from '../api/types';
import { getDocument } from '../api/client';

interface Props {
  summary: DocumentSummary;
  onClose: () => void;
}

export function DocumentDetail({ summary, onClose }: Props) {
  const [doc, setDoc] = useState<Document | null>(null);

  useEffect(() => {
    let cancelled = false;
    getDocument(summary.project_id, summary.slug)
      .then(d => { if (!cancelled) setDoc(d); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [summary.project_id, summary.slug]);

  return (
    <div className="detail-overlay">
      <div className="detail-header">
        <h2>{summary.title}</h2>
        <button className="detail-close" onClick={onClose}>✕</button>
      </div>
      <div className="detail-body">
        <div className="detail-section">
          <dl className="detail-meta">
            <dt>Slug</dt>
            <dd>{summary.slug}</dd>
            <dt>Type</dt>
            <dd>{summary.doc_type}</dd>
            <dt>Project</dt>
            <dd>{summary.project_id}</dd>
            {summary.updated_at && (
              <><dt>Updated</dt><dd>{new Date(summary.updated_at + 'Z').toLocaleString()}</dd></>
            )}
            {summary.tags && summary.tags.length > 0 && (
              <><dt>Tags</dt><dd>{summary.tags.join(', ')}</dd></>
            )}
          </dl>
        </div>

        <div className="detail-section">
          <h3>Content</h3>
          <div className="detail-description">
            {doc ? doc.content : 'Loading...'}
          </div>
        </div>
      </div>
    </div>
  );
}
