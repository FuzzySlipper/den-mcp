# Legacy Codex/Claude Bridge Notes

Status: retired from the supported Den runtime as of task `#812`.

Den previously carried several direct-agent bridge experiments:

- `bin/den-agent` — a wrapper around Claude/Codex/OMP CLIs that polled legacy
  dispatches and attempted terminal/kitty handoff behavior.
- `bin/den-codex-bridge` — a Codex app-server bridge prototype that consumed
  wake-worthy stream/dispatch entries and launched Codex turns.
- `docs/codex-app-server-bridge-plan.md` — historical design notes for that
  app-server bridge direction.
- older Claude/Codex guidance snippets that implied `claude-code`/`codex`
  dispatch routing was a normal workflow.

Those pieces are no longer supported runtime behavior. The current operator path
is Den web plus Pi/conductor runs, task-thread messages, review workflow records,
agent-stream ops/control events, and AgentRun state.

What remains supported:

- Claude Code, Codex CLI, and similar tools may still use Den MCP manually when
  a human starts them.
- Agent identity strings such as `claude-code` or `codex` may still appear in
  historical messages, audit records, and manual MCP sessions.
- Generic dispatch tables/APIs remain for legacy/debug compatibility, not as the
  normal work queue.
- Kitty layout/signaling helpers remain terminal-local utilities, not a
  Codex/Claude dispatch injection path.

Future local desktop/agent companion work should be designed as an explicit
adapter boundary that consumes Den-owned attention/run/task context. It should
not revive dispatch prompt injection as the default conductor workflow.
