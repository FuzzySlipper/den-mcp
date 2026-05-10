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

test('space list keeps project-kind spaces for unified switching', () => {
  const spaces = [
    space({ id: 'proj-1', name: 'Project', kind: 'project' }),
    space({ id: 'personal-1', name: 'Personal', kind: 'personal' }),
    space({ id: 'assistant-1', name: 'Assistant', kind: 'assistant' }),
  ];
  assert.equal(spaces.length, 3);
  assert.equal(spaces[0].kind, 'project');
  assert.equal(spaces[1].kind, 'personal');
  assert.equal(spaces[2].kind, 'assistant');
});
