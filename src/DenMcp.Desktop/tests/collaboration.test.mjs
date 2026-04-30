import assert from 'node:assert/strict';
import test from 'node:test';
import { compileResponse } from '../src/desktop/collaborationCompileResponse.ts';
import { createDenCollaborationApi } from '../src/desktop/denCollaborationApi.ts';

function segment(overrides = {}) {
  return {
    id: 1,
    turn_id: 1,
    sequence_number: 1,
    segment_hash: 'abc12345def67890',
    segment_type: 'paragraph',
    raw_markdown: 'This is a test segment.',
    text: 'This is a test segment.',
    heading_level: null,
    code_language: null,
    created_at: '2026-04-30T00:00:00.000Z',
    ...overrides,
  };
}

function codeSegment(overrides = {}) {
  return segment({
    id: 2,
    sequence_number: 2,
    segment_hash: 'def45678abcd1234',
    segment_type: 'code_block',
    raw_markdown: 'console.log("hello");',
    text: null,
    ...overrides,
  });
}

function headingSegment(overrides = {}) {
  return segment({
    id: 3,
    sequence_number: 3,
    segment_hash: '1111222233334444',
    segment_type: 'heading',
    raw_markdown: '## Section Title',
    text: 'Section Title',
    heading_level: 2,
    ...overrides,
  });
}

async function withFetchStub(fetchStub, run) {
  const previousFetch = globalThis.fetch;
  globalThis.fetch = fetchStub;
  try {
    return await run();
  } finally {
    globalThis.fetch = previousFetch;
  }
}

function annotation(overrides = {}) {
  return {
    id: 10,
    session_id: 1,
    turn_id: 1,
    segment_id: 1,
    segment_hash: 'abc12345def67890',
    annotation_type: 'note',
    body: 'Looks good.',
    created_by: 'desktop-operator',
    updated_by: null,
    revision: 1,
    created_at: '2026-04-30T00:01:00.000Z',
    updated_at: '2026-04-30T00:01:00.000Z',
    ...overrides,
  };
}

test('collaboration API reports HTML responses calmly instead of raw JSON parse errors', async () => {
  await withFetchStub(async () => new Response('<!doctype html><html><body>SPA</body></html>', {
    status: 200,
    headers: { 'content-type': 'text/html' },
  }), async () => {
    const api = createDenCollaborationApi('http://127.0.0.1:5199', 'den-mcp');
    await assert.rejects(
      () => api.listSessions(null, null),
      (error) => {
        assert.ok(error instanceof Error);
        assert.match(error.message, /returned HTML instead of JSON/);
        assert.doesNotMatch(error.message, /Unexpected token/);
        return true;
      },
    );
  });
});

test('compileResponse returns fallback when no annotations exist', () => {
  const segments = [segment()];
  const result = compileResponse(segments, []);
  assert.equal(result, '[no annotations — acknowledged in full, proceed]');
});

test('compileResponse returns fallback when empty input', () => {
  assert.equal(compileResponse([], []), '[no annotations — acknowledged in full, proceed]');
});

test('compileResponse formats note annotation with body', () => {
  const segments = [segment({ id: 1, sequence_number: 1 })];
  const annotations = [
    annotation({ segment_id: 1, annotation_type: 'note', body: 'Consider renaming this variable.' }),
  ];
  const result = compileResponse(segments, annotations);

  assert.match(result, /\[segment 1 · abc12345\]/);
  assert.match(result, /This is a test segment/);
  assert.match(result, /\[note\]: Consider renaming this variable/);
  assert.doesNotMatch(result, /no annotations/);
});

test('compileResponse formats skip annotation without body', () => {
  const segments = [segment({ id: 1, sequence_number: 1 })];
  const annotations = [
    annotation({ segment_id: 1, annotation_type: 'skip', body: null }),
  ];
  const result = compileResponse(segments, annotations);

  assert.match(result, /\[skip — no response needed\]/);
  assert.doesNotMatch(result, /\[note\]/);
});

test('compileResponse formats done annotation with body', () => {
  const segments = [segment({ id: 1, sequence_number: 1 })];
  const annotations = [
    annotation({ segment_id: 1, annotation_type: 'done', body: 'Already fixed in commit abc.' }),
  ];
  const result = compileResponse(segments, annotations);

  assert.match(result, /\[done — already handled\]: Already fixed/);
});

test('compileResponse formats done annotation without body', () => {
  const segments = [segment({ id: 1, sequence_number: 1 })];
  const annotations = [
    annotation({ segment_id: 1, annotation_type: 'done', body: null }),
  ];
  const result = compileResponse(segments, annotations);

  assert.match(result, /\[done — already handled\]/);
  assert.doesNotMatch(result, /\[done — already handled\]:/);
});

test('compileResponse formats flag annotation with body', () => {
  const segments = [segment({ id: 1, sequence_number: 1 })];
  const annotations = [
    annotation({ segment_id: 1, annotation_type: 'flag', body: 'Needs discussion in standup.' }),
  ];
  const result = compileResponse(segments, annotations);

  assert.match(result, /\[FLAG\]: Needs discussion in standup/);
});

