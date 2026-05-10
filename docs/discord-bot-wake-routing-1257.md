# Discord Bot-to-Bot Wake Routing Investigation — Task #1257

Date: 2026-05-10
Project: `den-mcp`
Related Hermes fix branch: `/home/agents/hermes-agent` branch `task/1257-discord-allow-bots-config`, commit `302972470`

## Summary

Patch's "monkey in the middle" observation was real: Planner and Runner config files both contained the intended multi-agent Discord settings, but the running gateway processes did **not** have `DISCORD_ALLOW_BOTS=mentions` in their process environment.

Root cause found in Hermes Agent, not Discord itself:

- `discord.allow_bots: mentions` existed in both profile config files.
- `gateway/platforms/discord.py` gates bot-authored messages using `os.getenv("DISCORD_ALLOW_BOTS", "none")`.
- `gateway/config.py` bridged many `discord.*` config keys into `DISCORD_*` env vars (`require_mention`, `free_response_channels`, `auto_thread`, `allowed_channels`, etc.) but **omitted `discord.allow_bots`**.
- Therefore, despite correct YAML, gateway startup left `DISCORD_ALLOW_BOTS` unset and bot-authored messages defaulted to `none`, so direct bot-to-bot mentions were ignored before normal mention routing.

The human relay worked because human-authored messages do not hit the bot-author filter; with a direct mention in the human reply, Planner/Runner proceeded through the normal `require_mention` check and received reply context.

## Evidence

### Profile config was already correct

Both profiles had this Discord section:

```yaml
discord:
  require_mention: true
  free_response_channels: ''
  allowed_channels: ''
  auto_thread: false
  reactions: true
  channel_prompts: {}
  dm_role_auth_guild: ''
  server_actions: ''
  allow_bots: mentions
```

Files checked:

- `/home/agents/profiles/den-mcp-runner/config.yaml`
- `/home/agents/profiles/den-mcp-planner/config.yaml`

### Running gateway env showed the bug

Processes:

- Planner: `/home/agents/hermes-agent/venv/bin/python -m hermes_cli.main --profile den-mcp-planner gateway run --replace`
- Runner: `/home/agents/hermes-agent/venv/bin/python -m hermes_cli.main --profile den-mcp-runner gateway run --replace`

Their `/proc/<pid>/environ` contained `HERMES_HOME=...` but did **not** contain:

- `DISCORD_ALLOW_BOTS`
- `DISCORD_REQUIRE_MENTION`
- `DISCORD_AUTO_THREAD`
- `DISCORD_ALLOWED_CHANNELS`
- `DISCORD_FREE_RESPONSE_CHANNELS`

This initially looked odd for all bridged settings, but the key failure was confirmed in source: `allow_bots` was not bridged at all, while the Discord adapter depended on the env var.

### Source code behavior

Relevant gate in `/home/agents/hermes-agent/gateway/platforms/discord.py`:

```python
if getattr(message.author, "bot", False):
    allow_bots = os.getenv("DISCORD_ALLOW_BOTS", "none").lower().strip()
    if allow_bots == "none":
        return
    elif allow_bots == "mentions":
        if not self._client.user or self._client.user not in message.mentions:
            return
```

Before the fix, `/home/agents/hermes-agent/gateway/config.py` had Discord env bridging for `require_mention`, `free_response_channels`, `auto_thread`, `reactions`, `ignored_channels`, `allowed_channels`, `no_thread_channels`, and mention controls, but not `allow_bots`.

## Fix applied in Hermes checkout

Hermes checkout: `/home/agents/hermes-agent`

Branch: `task/1257-discord-allow-bots-config`
Commit: `302972470` — `fix: bridge Discord allow_bots config`

Changes:

1. `gateway/config.py`
   - Bridges `discord.allow_bots` to `DISCORD_ALLOW_BOTS` when the env var is not already set.
   - Also includes `allow_bots` in bridged platform `extra` for Discord/Slack/Feishu consistency.

2. `gateway/platforms/discord.py`
   - Adds debug logging when bot-authored Discord messages are ignored because:
     - `DISCORD_ALLOW_BOTS=none`, or
     - `DISCORD_ALLOW_BOTS=mentions` but this bot was not mentioned.

