import assert from 'node:assert/strict';
import test from 'node:test';
import { getMessage, queryLibrarian } from '../../src/DenMcp.Server/ClientApp/src/api/client.ts';
import {
  documentRefFromLibrarianItem,
  groupLibrarianItems,
  librarianHasResults,
  messageRefFromLibrarianItem,
  stableLibrarianRef,
  taskRefFromLibrarianItem,
} from '../../src/DenMcp.Server/ClientApp/src/librarian.ts';

function item(overrides) {
  return {
    type: 'document',
    source_id: 'den-mcp/example-doc',
    project_id: 'den-mcp',
    summary: 'Example summary',
    why_relevant: 'It matches the query.',
    snippet: null,
    ...overrides,
  };
}

test('librarian helpers group source-attributed results and preserve stable refs', () => {
  const doc = item({ source_id: '[doc: _global/pi-guidance]', project_id: '_global' });
  const task = item({ type: 'task', source_id: '#855', project_id: 'den-mcp' });
  const message = item({ type: 'message', source_id: 'msg#1175 thread#1173', project_id: 'den-mcp' });
  const unknown = item({ type: 'run', source_id: 'run-1' });

  const groups = groupLibrarianItems([message, unknown, doc, task]);

  assert.deepEqual(groups.map(group => group.key), ['document', 'task', 'message', 'other']);
  assert.deepEqual(documentRefFromLibrarianItem(doc), {
    projectId: '_global',
    slug: 'pi-guidance',
    ref: '[doc: _global/pi-guidance]',
  });
  assert.deepEqual(taskRefFromLibrarianItem(task), {
    projectId: 'den-mcp',
    taskId: 855,
    ref: '#855',
  });
  assert.deepEqual(messageRefFromLibrarianItem(message), {
    projectId: 'den-mcp',
    messageId: 1175,
    threadId: 1173,
    ref: 'msg#1175 thread#1173',
  });
  assert.equal(stableLibrarianRef(doc), '[doc: _global/pi-guidance]');
});

test('librarian result emptiness includes recommendations', () => {
  assert.equal(librarianHasResults(null), false);
  assert.equal(librarianHasResults({ relevant_items: [], recommendations: [], confidence: 'low' }), false);
  assert.equal(librarianHasResults({ relevant_items: [], recommendations: ['Read docs'], confidence: 'medium' }), true);
});

test('getMessage supports message navigation from librarian refs', async () => {
  const previousFetch = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (url) => {
    calls.push(url);
    return {
      ok: true,
      status: 200,
      json: async () => ({
        id: 1175,
        project_id: 'den-mcp',
        task_id: 855,
        thread_id: 1173,
        sender: 'pi-reviewer',
        content: 'Review findings',
        intent: 'review_feedback',
        metadata: null,
        created_at: '2026-04-27T02:26:18',
      }),
    };
  };

  try {
    const message = await getMessage('den-mcp', 1175);
    assert.equal(calls[0], '/api/projects/den-mcp/messages/1175');
    assert.equal(message?.thread_id, 1173);
  } finally {
    globalThis.fetch = previousFetch;
  }
});


test('queryLibrarian posts snake_case request fields and returns response', async () => {
  const previousFetch = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push({ url, init });
    return {
      ok: true,
      json: async () => ({
        relevant_items: [item({ source_id: 'den-mcp/librarian-web' })],
        recommendations: ['Open the document'],
        confidence: 'high',
      }),
    };
  };

  try {
    const response = await queryLibrarian('den-mcp', {
      query: 'librarian UI',
      taskId: 855,
      includeGlobal: false,
    });

    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, '/api/projects/den-mcp/librarian/query');
    assert.equal(calls[0].init.method, 'POST');
    assert.deepEqual(JSON.parse(calls[0].init.body), {
      query: 'librarian UI',
      task_id: 855,
      include_global: false,
    });
    assert.equal(response.confidence, 'high');
    assert.equal(response.relevant_items[0].source_id, 'den-mcp/librarian-web');
  } finally {
    globalThis.fetch = previousFetch;
  }
});
