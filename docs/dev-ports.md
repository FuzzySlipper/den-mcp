# Dev Ports

Reserve `10000-15000` for den-managed development and internal helper services on the homelab box.

Guidelines:
- Prefer explicit fixed assignments over auto-incrementing from the last used port.
- Keep loopback-only helper services in this range too so they do not collide with common defaults like `8080`.
- Treat framework-default ports as exceptions only when a tool truly requires them.

Current assignments:
- `12081` — `signal-cli-den` JSON-RPC and SSE endpoint on `127.0.0.1`
