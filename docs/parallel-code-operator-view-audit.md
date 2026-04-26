# Parallel Code Operator View Audit

Task: #818 — redesign Den web around a parallel-code-style workspace operator view.

Reference inspected locally: `/tmp/den-818-parallel-code-reference` (`https://github.com/johannesjo/parallel-code`). Files/screens inspected include `README.md`, `screens/agent-working.png`, `screens/workflow.png`, `src/App.tsx`, `src/components/TaskPanel.tsx`, `TaskTitleBar.tsx`, `TaskBranchInfoBar.tsx`, `TaskChangedFilesSection.tsx`, `TilingLayout.tsx`, and `Sidebar.tsx`.

## What Parallel Code gets right for Den

- **One work item is the center of gravity.** A task panel contains status, project/branch/worktree identity, notes, changed files, terminal/live agent output, and prompt controls. Operators do not have to cross-reference a raw feed to understand one piece of work.
- **Dense task strip instead of a marketing dashboard.** The sidebar/task columns expose many simultaneous pieces of work with status dots, compact labels, and keyboard-friendly navigation.
- **Run activity sits next to artifacts/diff.** Changed files and terminal/agent output are in the same work panel, so the operator can correlate “what the agent did” with “what changed.”
- **Controls are colocated with state.** Merge, push, close, prompt, and terminal controls live in the task panel title/body rather than a separate administration page.
- **Raw terminal/log remains available but is not the only abstraction.** The UI has structured changed-file and task/status chrome around the terminal.
- **Desktop shell is useful when runtime control matters.** Parallel Code uses Electron for terminals, worktree/file-system access, window shortcuts, and app-level persistence. Den can remain web-first for this slice, but a local app shell is a reasonable future path if browser sandbox limits become painful.

## What Den should avoid

- **Do not adopt Parallel Code's workflow wholesale.** Den's durable records are tasks, review rounds/findings, messages, agent-stream ops, and AgentRun records; not every work item must start as an isolated worktree.
- **Do not make Den depend on Parallel Code implementation.** Borrow information architecture and interaction patterns only.
- **Do not force branch/worktree data before it exists.** Den can initially show review diff metadata and run artifacts, then add changed-file/workspace projections later.
- **Do not replace Den task/review history with terminal transcripts.** Raw child Pi transcripts remain artifacts; the operator surface should show bounded grouped activity and link to raw logs only as fallback.

## Den information architecture proposal

### Work item / workspace panel

The task detail view should act as the first Den workspace surface. It should answer:

- **What is running?** Current/recent AgentRun state, role/model, duration, and controls.
- **What changed?** Current review branch/base/head metadata now; changed-file/diff projection later.
- **What needs review?** Current review round, verdict, open findings, and pending review state.
- **What is stuck?** Failed/timeout/aborted runs, blocked task status, unresolved findings, failed reviewer runs.
- **What can I do next?** A compact suggested next operator action derived from task/run/review state.

### Run detail panel

The run detail view should stop being a flat event feed. It should show:

- header activity summary: current/last tool, tool count, assistant message count, error count
- grouped work cards:
  - assistant commentary separate from tool effects
  - tool request/start/update/end grouped by `tool_call_id`
  - args/result previews bounded for scanning
  - error and broad/off-scope command indicators
  - raw event expansion for debugging
- lifecycle and artifacts below the structured work view

### Global dashboard / attention queue

A later attention projection (#807) should use the workspace shape as the target. The top-level dashboard can list attention items, but each item should deep-link into a task/workspace panel rather than becoming a competing queue.

## Web vs local app shell

The current Den web app can implement the near-term operator surface using existing APIs (`TaskDetail`, `SubagentRunDetail`, `work_events`, AgentRun summaries, review workflow). If future slices need direct terminal ownership, file watching, editor/file-manager integration, or safer local process controls, a local app shell (Tauri/Electron) is acceptable because Den is intended to live on this machine rather than be a distributed SaaS product. The immediate #818 slice should stay web-first and surface where the browser boundary becomes real.
