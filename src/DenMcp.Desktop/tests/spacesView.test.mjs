import assert from 'node:assert/strict';
import test from 'node:test';

function space(overrides = {}) {
  return {
    id: 'personal-1',
    name: 'Personal',
    kind: 'personal',
    visibility: 'normal',
    owner: null,
    rootPath: null,
    description: null,
    createdAt: null,
    updatedAt: null,
    ...overrides,
  };
}

test('space kind label uses raw kind', () => {
  const s = space();
  assert.equal(s.kind, 'personal');
});

test('space visibility hidden is distinct from normal', () => {
  const normal = space();
  const hidden = space({ visibility: 'hidden' });
  assert.notEqual(normal.visibility, hidden.visibility);
});

test('space list excludes project-kind spaces in sidecar filtering', () => {
  const spaces = [
    space({ id: 'proj-1', name: 'Project', kind: 'project' }),
    space({ id: 'personal-1', name: 'Personal', kind: 'personal' }),
    space({ id: 'assistant-1', name: 'Assistant', kind: 'assistant' }),
  ];
  const nonProject = spaces.filter((s) => s.kind !== 'project');
  assert.equal(nonProject.length, 2);
  assert.equal(nonProject[0].kind, 'personal');
  assert.equal(nonProject[1].kind, 'assistant');
});
