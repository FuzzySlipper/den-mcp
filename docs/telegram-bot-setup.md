# Telegram Bot Setup For Codex Bridge

Date: 2026-04-22

This runbook covers the first real-world setup path for using Telegram as the
wake surface for a per-project Codex bridge.

The current design is:

- Telegram is a wake and operator surface.
- Den remains the source of truth for dispatch context.
- `den-codex-bridge` owns the live Codex app-server session per project.
- `den-telegram-relay` talks to the Telegram Bot API and forwards small wake
  requests into the local bridge.

## Prerequisites

- A running Den server.
- A project with a working `den-codex-bridge`.
- A Telegram bot token created with `@BotFather`.
- The local machine can reach `https://api.telegram.org`.

## 1. Create The Bot

Create the bot manually in Telegram with `@BotFather`.

Recommended minimum steps:

1. Start a chat with `@BotFather`.
2. Run `/newbot`.
3. Choose a display name and username.
4. Save the returned bot token securely.

Telegram’s bot overview and Bot API reference:

- https://core.telegram.org/bots
- https://core.telegram.org/bots/api

## 2. Register Bot Commands And Profile Text

After you have a token, use the relay’s `register` command to configure the bot
through the Bot API.

Example for a project-specific bridge:

```bash
bin/den-telegram-relay register \
  --bot-token "$DEN_TELEGRAM_BOT_TOKEN" \
  --project den-mcp
```

This currently configures:

- command menu via `setMyCommands`
- bot description via `setMyDescription`
- bot short description via `setMyShortDescription`

If the bot was previously used with webhooks and you want to move to long
polling, add `--clear-webhook`.

```bash
bin/den-telegram-relay register \
  --bot-token "$DEN_TELEGRAM_BOT_TOKEN" \
  --project den-mcp \
  --clear-webhook
```

## 3. Discover The Chat ID To Allowlist

The poller intentionally requires explicit `--allowed-chat-id` values. For a
first-time setup, use `discover-chat` before starting the locked-down relay.

1. Open a chat with your bot in Telegram.
2. Send it any message, for example `/start`.
3. Run:

```bash
bin/den-telegram-relay discover-chat \
  --bot-token "$DEN_TELEGRAM_BOT_TOKEN" \
  --clear-webhook \
  --once
```

The relay will print the discovered chat id and basic chat metadata. Use that
chat id in the real polling command.

## 4. Start The Project Bridge

Start the project-scoped Codex bridge:

```bash
bin/den-codex-bridge serve \
  --project den-mcp \
  --base-url http://127.0.0.1:5199
```

If the project root is not discoverable from Den yet, pass `--project-root`.

## 5. Start The Telegram Relay

Run the long-polling relay with the allowlisted chat id:

```bash
bin/den-telegram-relay poll \
  --bot-token "$DEN_TELEGRAM_BOT_TOKEN" \
  --project den-mcp \
  --allowed-chat-id 123456789 \
  --clear-webhook
```

If you want to preserve pending webhook updates when switching away from
webhooks, omit `--drop-pending-updates`. If you want a clean handoff, add it.

## 6. Use The Bot

The relay currently supports:

- `/help`
- `/status`
- `/wake <dispatch_id>`
- `/chatid`

If no default project is configured, use:

- `/status <project>`
- `/wake <project> <dispatch_id>`

## Suggested First Live Test

1. Start `den-codex-bridge serve` for one project.
2. Start `den-telegram-relay poll` with a single private-chat allowlist id.
3. Send `/status` to confirm bridge visibility.
4. Approve or create one dispatch in Den.
5. Send `/wake <dispatch_id>` from the allowlisted chat.
6. Confirm the bridge queues the dispatch and only drains it when Codex is
   idle.

## Safety Notes

- Do not share the bot token. Anyone with it can control the bot.
- Keep the allowlist narrow, especially during first live tests.
- Long polling and webhooks should not be active at the same time for this
  setup. Use `--clear-webhook` when switching to local polling.
- Telegram is not the source of truth for prompts. The bot should only wake the
  bridge with a small dispatch reference.

## Relevant Bot API Methods

The current relay bootstrap flow relies on these official Bot API methods:

- `getMe`
- `getWebhookInfo`
- `deleteWebhook`
- `getUpdates`
- `setMyCommands`
- `setMyDescription`
- `setMyShortDescription`

Reference:

- https://core.telegram.org/bots/api
