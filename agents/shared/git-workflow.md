## Git Workflow

- Create a feature branch per task: `git checkout -b task/<task-id>-short-description`
- Commit work to that branch; never commit directly to `main`
- When marking a task for review, leave the branch as-is so the reviewing agent can diff `main...HEAD`
- If review finds issues, continue working on the same task branch and add follow-up commits there
- Merge to `main` only after review passes, and only if the branch still matches the reviewed head: `git checkout main && git merge task/<id>-... && git branch -d task/<id>-...`
