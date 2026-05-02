import { useMemo, useRef, useState } from 'react';
import { ShellConsoleMode, shellConsoleModes } from '../shellState';
import { ConsoleCommandHistoryEntry, ConsoleCommandLine, ConsoleLine } from '../consoleLines';

interface ConsoleDockProps {
  mode: ShellConsoleMode;
  onModeChange: (mode: ShellConsoleMode) => void;
  lines: ConsoleLine[];
  onRunCommand?: (command: string) => Promise<void>;
  consoleCommands?: { name: string; displayName: string; description: string; needsTarget: boolean }[];
  consoleCommandHistory?: ConsoleCommandHistoryEntry[];
  /** In-flight progress lines from the currently running command. */
  activeProgressLines?: ConsoleCommandLine[];
  /** Name of the currently running command for the in-flight progress header. */
  activeProgressCommand?: string;
}

type InputMode = 'filter' | 'palette' | 'agent';

function detectInputMode(value: string): InputMode {
  if (value.startsWith('/')) return 'palette';
  if (value.startsWith('@')) return 'agent';
  return 'filter';
}

function modeGlyph(mode: ShellConsoleMode): string {
  switch (mode) {
    case 'collapsed': return '▁';
    case 'preview': return '▂';
    case 'half': return '▄';
    case 'full': return '█';
  }
}

