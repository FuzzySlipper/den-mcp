# Legacy Mobile Bridge Integrations

Status: retired from the supported Den runtime as of task `#811`.

Den previously carried Signal and Telegram bridge paths for mobile wake-up,
dispatch approval, and operator notification experiments. Those paths depended
on dispatch-first workflow assumptions that are no longer canonical.

The supported runtime is now:

- Den web/operator views
- Pi/conductor and Pi sub-agent runs
- task-thread messages and review workflow records
- agent-stream ops/control events
- first-class AgentRun state
- generic notification abstractions that can be reused by future web/desktop
  attention surfaces

Removed from the active repo surface:

- Signal default server integration, `DenMcp__Signal__*` configuration, managed
  `signal-cli` startup, Signal reaction listener, and Signal-specific tests.
- Signal deployment units and maintenance/smoke scripts.
- Telegram relay executable, tests, and setup runbook.

Historical notes may still mention Signal or Telegram as past bridge examples.
Do not treat those mentions as supported setup instructions unless a future task
explicitly reintroduces a maintained adapter under a new design.