test('compileResponse formats flag annotation without body as needs discussion', () => {
  const segments = [segment({ id: 1, sequence_number: 1 })];
  const annotations = [
    annotation({ segment_id: 1, annotation_type: 'flag', body: null }),
  ];
  const result = compileResponse(segments, annotations);

  assert.match(result, /\[FLAG\]: needs discussion/);
});

test('compileResponse formats note annotation without body as acknowledged', () => {
  const segments = [segment({ id: 1, sequence_number: 1 })];
  const annotations = [
    annotation({ segment_id: 1, annotation_type: 'note', body: null }),
  ];
  const result = compileResponse(segments, annotations);

  assert.match(result, /\[note\]: acknowledged/);
});

test('compileResponse formats code_block segments with truncated first line', () => {
  const segments = [codeSegment({ id: 2, sequence_number: 2 })];
  const annotations = [
    annotation({ segment_id: 2, annotation_type: 'note', body: 'Use two spaces.' }),
  ];
  const result = compileResponse(segments, annotations);

  assert.match(result, /\[code block: console\.log\("hello"\)/);
});

test('compileResponse formats heading segments directly', () => {
  const segments = [headingSegment({ id: 3, sequence_number: 3 })];
  const annotations = [
    annotation({ segment_id: 3, annotation_type: 'note', body: 'Check heading depth.' }),
  ];
  const result = compileResponse(segments, annotations);

  assert.match(result, /Section Title/);
});

test('compileResponse includes unannotated segment count in footer', () => {
  const segments = [
    segment({ id: 1, sequence_number: 1 }),
    segment({ id: 4, sequence_number: 2, segment_hash: 'ffff0000aaaa1111', raw_markdown: 'Second segment.', text: 'Second segment.' }),
    segment({ id: 5, sequence_number: 3, segment_hash: 'bbbb2222cccc3333', raw_markdown: 'Third segment.', text: 'Third segment.' }),
  ];
  const annotations = [
    annotation({ segment_id: 1, annotation_type: 'note', body: 'Looks good.' }),
  ];
  const result = compileResponse(segments, annotations);

  assert.match(result, /\[segment 1 · abc12345\]/);
  assert.match(result, /\[2 section\(s\) not annotated/);
  assert.match(result, /acknowledged, proceed with flagged/);
});

test('compileResponse omits footer when all segments annotated', () => {
  const segments = [
    segment({ id: 1, sequence_number: 1 }),
    segment({ id: 4, sequence_number: 2, segment_hash: 'ffff0000aaaa1111', raw_markdown: 'Second segment.', text: 'Second segment.' }),
  ];
  const annotations = [
    annotation({ segment_id: 1, annotation_type: 'note', body: 'Looks good.' }),
    annotation({ segment_id: 4, annotation_type: 'done', body: 'Handled.' }),
  ];
  const result = compileResponse(segments, annotations);

  assert.match(result, /\[segment 1 · abc12345\]/);
  assert.match(result, /\[segment 2 · ffff0000\]/);
  assert.doesNotMatch(result, /section\(s\) not annotated/);
  assert.doesNotMatch(result, /no annotations/);
});

test('compileResponse skips segments without annotations', () => {
  const segments = [
    segment({ id: 1, sequence_number: 1 }),
    headingSegment({ id: 3, sequence_number: 2, raw_markdown: '## Unrelated', text: 'Unrelated' }),
  ];
  const annotations = [
    annotation({ segment_id: 1, annotation_type: 'skip' }),
  ];
  const result = compileResponse(segments, annotations);

  // Only segment 1 appears; segment 3 is skipped (no annotations)
  assert.match(result, /\[segment 1/);
  assert.doesNotMatch(result, /Unrelated/);
});

test('compileResponse shows segment hash prefix (first 8 chars)', () => {
  const segments = [segment({ id: 1, sequence_number: 1, segment_hash: 'abcdef1234567890' })];
  const annotations = [annotation({ segment_id: 1, annotation_type: 'note', body: 'ok' })];
  const result = compileResponse(segments, annotations);

  assert.match(result, /abcdef12/);
});

test('compileResponse handles short segment hash gracefully', () => {
  const segments = [segment({ id: 1, sequence_number: 1, segment_hash: 'ab' })];
  const annotations = [annotation({ segment_id: 1, annotation_type: 'note', body: 'ok' })];
  const result = compileResponse(segments, annotations);

  assert.match(result, /· ab\]/);
});

test('compileResponse handles long text truncation (>80 chars)', () => {
  const longText = 'A'.repeat(100);
  const segments = [segment({ id: 1, sequence_number: 1, raw_markdown: longText, text: longText })];
  const annotations = [annotation({ segment_id: 1, annotation_type: 'note', body: 'too long' })];
  const result = compileResponse(segments, annotations);

  assert.equal(result.includes('A'.repeat(80) + '...'), true);
  assert.equal(result.includes(longText), false);
});
