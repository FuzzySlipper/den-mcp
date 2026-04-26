# Dev Ports

Reserve `10000-15000` for den-managed development and internal helper services on the homelab box.

Guidelines:
- Prefer explicit fixed assignments over auto-incrementing from the last used port.
- Keep loopback-only helper services in this range too so they do not collide with common defaults like `8080`.
- Treat framework-default ports as exceptions only when a tool truly requires them.

Current assignments:
- none currently reserved for supported Den runtime services in this range.

Retired assignments:
- `12081` — legacy `signal-cli-den` JSON-RPC/SSE endpoint, removed from the supported runtime in task `#811`.