3. `tests/gateway/test_discord_bot_auth_bypass.py`
   - Adds regression coverage that `discord.allow_bots: mentions` in `config.yaml` produces `DISCORD_ALLOW_BOTS=mentions`.
   - Verifies an explicit env var still takes precedence.

Validation:

```bash
python -m pytest tests/gateway/test_discord_bot_auth_bypass.py -q
# 11 passed in 1.79s
```

Post-fix config-load smoke for both profiles produced:

```text
den-mcp-runner  DISCORD_ALLOW_BOTS=mentions DISCORD_REQUIRE_MENTION=true DISCORD_AUTO_THREAD=false DISCORD_ALLOWED_CHANNELS='' DISCORD_FREE_RESPONSE_CHANNELS=''
den-mcp-planner DISCORD_ALLOW_BOTS=mentions DISCORD_REQUIRE_MENTION=true DISCORD_AUTO_THREAD=false DISCORD_ALLOWED_CHANNELS='' DISCORD_FREE_RESPONSE_CHANNELS=''
```

## Runtime caveat

The running gateway processes still need a restart to pick up this Hermes source/config fix. The active Runner process is this Discord session, so I did **not** restart it mid-turn and risk killing the report before it landed.

Recommended restart after this packet is visible:

```bash
sudo -u agent env \
  XDG_RUNTIME_DIR=/run/user/$(id -u agent) \
  DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u agent)/bus \
  systemctl --user restart hermes-gateway@den-mcp-planner hermes-gateway@den-mcp-runner
```

Or restart via whatever gateway service wrapper is currently being used for these profiles.

## Answers to task questions

### Does the Discord adapter ignore bot-authored messages unless `allow_bots` is set?

Yes. Bot-authored messages hit an early gate before the human allowlist and before `_handle_message`. Default is effectively `DISCORD_ALLOW_BOTS=none`.

### Were Planner and Runner running with the intended config?

Their YAML config files were correct, but runtime behavior was not, because `discord.allow_bots` was not translated into the env var the adapter reads. After the Hermes patch and gateway restart, they should be.

### How do `require_mention`, `free_response_channels`, and `allowed_channels` interact?

For non-DM Discord messages:

1. `allowed_channels` is an allowlist if non-empty. Empty means no channel allowlist.
2. `ignored_channels` rejects even if mentioned.
3. `free_response_channels` bypasses mention requirement for listed channels/parent channels.
4. With `require_mention: true` and not free-response / participating thread, the bot must be directly mentioned.
5. Bot-authored messages must pass the separate `allow_bots` gate first.

### Are bot mentions in replies parsed the same as human mentions?

The code uses `message.mentions` for both, so it should work the same once bot-authored messages pass `allow_bots`. Discord reply context can still differ, but the observed failure was earlier than reply-context handling.

### Does Discord suppress bot-to-bot mentions?

No evidence of a hard Discord limitation here. The observed behavior is explained by Hermes runtime config bridging.

### Can Hermes reduce duplicate pings in reply handoffs?

Probably. Separate follow-up candidate: tune send/reply behavior with `AllowedMentions(replied_user=False)` or a per-message option for agent-to-agent handoffs. Current safe defaults allow user and replied-user pings, so replying can re-ping the original sender.

## Recommendation

Short term:

- Restart Planner/Runner gateways after the Hermes fix.
- Keep `discord.require_mention: true`, `discord.allow_bots: mentions`, `discord.auto_thread: false`.
- Do **not** use broad `free_response_channels` in `#den-mcp` yet; it would make shared coordination too noisy.

Medium term:

- Prefer Den agent-stream/dispatch for durable agent-to-agent wake routing, with Discord as the human-visible mirror.
- Keep Discord bot-to-bot mentions as convenience/diagnostic wakeups, not the sole coordination primitive.

Long term:

- If Discord semantics remain squishy, a Den-native or small IRC-ish internal transport with explicit routing rules would fit this system better than pretending Discord is deterministic agent middleware.
