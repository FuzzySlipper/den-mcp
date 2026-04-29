import assert from 'node:assert/strict';
import test from 'node:test';
import { currentFindingDisplayNote, renderFindingMeta } from '../../src/DenMcp.Server/ClientApp/src/reviewFindings.ts';

function finding(overrides = {}) {
  return {
    id: 1,
    finding_key: 'R1-1',
    task_id: 1,
    review_round_id: 1,
    review_round_number: 1,
    finding_number: 1,
    created_by: 'reviewer',
    category: 'blocking_bug',
    summary: 'Fix it',
    notes: null,
    file_references: null,
    test_commands: null,
    status: 'open',
    status_updated_by: null,
    status_notes: null,
    status_updated_at: null,
    response_by: null,
    response_notes: null,
    response_at: null,
    follow_up_task_id: null,
    created_at: '2026-04-29T00:00:00Z',
    updated_at: '2026-04-29T00:00:00Z',
    ...overrides,
  };
}

test('currentFindingDisplayNote prefers explicit status notes', () => {
  assert.deepEqual(currentFindingDisplayNote(finding({
    status_notes: 'Verified in rereview',
    response_notes: 'Implemented on branch',
  })), { label: 'Status note', value: 'Verified in rereview' });
});

test('currentFindingDisplayNote hides stale response after later reviewer status update without notes', () => {
  const note = currentFindingDisplayNote(finding({
    status: 'verified_fixed',
    status_updated_by: 'reviewer',
    status_updated_at: '2026-04-29T00:10:00Z',
    response_by: 'coder',
    response_notes: 'Implemented on branch',
    response_at: '2026-04-29T00:05:00Z',
  }));

  assert.equal(note, null);
});

test('currentFindingDisplayNote shows newer implementer response after an older status update', () => {
  assert.deepEqual(currentFindingDisplayNote(finding({
    status: 'not_fixed',
    status_updated_by: 'reviewer',
    status_updated_at: '2026-04-29T00:05:00Z',
    response_by: 'coder',
    response_notes: 'Fixed in a later commit',
    response_at: '2026-04-29T00:10:00Z',
  })), { label: 'Response', value: 'Fixed in a later commit' });
});

test('currentFindingDisplayNote shows claimed_fixed response from same transition', () => {
  assert.deepEqual(currentFindingDisplayNote(finding({
    status: 'claimed_fixed',
    status_updated_by: 'coder',
    status_updated_at: '2026-04-29T00:10:00Z',
    response_by: 'coder',
    response_notes: 'Fixed in follow-up commit',
    response_at: '2026-04-29T00:10:00Z',
  })), { label: 'Response', value: 'Fixed in follow-up commit' });
});

test('renderFindingMeta includes files tests and current display note', () => {
  assert.deepEqual(renderFindingMeta(finding({
    file_references: ['src/Foo.cs:12'],
    test_commands: ['dotnet test'],
    status_notes: 'Confirmed',
  })), [
    'Files: src/Foo.cs:12',
    'Tests: dotnet test',
    'Status note: Confirmed',
  ]);
});
