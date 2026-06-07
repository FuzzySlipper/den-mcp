# Orchestrator retry-cap mirror policy

`den-mcp` is a legacy/thin MCP mirror for Den orchestration behavior. Canonical live behavior belongs in `den-core`, but this mirror still exposes `determine_orchestrator_next_action` in some compatibility paths.

As of den-core #2078, the mirrored default matches Core:

- default `max_attempts = 4`
- explicit per-call `max_attempts` overrides remain honored
- the cap is per role / per gate, not a total task-attempt limit

Do not spend ordinary retry budget on infrastructure failures such as no worker claim, auth expiry, missing Channels membership, route 404, undeployed live-service state, provider/config drift, or synthetic registration assignments superseded by concrete pool assignments. Those should block, split, or route to the owning service/operator instead of being treated as normal worker-loop retry pressure.
