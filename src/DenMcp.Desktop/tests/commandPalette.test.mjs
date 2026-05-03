import assert from 'node:assert/strict';
import test from 'node:test';
import {
  buildPaletteCommands,
  clampSelectedIndex,
  defaultPaletteState,
  filterCommands,
  scoreCommand,
} from '../src/commandPalette.ts';

test('buildPaletteCommands returns a non-empty command set', () => {
  const commands = buildPaletteCommands();
  assert.ok(commands.length > 0, 'expected at least one command');

  // Navigation commands
  const navCommands = commands.filter((c) => c.action.type === 'navigate');
  assert.ok(navCommands.length >= 9, `expected at least 9 navigation commands, got ${navCommands.length}`);

  // Task filter commands
  const filterCommands = commands.filter((c) => c.action.type === 'filter_tasks');
  assert.ok(filterCommands.length === 7, `expected 7 task filter commands, got ${filterCommands.length}`);

  // Utility commands
  const utilCommands = commands.filter((c) => c.action.type === 'cycle_theme' || c.action.type === 'toggle_console');
  assert.ok(utilCommands.length === 2, 'expected 2 utility commands');

  // All commands have required fields
  for (const cmd of commands) {
    assert.ok(cmd.id, `command missing id`);
    assert.ok(cmd.label, `command ${cmd.id} missing label`);
    assert.ok(cmd.group, `command ${cmd.id} missing group`);
    assert.ok(cmd.action, `command ${cmd.id} missing action`);
  }
});

test('each command has a unique id', () => {
  const commands = buildPaletteCommands();
  const ids = new Set(commands.map((c) => c.id));
  assert.equal(ids.size, commands.length, 'duplicate command ids found');
});

test('navigate actions reference valid tab ids', () => {
  const validTabs = ['operator', 'agent', 'tasks', 'messages', 'git', 'compare', 'terminals', 'collaboration', 'settings', 'docs'];
  const commands = buildPaletteCommands();
  const navCommands = commands.filter((c) => c.action.type === 'navigate');
  for (const cmd of navCommands) {
    assert.ok(validTabs.includes(cmd.action.tab), `invalid tab: ${cmd.action.tab}`);
  }
});

test('filter actions reference valid task status filters', () => {
  const validFilters = ['all', 'in_progress', 'review', 'blocked', 'planned', 'done', 'cancelled'];
  const commands = buildPaletteCommands();
  const filterCommands = commands.filter((c) => c.action.type === 'filter_tasks');
  for (const cmd of filterCommands) {
    assert.ok(validFilters.includes(cmd.action.filter), `invalid filter: ${cmd.action.filter}`);
  }
});

test('scoreCommand: exact prefix scores highest', () => {
  assert.equal(scoreCommand('Go to tasks', 'Go to tasks'), 100);
  assert.ok(scoreCommand('go', 'Go to tasks') >= 80);
  assert.ok(scoreCommand('Go to tasks', 'Go to tasks') > scoreCommand('tasks', 'Go to tasks'));
});

test('scoreCommand: prefix match scores higher than substring', () => {
  assert.ok(scoreCommand('Go', 'Go to tasks') > scoreCommand('tasks', 'Go to tasks'));
});

test('scoreCommand: substring match scores higher than fuzzy', () => {
  assert.ok(scoreCommand('tasks', 'Go to tasks') > scoreCommand('gtt', 'Go to tasks'));
});

test('scoreCommand: fuzzy match scores when chars appear in order', () => {
  assert.ok(scoreCommand('gtt', 'Go to tasks') > 0, 'fuzzy match should score > 0');
});

test('scoreCommand: no match returns 0', () => {
  assert.equal(scoreCommand('xyz', 'Go to tasks'), 0);
});

test('scoreCommand: empty query returns neutral score', () => {
  assert.equal(scoreCommand('', 'Go to tasks'), 1);
});

test('scoreCommand: case insensitive', () => {
  assert.equal(scoreCommand('go to', 'Go to tasks'), scoreCommand('GO TO', 'Go to tasks'));
});

test('scoreCommand: word boundary prefix matches', () => {
  // "to" is a word boundary in "Go to tasks"
  const score = scoreCommand('to', 'Go to tasks');
  assert.ok(score > 0, 'word boundary match should score > 0');
  assert.ok(score >= 80, 'word boundary prefix should score >= 80');
});

test('filterCommands: empty query returns all commands', () => {
  const commands = buildPaletteCommands();
  const filtered = filterCommands(commands, '');
  assert.equal(filtered.length, commands.length);
});

test('filterCommands: whitespace query returns all commands', () => {
  const commands = buildPaletteCommands();
  const filtered = filterCommands(commands, '   ');
  assert.equal(filtered.length, commands.length);
});

test('filterCommands: filters to matching commands', () => {
  const commands = buildPaletteCommands();
  const filtered = filterCommands(commands, 'tasks');
  assert.ok(filtered.length > 0, 'should have matches for "tasks"');
  assert.ok(filtered.length < commands.length, 'should filter out some commands');
  // All results should contain "tasks" or fuzzy-match
  for (const cmd of filtered) {
    assert.ok(scoreCommand('tasks', cmd.label) > 0, `${cmd.label} should match "tasks"`);
  }
});

test('filterCommands: results are sorted by score descending', () => {
  const commands = buildPaletteCommands();
  const filtered = filterCommands(commands, 'Go to');
  assert.ok(filtered.length >= 2, 'should match multiple "Go to ..." commands');
  // First result should have "Go to" prefix
  for (let i = 1; i < filtered.length; i++) {
    const scorePrev = scoreCommand('Go to', filtered[i - 1].label);
    const scoreCurr = scoreCommand('Go to', filtered[i].label);
    assert.ok(scorePrev >= scoreCurr, `results not sorted: ${filtered[i - 1].label}(${scorePrev}) < ${filtered[i].label}(${scoreCurr})`);
  }
});

test('filterCommands: no match returns empty array', () => {
  const commands = buildPaletteCommands();
  const filtered = filterCommands(commands, 'zzzznonexistent');
  assert.equal(filtered.length, 0);
});

test('clampSelectedIndex: clamps to valid range', () => {
  assert.equal(clampSelectedIndex(-1, 10), 0);
  assert.equal(clampSelectedIndex(5, 10), 5);
  assert.equal(clampSelectedIndex(10, 10), 9);
  assert.equal(clampSelectedIndex(15, 10), 9);
});

test('clampSelectedIndex: empty list returns 0', () => {
  assert.equal(clampSelectedIndex(0, 0), 0);
  assert.equal(clampSelectedIndex(5, 0), 0);
});

test('defaultPaletteState starts closed with empty query', () => {
  assert.equal(defaultPaletteState.open, false);
  assert.equal(defaultPaletteState.query, '');
  assert.equal(defaultPaletteState.selectedIndex, 0);
});

test('filterCommands: "filter" matches task filter commands', () => {
  const commands = buildPaletteCommands();
  const filtered = filterCommands(commands, 'filter');
  assert.ok(filtered.length >= 3, 'should match task filter commands');
  const allFilters = filtered.every((c) => c.action.type === 'filter_tasks');
  assert.ok(allFilters, 'all "filter" matches should be filter_tasks actions');
});

test('filterCommands: "theme" matches cycle_theme', () => {
  const commands = buildPaletteCommands();
  const filtered = filterCommands(commands, 'theme');
  assert.ok(filtered.length >= 1, 'should match cycle theme');
  const hasTheme = filtered.some((c) => c.action.type === 'cycle_theme');
  assert.ok(hasTheme, 'should include cycle_theme');
});