export function ConsoleDock({
  mode,
  onModeChange,
  lines,
  onRunCommand,
  consoleCommands,
  consoleCommandHistory,
  activeProgressLines,
  activeProgressCommand,
}: ConsoleDockProps) {
  const [inputValue, setInputValue] = useState('');
  const [runningCommand, setRunningCommand] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const inputMode = detectInputMode(inputValue);

  // Merge diagnostic lines with command history entries for the output display.
  // In-flight progress lines are rendered before the final response history.
  const displayLines = useMemo(() => {
    const result: { kind: 'diag' | 'cmd-start' | 'cmd-line' | 'cmd-end' | 'progress-line'; data: unknown; key: string }[] = [];

    // When showing command history, prepend history entries
    // In-flight progress lines: rendered before history when a command is running.
    // Shows the actual command name in the header and a closing delimiter.
    if (activeProgressLines && activeProgressLines.length > 0) {
      const progressCommand = activeProgressCommand || '…';
      result.push({
        kind: 'cmd-start',
        data: { command: progressCommand, executedAt: new Date().toISOString(), lines: [], status: 'running' as const },
        key: 'progress:running',
      });
      for (let i = 0; i < activeProgressLines.length; i++) {
        result.push({
          kind: 'progress-line',
          data: activeProgressLines[i],
          key: `progress:${i}`,
        });
      }
      // Close the in-flight progress block with a delimiter
      result.push({
        kind: 'cmd-end',
        data: null,
        key: 'progress:running:end',
      });
    }

    if (showHistory && consoleCommandHistory && consoleCommandHistory.length > 0) {
      for (const entry of consoleCommandHistory) {
        result.push({
          kind: 'cmd-start',
          data: entry,
          key: `cmd:${entry.executedAt}:start`,
        });
        for (let i = 0; i < entry.lines.length; i++) {
          result.push({
            kind: 'cmd-line',
            data: entry.lines[i],
            key: `cmd:${entry.executedAt}:${i}`,
          });
        }
        result.push({
          kind: 'cmd-end',
          data: entry,
          key: `cmd:${entry.executedAt}:end`,
        });
      }
    } else {
      // Show diagnostic lines filtered by input mode
      let effectiveLines = lines;
      if (inputMode === 'filter' && inputValue.trim()) {
        const query = inputValue.toLowerCase();
        effectiveLines = lines.filter(
          (line) =>
            line.message.toLowerCase().includes(query) ||
            line.level.toLowerCase().includes(query) ||
            line.ts.toLowerCase().includes(query),
        );
      }

      for (let i = 0; i < effectiveLines.length; i++) {
        result.push({
          kind: 'diag',
          data: effectiveLines[i],
          key: `${effectiveLines[i].ts}:${i}`,
        });
      }

      // If there are recent command history entries, show them after diagnostics
      if (consoleCommandHistory && consoleCommandHistory.length > 0) {
        result.push({
          kind: 'cmd-end',
          data: null,
          key: 'cmd-separator',
        });
        const recent = consoleCommandHistory.slice(0, 3);
        for (const entry of recent) {
          result.push({
            kind: 'cmd-start',
            data: entry,
            key: `cmd:${entry.executedAt}:start`,
          });
          for (let i = 0; i < Math.min(entry.lines.length, 5); i++) {
            result.push({
              kind: 'cmd-line',
              data: entry.lines[i],
              key: `cmd:${entry.executedAt}:${i}`,
            });
          }
          result.push({
            kind: 'cmd-end',
            data: entry,
            key: `cmd:${entry.executedAt}:end`,
          });
        }
      }
    }

    return result;
  }, [lines, inputValue, inputMode, showHistory, consoleCommandHistory, activeProgressLines, activeProgressCommand]);

  const modeIndicator = inputMode !== 'filter'
    ? (inputMode === 'palette' ? '[command]' : '[agent prompt]')
    : null;

  const handleInputChange = (value: string) => {
    setInputValue(value);
    if (showHistory && !value.startsWith('/')) {
      setShowHistory(false);
    }
  };

  const handleKeyDown = async (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter' && inputValue.trim() && onRunCommand) {
      const trimmed = inputValue.trim();

      // If in palette mode (/), strip the leading /
      const command = trimmed.startsWith('/') ? trimmed.slice(1) : trimmed;

      if (command) {
        setRunningCommand(true);
        setShowHistory(false);
        try {
          await onRunCommand(command);
        } finally {
          setRunningCommand(false);
          setInputValue('');
          // After running a command, show the history briefly
          setShowHistory(true);
        }
      }
    } else if (event.key === 'Escape') {
      setInputValue('');
      setShowHistory(false);
    } else if (event.key === 'ArrowUp' && consoleCommandHistory && consoleCommandHistory.length > 0) {
      event.preventDefault();
      // Fill input with last command
      setInputValue('/' + consoleCommandHistory[0].command);
      setShowHistory(true);
    }
  };

  return (
    <section className="console-dock" data-mode={mode} aria-label="Console dock">
      <div className="console-header">
        <div className="console-prompt">
          <span className="console-glyph" aria-hidden="true">›_</span>
          <span className="console-target">den-mcp · operator</span>
          {modeIndicator ? (
            <span className="console-mode-stub">{modeIndicator}</span>
          ) : null}
          {runningCommand ? <span className="console-running" aria-label="Running command">⟳</span> : null}
          <input
            ref={inputRef}
            aria-label={inputMode === 'palette' ? 'Console command' : inputMode === 'agent' ? 'Agent prompt (stub)' : 'Filter console logs'}
            placeholder={
              inputMode === 'palette'
                ? 'type a command (help, refresh, git-status…)'
                : inputMode === 'agent'
                  ? 'agent prompt stub — type a message…'
                  : 'run a command (/help), ask an agent (@), or filter logs…'
            }
            value={inputValue}
            onChange={(event) => handleInputChange(event.target.value)}
            onKeyDown={handleKeyDown}
            disabled={runningCommand}
          />
        </div>
        <div className="console-controls">
          {consoleCommandHistory && consoleCommandHistory.length > 0 ? (
            <button
              type="button"
              className={showHistory ? 'active' : ''}
              title="Toggle command history"
              onClick={() => setShowHistory((prev) => !prev)}
            >
              ⌕
            </button>
          ) : null}
          {shellConsoleModes.map((option) => (
            <button
              key={option}
              type="button"
              title={option}
              className={mode === option ? 'active' : ''}
              onClick={() => onModeChange(option)}
            >
              {modeGlyph(option)}
            </button>
          ))}
        </div>
      </div>
      {mode !== 'collapsed' && (
        <div className="console-output" aria-live="polite">
          {displayLines.length === 0 ? (
            <div className="console-line">
              <span className="ts">--:--:--</span>
              <span className="lvl info">info</span>
              <span>
                {showHistory
                  ? 'no command history'
                  : inputMode !== 'filter'
                    ? `no matching lines in ${inputMode} mode`
                    : inputValue.trim()
                      ? `no lines match "${inputValue}"`
                      : 'waiting for runtime diagnostics'}
              </span>
            </div>
          ) : (
            displayLines.map((item) => {
              if (item.kind === 'cmd-start') {
                const entry = item.data as ConsoleCommandHistoryEntry;
                const isRunning = entry.status === 'running';
                const statusLabel = isRunning ? 'run' : entry.status === 'success' ? 'ok' : 'err';
                const statusClass = isRunning ? 'run' : entry.status === 'success' ? 'ok' : 'err';
                return (
                  <div className={`console-line console-cmd-header ${isRunning ? 'console-cmd-inflight' : ''}`} key={item.key}>
                    <span className="ts">{formatTimestampShort(entry.executedAt)}</span>
                    <span className={`lvl ${statusClass}`}>{statusLabel}</span>
                    <span className="console-cmd-name">{isRunning ? '⟳' : '▶'} {entry.command}</span>
                    {entry.errorMessage ? (
                      <span className="console-cmd-error">{entry.errorMessage}</span>
                    ) : null}
                  </div>
                );
              }

              if (item.kind === 'cmd-line' || item.kind === 'progress-line') {
                const line = item.data as { level: string; timestamp: string; source: string; message: string };
                return (
                  <div className="console-line console-cmd-output" key={item.key}>
                    <span className="ts">{formatTimestampShort(line.timestamp)}</span>
                    <span className={`lvl ${line.level}`}>{line.level}</span>
                    <span className="console-cmd-source">{line.source}</span>
                    <span>{line.message}</span>
                  </div>
                );
              }

              if (item.kind === 'cmd-end') {
                return (
                  <div className="console-line console-cmd-separator" key={item.key}>
                    <span className="ts" />
                    <span className="lvl" />
                    <span className="console-cmd-dash">· · ·</span>
                  </div>
                );
              }

              // Diagnostic line
              const line = item.data as ConsoleLine;
              return (
                <div className="console-line" key={item.key}>
                  <span className="ts">{line.ts}</span>
                  <span className={`lvl ${line.level}`}>{line.level}</span>
                  <span>{line.message}</span>
                </div>
              );
            })
          )}
        </div>
      )}
    </section>
  );
}

function formatTimestampShort(isoString: string): string {
  const d = new Date(isoString);
  if (Number.isNaN(d.getTime())) return '--:--:--';
  return d.toLocaleTimeString();
}
