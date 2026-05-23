# Hermes-launched Den Pi Worker Smoke Tests — Task #1247

> **Quarantine notice (Task #1552):** The Pi-specific path tools `start_coder_worker_path` and `start_reviewer_worker_path` are quarantined with `legacy_` prefixes and marked LEGACY / ADMIN ONLY. Modern workflows use `register_worker_run`, standard packet tools, and Den task/review APIs.

This document records the staged smoke coverage added for the Hermes → Den MCP → Den Pi worker migration.

## Automated smoke coverage

Primary automated smoke test:

```bash
dotnet test tests/DenMcp.Server.Tests/DenMcp.Server.Tests.csproj \
  --filter "FullyQualifiedName~StagedSmoke_CoderReviewerFullLoop_UsesDenStateAndBoundedReferences"
```

In this temporary clone, `dotnet build tests/DenMcp.Server.Tests/DenMcp.Server.Tests.csproj --no-restore` is the available validation gate because the runtime environment is missing `Microsoft.AspNetCore.App` 10.0.0 for test execution.

## Stages covered

The stage descriptions below are historical smoke coverage notes from the Pi migration. Tool names named here now resolve as `legacy_*` MCP tools and are admin-only, not normal Runner workflow guidance.

1. **Coder-only smoke**
   - Historically started through `start_coder_worker_path` (now `legacy_start_coder_worker_path`).
   - Prepare a Den `coder_context_packet`.
   - Launch a coder worker through Den worker state.
   - Verify tmux/container/status handles are discoverable from Den worker status.
   - Post an `implementation_packet` using `post_worker_completion_packet`.
   - Verify `verify_coder_worker_completion` returns `ready_for_review` only after branch/head/tests metadata exists.

2. **Reviewer smoke**
   - Historically started through `start_reviewer_worker_path` (now `legacy_start_reviewer_worker_path`).
   - Prepare a Den `reviewer_context_packet`.
   - Launch a reviewer worker through Den worker state.
   - Post a `review_findings_packet` tied to review round metadata.
   - Verify `verify_reviewer_worker_completion` returns `review_recorded` only after review-round, branch, and head metadata exist.

3. **Full-loop smoke**
   - Confirm both coder and reviewer workers are complete in Den worker state.
   - Compute the orchestrator next-action decision from Den verifier results, not process exit.
   - Confirm packet references are bounded and the long task prompt body is not present in launch command display/process-arg proxy.
   - Confirm tmux/container metadata remains discoverable through Den status payloads.

## Diagnostics expected on failure

The smoke path is designed to produce actionable failures:

- Missing completion packet → `completion_state=missing_packet`, verdict `incomplete`.
- Malformed completion packet → `completion_state=malformed`, failure category `malformed_packet`.
- Missing branch/head/tests metadata → verifier verdict `incomplete` with individual failed checks.
- Worker launch handles missing → assertion failure on Den worker `session.tmux_session` or `session.container_name` fields.
- Prompt/reference regression → assertion failure if long task description appears in launch prompt ref or launch command display.

## Cleanup/resource note

The automated test uses the fake Pi session host, so no real containers or tmux sessions are created. In live smoke, cleanup should be verified through Den `cleanup`/session status APIs once those are wired to the live Docker/tmux adapter; tmux remains observability/break-glass only, not the automation API.
