import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  buildPaletteCommands,
  clampSelectedIndex,
  filterCommands,
  type PaletteAction,
  type PaletteCommand,
} from '../commandPalette.ts';
import type { TaskStatusFilter } from '../tasksDashboardView.ts';
import type { ShellTabId } from '../shellState.ts';

export interface CommandPaletteCallbacks {
  onNavigate: (tab: ShellTabId) => void;
  onFilterTasks: (filter: TaskStatusFilter) => void;
  onCycleTheme: () => void;
  onToggleConsole: () => void;
  onClose: () => void;
}

interface Props {
  open: boolean;
  callbacks: CommandPaletteCallbacks;
}

const allCommands = buildPaletteCommands();

export function CommandPalette({ open, callbacks }: Props) {
  const [query, setQuery] = useState('');
  const [selectedIndex, setSelectedIndex] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  // Reset state when opening
  useEffect(() => {
    if (open) {
      setQuery('');
      setSelectedIndex(0);
      // Focus input after render
      requestAnimationFrame(() => inputRef.current?.focus());
    }
  }, [open]);

  const filtered = useMemo(() => filterCommands(allCommands, query), [query]);

  // Clamp selection when filtered list changes
  useEffect(() => {
    setSelectedIndex((prev) => clampSelectedIndex(prev, filtered.length));
  }, [filtered.length]);

  const executeCommand = useCallback(
    (cmd: PaletteCommand) => {
      const action = cmd.action;
      callbacks.onClose();
      // Defer action so the palette closes first
      requestAnimationFrame(() => dispatchAction(action, callbacks));
    },
    [callbacks],
  );

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        callbacks.onClose();
        return;
      }
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        setSelectedIndex((prev) => clampSelectedIndex(prev + 1, filtered.length));
        return;
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        setSelectedIndex((prev) => clampSelectedIndex(prev - 1, filtered.length));
        return;
      }
      if (e.key === 'Enter') {
        e.preventDefault();
        const cmd = filtered[selectedIndex];
        if (cmd) executeCommand(cmd);
        return;
      }
    },
    [callbacks, filtered, selectedIndex, executeCommand],
  );

  // Global Ctrl+K shortcut
  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
        e.preventDefault();
        callbacks.onClose();
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [open, callbacks]);

  if (!open) return null;

  // Group filtered commands
  const groups = groupCommands(filtered);

  let globalIndex = 0;

  return (
    <div className="palette-backdrop" onClick={callbacks.onClose}>
      <div className="palette-panel" onClick={(e) => e.stopPropagation()}>
        <div className="palette-input-row">
          <span className="palette-glyph" aria-hidden="true">⌕</span>
          <input
            ref={inputRef}
            className="palette-input"
            type="text"
            placeholder="Type a command…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={handleKeyDown}
            aria-label="Command palette search"
            autoComplete="off"
          />
          <kbd className="palette-hint">Esc to close</kbd>
        </div>
        <div className="palette-list" role="listbox">
          {filtered.length === 0 ? (
            <div className="palette-empty">No matching commands.</div>
          ) : (
            groups.map(([group, commands]) => (
              <div key={group} className="palette-group">
                <div className="palette-group-label">{group}</div>
                {commands.map((cmd) => {
                  const idx = globalIndex++;
                  return (
                    <button
                      key={cmd.id}
                      type="button"
                      role="option"
                      aria-selected={idx === selectedIndex}
                      className={`palette-item ${idx === selectedIndex ? 'selected' : ''}`}
                      onClick={() => executeCommand(cmd)}
                      onMouseEnter={() => setSelectedIndex(idx)}
                    >
                      <span className="palette-item-label">{cmd.label}</span>
                      {cmd.shortcut && <kbd className="palette-item-shortcut">{cmd.shortcut}</kbd>}
                    </button>
                  );
                })}
              </div>
            ))
          )}
        </div>
      </div>
    </div>
  );
}

function dispatchAction(action: PaletteAction, callbacks: CommandPaletteCallbacks): void {
  switch (action.type) {
    case 'navigate':
      callbacks.onNavigate(action.tab);
      break;
    case 'filter_tasks':
      callbacks.onFilterTasks(action.filter);
      break;
    case 'cycle_theme':
      callbacks.onCycleTheme();
      break;
    case 'toggle_console':
      callbacks.onToggleConsole();
      break;
  }
}

function groupCommands(commands: PaletteCommand[]): [string, PaletteCommand[]][] {
  const map = new Map<string, PaletteCommand[]>();
  for (const cmd of commands) {
    const list = map.get(cmd.group) ?? [];
    list.push(cmd);
    map.set(cmd.group, list);
  }
  return [...map.entries()];
}
