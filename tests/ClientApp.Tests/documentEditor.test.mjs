import test from 'node:test';
import assert from 'node:assert/strict';

import {
  documentIdentity,
  documentSelectionAction,
  isDocumentEditorSaveShortcut,
  isSameDocument,
  shouldPromptForDirtyDocumentSwitch,
} from '../../src/DenMcp.Server/ClientApp/src/documentEditor.ts';

const doc = (projectId, slug) => ({
  id: 1,
  project_id: projectId,
  slug,
  title: slug,
  doc_type: 'note',
  tags: [],
  updated_at: '2026-04-27T00:00:00',
});

test('document editor switch guard only prompts for dirty switches to another document', () => {
  const current = doc('den-mcp', 'current');

  assert.equal(documentIdentity(current), 'den-mcp/current');
  assert.equal(isSameDocument(current, doc('den-mcp', 'current')), true);
  assert.equal(documentSelectionAction(current, doc('den-mcp', 'current'), true), 'keep_current');
  assert.equal(documentSelectionAction(current, doc('den-mcp', 'other'), false), 'select');
  assert.equal(documentSelectionAction(current, doc('_global', 'current'), true), 'prompt_for_dirty_switch');
  assert.equal(documentSelectionAction(current, doc('den-mcp', 'other'), true), 'prompt_for_dirty_switch');
  assert.equal(shouldPromptForDirtyDocumentSwitch(current, doc('den-mcp', 'current'), true), false);
  assert.equal(shouldPromptForDirtyDocumentSwitch(current, doc('den-mcp', 'other'), false), false);
  assert.equal(shouldPromptForDirtyDocumentSwitch(current, doc('_global', 'current'), true), true);
  assert.equal(shouldPromptForDirtyDocumentSwitch(current, doc('den-mcp', 'other'), true), true);
});

test('document editor save shortcut recognizes Ctrl+S and Cmd+S without Alt', () => {
  assert.equal(isDocumentEditorSaveShortcut({ key: 's', ctrlKey: true }), true);
  assert.equal(isDocumentEditorSaveShortcut({ key: 'S', metaKey: true }), true);
  assert.equal(isDocumentEditorSaveShortcut({ key: 's', ctrlKey: true, altKey: true }), false);
  assert.equal(isDocumentEditorSaveShortcut({ key: 'Enter', ctrlKey: true }), false);
  assert.equal(isDocumentEditorSaveShortcut({ key: 's' }), false);
});
