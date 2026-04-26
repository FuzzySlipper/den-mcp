# Kitty Agent State Protocol

Task: `#559`

This document defines the OSC 1337 user-variable contract between optional
Den-aware local terminal processes and Kitty-side integrations such as the
watcher, custom tab bar, and future layout helpers. Retired Codex/Claude dispatch
wrappers are not part of the supported runtime.

The protocol is intentionally small:

- local helpers publish state with escape sequences only
- Kitty-side code observes window-local user variables
- no server calls are required to emit signals

## Transport

Kitty exposes per-window user variables through OSC 1337:

```bash
printf '\033]1337;SetUserVar=%s=%s\007' "$key" "$encoded_value"
```

Values must be base64-encoded without trailing newlines.

The helper for local terminal integrations lives at [den-signal.sh](./den-signal.sh).

## Variables

| Variable | Values | Meaning | Typical update point |
|---|---|---|---|
| `den_agent` | agent identity such as `pi`, `claude-code`, `codex` | which agent owns the window | when a Den-aware local helper starts or retargets |
| `den_project` | Den project id such as `den-mcp`, `quillforge` | which project the window is currently working in | when a Den-aware local helper starts or retargets |
| `den_status` | `idle`, `working`, `reviewing`, `waiting`, `done`, `error` | current lifecycle state for the agent session | whenever the local helper's work state changes |
| `den_task` | task id as text, or empty string | active task in the window | when a task is claimed, changed, or cleared |
| `den_dispatch` | dispatch id as text, or empty string | legacy dispatch id, if a debug/legacy flow is explicitly active | legacy/debug dispatch flows only |

## Status semantics

`den_status` uses these normalized values:

- `idle`: window is open but not currently working a task
- `working`: active implementation or investigation work
- `reviewing`: active code review or rereview work
- `waiting`: blocked on user input, dependency, or external completion
- `done`: just completed a meaningful unit of work and may need attention
- `error`: helper or agent hit a terminal failure that needs attention

`done` is expected to be an edge state, not a permanent resting state. A common
pattern is:

1. set `den_status=done`
2. optionally clear `den_task` / legacy `den_dispatch`
3. after the completion is acknowledged or the next task is picked up, move to
   `idle`, `working`, or `reviewing`

## Lifecycle contract

Local helpers should publish the minimum stable context first, then update
dynamic fields as work changes.

### Startup

Set these as soon as the window is known to be Den-managed:

```bash
den_signal den_agent "pi"
den_signal den_project "den-mcp"
den_signal den_status "idle"
den_signal_clear den_task
den_signal_clear den_dispatch
```

### Active task started

```bash
den_signal den_task "559"
den_signal den_status "working"
```

If the task is specifically review work, use `reviewing` instead of `working`.
`den_dispatch` should only be set for explicit legacy/debug dispatch flows.

### Waiting on something external

```bash
den_signal den_status "waiting"
```

Do not clear task or legacy dispatch just because the agent is temporarily
waiting. Those fields describe ownership/context, not whether the terminal is
actively typing.

### Completion

```bash
den_signal den_status "done"
den_signal_clear den_dispatch
den_signal_clear den_task
```

After the completion state has been surfaced to the user, the local helper can
transition back to `idle` or directly to the next active state.

### Error

```bash
den_signal den_status "error"
```

Keep `den_task` and any legacy `den_dispatch` populated when possible so the
watcher can show where the failure happened.

## Watcher expectations

The Kitty watcher for `#560` should treat these variables as a latest-value
window snapshot, not as an append-only event log.

Watcher behavior should assume:

- user-variable updates can arrive independently and in any order
- a window is only considered Den-managed once both `den_agent` and
  `den_project` are known
- empty `den_task` and `den_dispatch` mean "no active association"
- status changes are the primary driver for tab decoration and notifications
- missing variables should not crash the watcher; partial state is normal during
  startup and teardown

## Local helper

Source the helper from bash or zsh terminal integrations:

```bash
source /path/to/den-mcp/kitty/den-signal.sh
```

Then call:

```bash
den_signal den_status "working"
den_signal den_task "559"
den_signal_clear den_dispatch
```

The helper intentionally no-ops when `KITTY_WINDOW_ID` is not set so local
terminal integrations can call it unconditionally outside Kitty.

When Kitty is active, the helper targets a terminal device instead of blindly
writing OSC 1337 bytes into captured stdout. In a normal interactive Kitty
session it writes to the terminal stream directly; if stdout is piped or
captured, it falls back to the controlling TTY (`/dev/tty`) when available so
user-variable updates stay terminal-only instead of leaking into pipelines.

## Manual testing

### Using the helper

Open a Kitty window and source the helper:

```bash
source ./kitty/den-signal.sh
```

Emit a full state sequence:

```bash
den_signal den_agent "pi"
den_signal den_project "den-mcp"
den_signal den_status "reviewing"
den_signal den_task "597"
den_signal den_status "done"
den_signal_clear den_dispatch
den_signal_clear den_task
den_signal den_status "idle"
```

### Using raw OSC 1337 directly

This is useful when debugging watcher behavior without the helper:

```bash
printf '\033]1337;SetUserVar=%s=%s\007' \
  "den_status" \
  "$(printf '%s' 'working' | base64 | tr -d '\r\n')"
```

### What to verify

For `#560` and later Kitty tasks, the manual verification loop should confirm:

- the watcher sees each variable update for the correct window
- tab state changes when `den_status` changes
- task and legacy dispatch context are retained while `waiting`
- completion notifications key off `done`, not just process exit
- outside Kitty, sourcing and calling the helper produces no errors and no
  escape output
