# Agent CLI Integration Note

Date: 2026-04-11
Task: `#570`

This note verifies what the currently installed `claude` and `codex` CLIs support for startup prompts, session resume, automation, and integration hooks before `den-agent` hard-codes prompt delivery behavior.

## Versions checked locally

- `claude` `2.1.101`
- `codex` `0.120.0`

## Reliable capability matrix

| Capability | Claude Code | Codex CLI | Recommendation |
|---|---|---|---|
| Start interactive session with initial prompt | Yes. Local help shows `claude [options] [command] [prompt]`. | Yes. Local help shows `codex [OPTIONS] [PROMPT]`. | Treat this as the primary MVP path for dispatch prompt delivery. |
| Resume prior interactive session | Yes. `--continue`, `--resume`, `--from-pr`, `--fork-session`. Anthropic docs confirm resume workflows. | Yes. `codex resume` and `codex fork`; `codex exec resume` for non-interactive continuation. | Useful for manual workflows, but not a safe basis for automatic prompt injection into a live session. |
| Non-interactive execution with prompt input | Yes. `claude --print` supports text, JSON, and `stream-json` output. | Yes. `codex exec` accepts a prompt or stdin and supports JSONL plus `--output-last-message` and `--output-schema`. | Keep available for later one-shot automation, not the default interactive wrapper path. |
| Startup/resume hook can add context | Yes. `SessionStart` hooks run on `startup` and `resume`; stdout or `additionalContext` is injected into context. | Yes. `SessionStart` hooks run on `startup` and `resume`; stdout or `additionalContext` is injected into context. | Real integration seam if we own per-user config, but not something `den-agent` should silently assume exists. |
| Hook on submitted user prompt can add context | Yes. `UserPromptSubmit` can add context or block. | Yes. `UserPromptSubmit` can add context or block. | Useful for future policy/context augmentation, not required for dispatch MVP. |
| Native notification hook for permission/idle attention | Yes. `Notification` hook supports permission and idle notification types. | No equivalent hook found in local help or current Codex hook docs. | Den/kitty/Signal should remain the cross-vendor notification path. |
| Broad tool interception hooks | Partial. Hooks exist, including tool lifecycle events, but they are not a clean external prompt-delivery channel. | Partial. Current Codex docs say `PreToolUse`/`PostToolUse` only emit for `Bash` today. | Do not build dispatch delivery around tool-hook interception. |
| Documented, supported external prompt injection into an already-running interactive session | Not found. | Not found. | Avoid synthetic keypress or TTY injection as an MVP requirement. |

## What this means for `den-agent`

### Safe MVP

1. Query Den for approved dispatches before launching the vendor CLI.
2. If a dispatch exists, pass the generated prompt as the CLI's native initial prompt when starting a fresh session.
3. If no dispatch exists, launch the CLI normally.
4. If the user is resuming or attaching to an already-running interactive session, do not assume prompt injection is supported.
5. In the resume/live-session case, fall back to:
   - focus the target terminal window
   - show the dispatch summary
   - copy or print the full prompt for explicit user paste/confirmation

For the current trusted local kitty workflow, a follow-on improvement is acceptable on top of that fallback:

- when `den-agent` is already running inside a kitty window and a new approved dispatch arrives mid-session, use kitty remote control to focus the agent window and paste a tiny Den wake-up like `den <dispatch_id>` into the live session input buffer
- have the agent read the machine-oriented handoff context from Den via MCP instead of trusting injected prose
- keep the full generated prompt available as clipboard/manual fallback instead of making skill-aware delivery a hard requirement

### Avoid for MVP

- writing characters into a running TTY as the primary mechanism
- assuming resume commands are equivalent to "send a new prompt into the existing session"
- assuming hooks are present in user config
- assuming Codex has a Claude-style native notification hook

## Task updates implied by this research

### `#564 den-agent wrapper`

The wrapper should explicitly treat prompt delivery as:

- first choice: native startup prompt on fresh session launch
- safe fallback: show/copy/focus for resumed or already-running sessions
- optional future enhancement: adapter-specific resume behavior after real-world validation

It should not require brittle prompt injection to count as complete.

### `#563 dispatch approval kitten`

`handle_result()` should be framed as:

- always: show details and focus the target window
- usually: copy or print the generated prompt
- optionally: attempt vendor-specific prompt delivery only when a supported path is confirmed

That keeps the kitten useful even when automatic delivery is unavailable.

## Evidence

### Local CLI help

- `claude --help`
- `codex --help`
- `codex exec --help`
- `codex resume --help`

### Official docs

- Anthropic Claude Code common workflows:
  - resume workflows: https://code.claude.com/docs/en/common-workflows
  - print / JSON / stream-json output modes: https://code.claude.com/docs/en/common-workflows
- Anthropic Claude Code hooks:
  - `SessionStart`, `UserPromptSubmit`, `Notification`: https://code.claude.com/docs/en/hooks
- OpenAI Codex CLI docs:
  - command line options: https://developers.openai.com/codex/cli/reference
  - non-interactive mode: https://developers.openai.com/codex/noninteractive
  - hooks: https://developers.openai.com/codex/hooks

## Bottom line

The clean cross-vendor abstraction is not "inject prompt into whatever session happens to exist." The clean abstraction is "deliver dispatch context before the session starts, and gracefully fall back to show/copy/focus when the session already exists," with kitty-mediated wake-up plus Den-hosted structured context as an optional local enhancement when the wrapper is already running inside a trusted kitty session.
