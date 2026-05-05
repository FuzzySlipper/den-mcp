import assert from 'node:assert/strict';
import test from 'node:test';
import {
  acceleratorMatchesEvent,
  defaultShellState,
  loadShellState,
  nextConsoleMode,
  nextTheme,
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
    selectedProjectId: null,
    hotkeys: { cycleTabForward: 'Ctrl+Tab', goBack: 'Browser_Back', focusConsole: 'Ctrl+`' },
  });

  assert.deepEqual(parseShellState('{"theme":"neon","activeTab":"mock"}'), defaultShellState);
  assert.deepEqual(parseShellState('not-json'), defaultShellState);
});

test('shell state serialization is stable and storage-backed', () => {
  const state = parseShellState({ theme: 'graphite-dark', accent: 'cyan', consoleMode: 'half', activeTab: 'git' });
  const serialized = serializeShellState(state);
  assert.equal(serialized, '{"theme":"graphite-dark","accent":"cyan","density":"comfortable","bodyFont":"sans","railMode":"expanded","consoleMode":"half","activeTab":"git","selectedProjectId":null,"hotkeys":{"cycleTabForward":"Ctrl+Tab","goBack":"Browser_Back","focusConsole":"Ctrl+`"}}');

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

  assert.equal(nextTheme('amber-dark'), 'graphite-dark');
  assert.equal(nextTheme('graphite-dark'), 'amber-dark');
});

function fakeKeyEvent(props) {
  return {
    ctrlKey: false,
    metaKey: false,
    altKey: false,
    shiftKey: false,
    key: '',
    ...props,
  };
}

test('acceleratorMatchesEvent rejects empty and Browser_Back accelerators', () => {
  assert.equal(acceleratorMatchesEvent('', fakeKeyEvent({ key: 'Tab', ctrlKey: true })), false);
  assert.equal(acceleratorMatchesEvent('Browser_Back', fakeKeyEvent({ key: 'Alt', altKey: true })), false);
});

test('acceleratorMatchesEvent matches Ctrl+Tab', () => {
  const match = fakeKeyEvent({ key: 'Tab', ctrlKey: true });
  const noCtrl = fakeKeyEvent({ key: 'Tab' });
  const withShift = fakeKeyEvent({ key: 'Tab', ctrlKey: true, shiftKey: true });

  assert.equal(acceleratorMatchesEvent('Ctrl+Tab', match), true);
  assert.equal(acceleratorMatchesEvent('Ctrl+Tab', noCtrl), false);
  assert.equal(acceleratorMatchesEvent('Ctrl+Tab', withShift), false);
});

test('acceleratorMatchesEvent matches Ctrl+`', () => {
  const match = fakeKeyEvent({ key: '`', ctrlKey: true });
  const noCtrl = fakeKeyEvent({ key: '`' });

  assert.equal(acceleratorMatchesEvent('Ctrl+`', match), true);
  assert.equal(acceleratorMatchesEvent('Ctrl+`', noCtrl), false);
});

test('acceleratorMatchesEvent matches Shift+Up', () => {
  const match = fakeKeyEvent({ key: 'ArrowUp', shiftKey: true });
  const noShift = fakeKeyEvent({ key: 'ArrowUp' });

  assert.equal(acceleratorMatchesEvent('Shift+Up', match), true);
  assert.equal(acceleratorMatchesEvent('Shift+Up', noShift), false);
});

test('acceleratorMatchesEvent matches Alt+Left for goBack', () => {
  const match = fakeKeyEvent({ key: 'ArrowLeft', altKey: true });
  const wrongKey = fakeKeyEvent({ key: 'ArrowRight', altKey: true });

  assert.equal(acceleratorMatchesEvent('Alt+Left', match), true);
  assert.equal(acceleratorMatchesEvent('Alt+Left', wrongKey), false);
});

test('acceleratorMatchesEvent is case-insensitive for letter keys', () => {
  const lower = fakeKeyEvent({ key: 'a', ctrlKey: true });
  const upper = fakeKeyEvent({ key: 'A', ctrlKey: true });

  assert.equal(acceleratorMatchesEvent('Ctrl+A', lower), true);
  assert.equal(acceleratorMatchesEvent('Ctrl+A', upper), true);
});

test('acceleratorMatchesEvent requires exact modifier set', () => {
  const ctrlOnly = fakeKeyEvent({ key: 'A', ctrlKey: true });
  const ctrlShift = fakeKeyEvent({ key: 'A', ctrlKey: true, shiftKey: true });

  assert.equal(acceleratorMatchesEvent('Ctrl+A', ctrlOnly), true);
  assert.equal(acceleratorMatchesEvent('Ctrl+A', ctrlShift), false);
  assert.equal(acceleratorMatchesEvent('Ctrl+Shift+A', ctrlShift), true);
});
