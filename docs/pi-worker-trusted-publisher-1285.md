# Pi Worker Trusted Publisher (Task 1285)

## Status

ADR / implementation note for the v1 trusted publisher MVP.

## Problem

Pi workers run in isolated, worker-controlled workspaces that must not receive long-lived Git publish credentials. Publishing worker output must therefore happen from trusted server-side state after Den review and workflow checks pass, not by asking the worker sandbox to push.

## v1 Policy

### Mode A: publish worker task branch

- The worker workspace is used only as an untrusted source for validation:
  - resolve the durable Pi run/session from Den;
  - require the durable session state to be `completed`;
  - require a matching structured completion packet with `status=completed`;
  - verify task/role/branch/head metadata;
  - verify the workspace `HEAD` equals the expected head;
  - compute changed-file scope from an explicit/reviewed base and fail closed if the diff cannot be computed.
- Non-validate publishes run `git push` only from the trusted canonical project root.
- If the expected worker commit object is not already available in the canonical project root, the publisher may fetch that specific object into the project root using controlled Git argv before pushing.
- The worker workspace never receives publisher credentials and never runs `git push` in Mode A.

### Mode B: publish reviewed branch / fast-forward target

- Only configured trusted orchestrator identities may request Mode B.
- Only configured target/base branches may be fast-forwarded.
- The remote name is policy-controlled: callers may omit it or supply the configured canonical remote name only. Arbitrary remote names and unsafe tokens are rejected.
- Before `fast_forward_main`, the publisher fetches the canonical remote, resolves the current remote base, verifies the remote base matches or descends from the reviewed base as appropriate, and verifies the reviewed head descends from the current remote base. Mismatches are rejected before push.

## Credential and storage rotation story

- v1 uses credentials available only to the trusted Den server / canonical repository environment (for example deploy-key or GitHub App credentials mounted outside Pi worker state).
- Credentials must not be copied into Pi workspaces, Pi state dirs, completion packets, task messages, or audit messages.
- Audit records redact remote URLs and record validation decisions/diagnostics, not secrets.
- Rotation is an operator action against the trusted server credential source. Since workers do not store publish credentials, worker session cleanup does not participate in credential rotation.
- If a credential is suspected exposed, rotate the trusted server credential, invalidate any old key/app installation token, and audit recent `trusted_publisher_audit` messages for affected project/branch operations.

## v2 Candidates

- Replace host-level Git credentials with short-lived GitHub App installation tokens scoped per project/repo and operation.
- Add explicit per-project publisher policy documents for allowed remotes, branches, and path scopes.
- Store signed provenance for fetched worker commits and validated completion packets.
- Add optional server-side mirror/quarantine repositories so fetching from worker workspaces never touches the canonical working copy before validation.
