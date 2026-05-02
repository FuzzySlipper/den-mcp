/**
 * Pure command-palette logic for the Den Desktop titlebar search entrypoint.
 *
 * Defines the V1 command set (tab navigation, task filtering) and provides
 * text matching/scoring helpers. No React or bridge deps here.
 */

import { shellTabs, type ShellTabId } from './shellState.ts';
import type { TaskStatusFilter } from './tasksDashboardView.ts';

// ── Types ──────────────────────────────────────────────────────

export interface PaletteCommand {
  id: string;
  label: string;
  group: string;
  shortcut?: string;
  action: PaletteAction;
}

export type PaletteAction =
  | { type: 'navigate'; tab: ShellTabId }
  | { type: 'filter_tasks'; filter: TaskStatusFilter }
  | { type: 'cycle_theme' }
  | { type: 'toggle_console' };

export interface PaletteState {
  open: boolean;
  query: string;
  selectedIndex: number;
}

export const defaultPaletteState: PaletteState = {
  open: false,
  query: '',
  selectedIndex: 0,
};

// ── V1 command definitions ─────────────────────────────────────

export function buildPaletteCommands(): PaletteCommand[] {
  const commands: PaletteCommand[] = [];

  // Tab navigation commands
  for (const tab of shellTabs) {
    commands.push({
      id: `nav:${tab.id}`,
      label: `Go to ${tab.label}`,
      group: 'Navigation',
      action: { type: 'navigate', tab: tab.id },
    });
  }

  // Task filter commands
  const taskFilters: TaskStatusFilter[] = ['all', 'in_progress', 'review', 'blocked', 'planned', 'done', 'cancelled'];
  for (const filter of taskFilters) {
    commands.push({
      id: `filter:${filter}`,
      label: filter === 'all' ? 'Show all tasks' : `Filter tasks: ${filter.replaceAll('_', ' ')}`,
      group: 'Tasks',
      action: { type: 'filter_tasks', filter },
    });
  }

  // Utility commands
  commands.push({
    id: 'util:cycle_theme',
    label: 'Cycle theme',
    group: 'Utility',
    shortcut: 'titlebar ◐',
    action: { type: 'cycle_theme' },
  });
  commands.push({
    id: 'util:toggle_console',
    label: 'Toggle console',
    group: 'Utility',
    action: { type: 'toggle_console' },
  });

  return commands;
}

// ── Filtering / scoring ────────────────────────────────────────

/**
 * Score a query against a label. Higher = better match.
 * Returns 0 for no match.
 */
export function scoreCommand(query: string, label: string): number {
  const q = query.toLowerCase().trim();
  if (q.length === 0) return 1; // neutral rank for empty query

  const l = label.toLowerCase();

  // Exact prefix match
  if (l.startsWith(q)) return 100;

  // Word-boundary prefix match (e.g. "go to" matches "Go to Tasks")
  const words = l.split(/\s+/);
  const wordPrefix = words.some((w) => w.startsWith(q));
  if (wordPrefix) return 80;

  // Substring contains
  if (l.includes(q)) return 60;

  // Fuzzy: all query chars appear in order
  let qi = 0;
  for (let li = 0; li < l.length && qi < q.length; li++) {
    if (l[li] === q[qi]) qi++;
  }
  if (qi === q.length) return 40;

  return 0; // no match
}

export function filterCommands(commands: PaletteCommand[], query: string): PaletteCommand[] {
  if (query.trim().length === 0) return commands;

  const scored = commands
    .map((cmd) => ({ cmd, score: scoreCommand(query, cmd.label) }))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score || a.cmd.label.localeCompare(b.cmd.label));

  return scored.map((entry) => entry.cmd);
}

/**
 * Clamp selectedIndex to [0, filteredCommands.length - 1].
 */
export function clampSelectedIndex(index: number, count: number): number {
  if (count === 0) return 0;
  return Math.max(0, Math.min(index, count - 1));
}
