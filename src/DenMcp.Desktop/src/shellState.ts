export const shellTabs = [
  { id: 'operator', label: 'operator', icon: '⌬', badge: null },
  { id: 'agent', label: 'agent', icon: '◎', badge: null },
  { id: 'tasks', label: 'tasks', icon: '◫', badge: null },
  { id: 'messages', label: 'messages', icon: '✉', badge: null },
  { id: 'docs', label: 'docs', icon: '📄', badge: null },
  { id: 'git', label: 'git', icon: '⋔', badge: null },
  { id: 'compare', label: 'compare', icon: '⫶⫶', badge: null },
  { id: 'terminals', label: 'terminals', icon: '>_', badge: null },
  { id: 'collaboration', label: 'collaboration', icon: '✺', badge: null },
  { id: 'settings', label: 'settings', icon: '⚙', badge: null },
] as const;

export const shellThemes = ['amber-dark', 'graphite-dark'] as const;
export const shellAccents = ['amber', 'cyan', 'green', 'violet'] as const;
export const shellDensities = ['compact', 'comfortable', 'spacious'] as const;
export const shellBodyFonts = ['sans', 'mono'] as const;
export const shellRailModes = ['expanded', 'collapsed', 'hidden'] as const;
export const shellConsoleModes = ['collapsed', 'preview', 'half', 'full'] as const;

export type ShellTabId = (typeof shellTabs)[number]['id'];
export type ShellTheme = (typeof shellThemes)[number];
export type ShellAccent = (typeof shellAccents)[number];
export type ShellDensity = (typeof shellDensities)[number];
export type ShellBodyFont = (typeof shellBodyFonts)[number];
export type ShellRailMode = (typeof shellRailModes)[number];
export type ShellConsoleMode = (typeof shellConsoleModes)[number];

export interface ShellState {
  theme: ShellTheme;
  accent: ShellAccent;
  density: ShellDensity;
  bodyFont: ShellBodyFont;
  railMode: ShellRailMode;
  consoleMode: ShellConsoleMode;
  activeTab: ShellTabId;
  /** Selected space/project filter shared across tabs. '_global' shows all spaces, null means 'no selection'. Kept as selectedProjectId for storage compatibility. */
  selectedProjectId: string | null;
  /** Hotkey bindings: action name → Electron accelerator string. */
  hotkeys: Record<string, string>;
}

export type ShellStatePatch = Partial<ShellState>;

/** Default hotkey bindings: action name → Electron accelerator string. */
export const defaultHotkeys: Record<string, string> = {
  'cycleTabForward': 'Ctrl+Tab',
  'goBack': 'Browser_Back',
  'focusConsole': 'Ctrl+`',
};

/** Canonical hotkey action names with user-facing labels. */
export const hotkeyActions: { action: string; label: string; description: string }[] = [
  { action: 'cycleTabForward', label: 'Cycle tab forward', description: 'Move to the next tab in the tab bar' },
  { action: 'goBack', label: 'Go back', description: 'Navigate back (mouse back button or shortcut)' },
  { action: 'focusConsole', label: 'Focus console', description: 'Jump to the console/command input' },
];

export const defaultShellState: ShellState = {
  theme: 'amber-dark',
  accent: 'amber',
  density: 'comfortable',
  bodyFont: 'sans',
  railMode: 'expanded',
  consoleMode: 'preview',
  activeTab: 'operator',
  selectedProjectId: null,
  hotkeys: { ...defaultHotkeys },
};

export const shellStateStorageKey = 'den-desktop:shell-state:v1';

function coerceChoice<T extends readonly string[]>(value: unknown, choices: T, fallback: T[number]): T[number] {
  return typeof value === 'string' && choices.includes(value) ? value as T[number] : fallback;
}

