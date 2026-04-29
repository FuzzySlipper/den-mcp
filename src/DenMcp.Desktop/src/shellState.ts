export const shellTabs = [
  { id: 'operator', label: 'operator', icon: '⌬', badge: null },
  { id: 'tasks', label: 'tasks', icon: '◫', badge: null },
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
}

export type ShellStatePatch = Partial<ShellState>;

export const defaultShellState: ShellState = {
  theme: 'amber-dark',
  accent: 'amber',
  density: 'comfortable',
  bodyFont: 'sans',
  railMode: 'expanded',
  consoleMode: 'preview',
  activeTab: 'operator',
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
