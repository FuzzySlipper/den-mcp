import assert from 'node:assert/strict';
import test from 'node:test';
import {
  defaultShellState,
  loadShellState,
  nextConsoleMode,
  parseShellState,
  saveShellState,
  serializeShellState,
  shellStateStorageKey,
  shellStateToDataAttributes,
} from '../src/shellState.ts';

function memoryStorage(initial = null) {
  const values = new Map();
  if (initial !== null) values.set(shellStateStorageKey, initial);
  return {
    values,
    getItem(key) {
      return values.has(key) ? values.get(key) : null;
    },
    setItem(key, value) {
      values.set(key, value);
    },
  };
}

test('shell state parser accepts valid values and repairs invalid values', () => {
  const parsed = parseShellState({
    theme: 'graphite-dark',
    accent: 'violet',
    density: 'compact',
    bodyFont: 'mono',
    railMode: 'collapsed',
    consoleMode: 'full',
    activeTab: 'collaboration',
    ignored: true,
  });

  assert.deepEqual(parsed, {
    theme: 'graphite-dark',
    accent: 'violet',
    density: 'compact',
    bodyFont: 'mono',
    railMode: 'collapsed',
    consoleMode: 'full',
    activeTab: 'collaboration',
  });

  assert.deepEqual(parseShellState('{"theme":"neon","activeTab":"mock"}'), defaultShellState);
  assert.deepEqual(parseShellState('not-json'), defaultShellState);
});

test('shell state serialization is stable and storage-backed', () => {
  const state = parseShellState({ theme: 'graphite-dark', accent: 'cyan', consoleMode: 'half', activeTab: 'git' });
  const serialized = serializeShellState(state);
  assert.equal(serialized, '{"theme":"graphite-dark","accent":"cyan","density":"comfortable","bodyFont":"sans","railMode":"expanded","consoleMode":"half","activeTab":"git"}');

  const storage = memoryStorage();
  saveShellState(storage, state);
  assert.equal(storage.values.get(shellStateStorageKey), serialized);
  assert.deepEqual(loadShellState(storage), state);
});

test('data attributes and console cycling match shell contract', () => {
  const state = parseShellState({ theme: 'graphite-dark', accent: 'green', density: 'spacious', bodyFont: 'mono', railMode: 'hidden', consoleMode: 'collapsed', activeTab: 'terminals' });

  assert.deepEqual(shellStateToDataAttributes(state), {
    'data-theme': 'graphite-dark',
    'data-accent': 'green',
    'data-density': 'spacious',
    'data-body-font': 'mono',
    'data-rail': 'hidden',
    'data-console': 'collapsed',
    'data-active-tab': 'terminals',
  });

  assert.equal(nextConsoleMode('collapsed'), 'preview');
  assert.equal(nextConsoleMode('preview'), 'half');
  assert.equal(nextConsoleMode('half'), 'full');
  assert.equal(nextConsoleMode('full'), 'collapsed');
});