function objectLike(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

export function parseShellState(value: unknown, fallback: ShellState = defaultShellState): ShellState {
  const input = typeof value === 'string' ? parseJsonObject(value) : objectLike(value);
  return {
    theme: coerceChoice(input.theme, shellThemes, fallback.theme),
    accent: coerceChoice(input.accent, shellAccents, fallback.accent),
    density: coerceChoice(input.density, shellDensities, fallback.density),
    bodyFont: coerceChoice(input.bodyFont, shellBodyFonts, fallback.bodyFont),
    railMode: coerceChoice(input.railMode, shellRailModes, fallback.railMode),
    consoleMode: coerceChoice(input.consoleMode, shellConsoleModes, fallback.consoleMode),
    activeTab: coerceChoice(input.activeTab, shellTabs.map((tab) => tab.id), fallback.activeTab),
    selectedProjectId: typeof input.selectedProjectId === 'string'
      ? (input.selectedProjectId === '_global' || input.selectedProjectId.length > 0 ? input.selectedProjectId : null)
      : (fallback.selectedProjectId ?? null),
    hotkeys: typeof input.hotkeys === 'object' && input.hotkeys !== null && !Array.isArray(input.hotkeys)
      ? { ...defaultHotkeys, ...Object.fromEntries(Object.entries(input.hotkeys).filter(([_, v]) => typeof v === 'string')) as Record<string, string> }
      : { ...fallback.hotkeys },
  };
}

function parseJsonObject(value: string): Record<string, unknown> {
  try {
    return objectLike(JSON.parse(value));
  } catch {
    return {};
  }
}

export function serializeShellState(state: ShellState): string {
  const normalized = parseShellState(state);
  return JSON.stringify({
    theme: normalized.theme,
    accent: normalized.accent,
    density: normalized.density,
    bodyFont: normalized.bodyFont,
    railMode: normalized.railMode,
    consoleMode: normalized.consoleMode,
    activeTab: normalized.activeTab,
    selectedProjectId: normalized.selectedProjectId,
    hotkeys: normalized.hotkeys,
  });
}

export interface ShellStateStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

export function loadShellState(storage: ShellStateStorage | null | undefined): ShellState {
  if (!storage) return defaultShellState;
  return parseShellState(storage.getItem(shellStateStorageKey));
}

export function saveShellState(storage: ShellStateStorage | null | undefined, state: ShellState): void {
  if (!storage) return;
  storage.setItem(shellStateStorageKey, serializeShellState(state));
}

export function nextConsoleMode(mode: ShellConsoleMode): ShellConsoleMode {
  const index = shellConsoleModes.indexOf(mode);
  return shellConsoleModes[(index + 1) % shellConsoleModes.length];
}

export function nextTheme(theme: ShellTheme): ShellTheme {
  const index = shellThemes.indexOf(theme);
  return shellThemes[(index + 1) % shellThemes.length];
}

export function shellStateToDataAttributes(state: ShellState): Record<string, string> {
  const normalized = parseShellState(state);
  return {
    'data-theme': normalized.theme,
    'data-accent': normalized.accent,
    'data-density': normalized.density,
    'data-body-font': normalized.bodyFont,
    'data-rail': normalized.railMode,
    'data-console': normalized.consoleMode,
    'data-active-tab': normalized.activeTab,
  };
}

export interface AttributeTarget {
  setAttribute(name: string, value: string): void;
}

export function applyShellDataAttributes(target: AttributeTarget, state: ShellState): void {
  const attributes = shellStateToDataAttributes(state);
  for (const [name, value] of Object.entries(attributes)) {
    target.setAttribute(name, value);
  }
}

/** Parse an Electron accelerator string into modifier/key expectations. */
function parseAccelerator(accelerator: string): { key: string; ctrl: boolean; meta: boolean; alt: boolean; shift: boolean } {
  const parts = accelerator.split('+').map((p) => p.trim());
  const key = parts.pop()!;
  const modifiers = new Set(parts);
  const isMac = typeof navigator !== 'undefined' && /Mac|iPod|iPhone|iPad/.test(navigator.platform);
  return {
    key,
    ctrl: modifiers.has('Ctrl') || (!isMac && modifiers.has('CommandOrControl')),
    meta: modifiers.has('Command') || (isMac && modifiers.has('CommandOrControl')),
    alt: modifiers.has('Alt'),
    shift: modifiers.has('Shift'),
  };
}

/**
 * Match an Electron accelerator string against a DOM KeyboardEvent.
 *
 * Returns true when the event's modifiers and key exactly match the
 * accelerator. Browser_Back accelerators always return false because
 * they are handled via the app-command event, not keyboard events.
 */
export function acceleratorMatchesEvent(accelerator: string, event: KeyboardEvent): boolean {
  if (!accelerator || accelerator === 'Browser_Back') return false;

  const parsed = parseAccelerator(accelerator);

  if (event.ctrlKey !== parsed.ctrl) return false;
  if (event.metaKey !== parsed.meta) return false;
  if (event.altKey !== parsed.alt) return false;
  if (event.shiftKey !== parsed.shift) return false;

  const domToElectron: Record<string, string> = {
    ArrowUp: 'Up',
    ArrowDown: 'Down',
    ArrowLeft: 'Left',
    ArrowRight: 'Right',
    ' ': 'Space',
    Escape: 'Escape',
    Enter: 'Enter',
    Tab: 'Tab',
    Backspace: 'Backspace',
    Delete: 'Delete',
    Home: 'Home',
    End: 'End',
    PageUp: 'PageUp',
    PageDown: 'PageDown',
  };

  const eventKey = domToElectron[event.key] ?? event.key;

  if (parsed.key.length === 1 && parsed.key !== '`') {
    return eventKey.toUpperCase() === parsed.key.toUpperCase();
  }

  return eventKey === parsed.key;
}
