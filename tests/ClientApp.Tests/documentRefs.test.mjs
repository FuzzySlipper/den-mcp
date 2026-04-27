import assert from 'node:assert/strict';
import test from 'node:test';
import { getDocument } from '../../src/DenMcp.Server/ClientApp/src/api/client.ts';
import {
  documentSummaryFromReference,
  parseDocumentReference,
  splitDocumentReferenceText,
} from '../../src/DenMcp.Server/ClientApp/src/documentRefs.ts';

test('document refs parse global document links', () => {
  const ref = parseDocumentReference('[doc: _global/agent-task-management-policy]');

  assert.deepEqual(ref, {
    projectId: '_global',
    slug: 'agent-task-management-policy',
    ref: '[doc: _global/agent-task-management-policy]',
  });
  assert.deepEqual(documentSummaryFromReference(ref), {
    id: 0,
    project_id: '_global',
    slug: 'agent-task-management-policy',
    title: 'agent-task-management-policy',
    doc_type: 'note',
    tags: null,
    updated_at: '',
  });
});

test('document refs split project document links while preserving surrounding markdown', () => {
  const parts = splitDocumentReferenceText('Read [doc: den-mcp/agent-stream-design] before [doc: den-mcp/project-specs].');

  assert.deepEqual(parts, [
    { kind: 'text', text: 'Read ' },
    {
      kind: 'document_ref',
      projectId: 'den-mcp',
      slug: 'agent-stream-design',
      ref: '[doc: den-mcp/agent-stream-design]',
    },
    { kind: 'text', text: ' before ' },
    {
      kind: 'document_ref',
      projectId: 'den-mcp',
      slug: 'project-specs',
      ref: '[doc: den-mcp/project-specs]',
    },
    { kind: 'text', text: '.' },
  ]);
});

test('missing document link targets resolve to null through the document client', async () => {
  const previousFetch = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (url) => {
    calls.push(url);
    return {
      ok: false,
      status: 404,
      json: async () => ({ error: 'missing' }),
    };
  };

  try {
    const missing = await getDocument('_global', 'deleted-doc');
    assert.equal(calls[0], '/api/projects/_global/documents/deleted-doc');
    assert.equal(missing, null);
  } finally {
    globalThis.fetch = previousFetch;
  }
});
