import { useMemo, useState } from 'react';
import { ShellConsoleMode, shellConsoleModes } from '../shellState';
import { ConsoleLine } from '../consoleLines';

interface ConsoleDockProps {
  mode: ShellConsoleMode;
  onModeChange: (mode: ShellConsoleMode) => void;
  lines: ConsoleLine[];
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

const filterModes: InputMode[] = ['filter', 'palette', 'agent'];

export function ConsoleDock({ mode, onModeChange, lines }: ConsoleDockProps) {
  const [inputValue, setInputValue] = useState('');
  const inputMode = detectInputMode(inputValue);

  const filteredLines = useMemo(() => {
    if (inputMode === 'palette' || inputMode === 'agent') {
      // In palette/agent mode, show full lines and rely on the mode indicator
      return lines;
    }
    if (!inputValue.trim()) return lines;
    const query = inputValue.toLowerCase();
    return lines.filter(
      (line) =>
        line.message.toLowerCase().includes(query) ||
        line.level.toLowerCase().includes(query) ||
        line.ts.toLowerCase().includes(query),
    );
  }, [lines, inputValue, inputMode]);

  // Mode indicator label for palette/agent stubs
  const modeIndicator = inputMode !== 'filter'
    ? (inputMode === 'palette' ? '[command palette]' : '[agent prompt]')
    : null;

  const handleInputChange = (value: string) => {
    setInputValue(value);
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
          <input
            aria-label={inputMode === 'palette' ? 'Command palette (stub)' : inputMode === 'agent' ? 'Agent prompt (stub)' : 'Filter console logs'}
            placeholder={
              inputMode === 'palette'
                ? 'palette stub — type a command…'
                : inputMode === 'agent'
                  ? 'agent prompt stub — type a message…'
                  : 'run a command, ask an agent, or filter logs…'
            }
            value={inputValue}
            onChange={(event) => handleInputChange(event.target.value)}
          />
        </div>
        <div className="console-controls">
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
          {filteredLines.length === 0 ? (
            <div className="console-line">
              <span className="ts">--:--:--</span>
              <span className="lvl info">info</span>
              <span>
                {inputMode !== 'filter'
                  ? `no matching lines in ${inputMode} mode`
                  : inputValue.trim()
                    ? `no lines match "${inputValue}"`
                    : 'waiting for runtime diagnostics'}
              </span>
            </div>
          ) : (
            filteredLines.map((line, index) => (
              <div className="console-line" key={`${line.ts}:${index}`}>
                <span className="ts">{line.ts}</span>
                <span className={`lvl ${line.level}`}>{line.level}</span>
                <span>{line.message}</span>
              </div>
            ))
          )}
        </div>
      )}
    </section>
  );
}
