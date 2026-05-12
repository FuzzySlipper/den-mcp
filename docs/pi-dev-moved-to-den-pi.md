# pi-dev moved to den-pi

The active Pi-side Den extensions and helper libraries moved out of this repository.

Use the standalone repo instead:

```text
/home/dev/den-pi
```

Canonical locations now include:

- `/home/dev/den-pi/extensions/den.ts`
- `/home/dev/den-pi/extensions/den-subagent.ts`
- `/home/dev/den-pi/extensions/exit-alias.ts`
- `/home/dev/den-pi/extensions/pi-powerline-footer/`
- `/home/dev/den-pi/lib/`
- `/home/dev/den-pi/skills/den-orchestrator/SKILL.md`

`den-mcp` should not regain active Pi extension implementation code. Keep MCP tool schemas and adapter behavior here; keep reusable Pi runtime extension code in `den-pi`.
