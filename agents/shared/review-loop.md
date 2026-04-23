## Review Loop

Task state plus task-thread messages are the source of truth. A status change alone is never a complete handoff.

When handing work to review:
1. Keep the task `in_progress` until there is a stable, reviewable diff.
2. When moving a task to `review`, also post a task-thread review packet or use structured review tooling.
3. A valid review packet must include:
   - branch under review
   - diff base or base branch
   - head commit
   - tests run
   - scope notes
   - open questions or known gaps
4. Changing a task to `review` without a review packet is incomplete.
5. If there is no stable reviewable head yet, stay `in_progress` and post a planning or handoff message instead.

Reviewer feedback should stay on the task thread and be concrete:
- Category or severity when known.
- File or behavior references.
- Whether the finding is blocking or follow-up.

Implementer follow-up should also stay on the task thread and include:
- What changed.
- New head commit if applicable.
- Tests run.
- Whether each finding is fixed, deferred, or split out.

Preferred sequence:
1. Implement on a task branch when practical.
2. Post the review packet.
3. Reviewer replies on the task thread with findings.
4. Implementer replies after fixes with updated head and tests.
5. Reviewer approves or requests more changes.
6. Merge only after reviewer approval, and only if the branch still matches the reviewed head.
7. After merge, triage every non-blocking finding by fixing it, splitting it into a follow-up task, or explicitly accepting it as backlog.
