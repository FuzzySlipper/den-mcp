from __future__ import annotations

import http.server
import json
import os
import pathlib
import socketserver
import stat
import subprocess
import tempfile
import textwrap
import threading
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
RELAY_SCRIPT_PATH = REPO_ROOT / "bin" / "den-telegram-relay"


class ThreadedTcpServer(socketserver.ThreadingMixIn, socketserver.TCPServer):
    allow_reuse_address = True


def make_executable(path: pathlib.Path, content: str) -> None:
    path.write_text(content, encoding="utf-8")
    path.chmod(path.stat().st_mode | stat.S_IEXEC)


class FakeBridgeServer:
    def __init__(self):
        self.wake_requests: list[int] = []
        self.server: ThreadedTcpServer | None = None
        self.thread: threading.Thread | None = None

    @property
    def port(self) -> int:
        assert self.server is not None
        return self.server.server_address[1]

    def start(self) -> None:
        outer = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):  # noqa: N802
                if self.path != "/wake":
                    self.send_error(404)
                    return
                length = int(self.headers.get("Content-Length", "0"))
                payload = json.loads(self.rfile.read(length).decode("utf-8"))
                outer.wake_requests.append(payload["dispatch_id"])
                body = json.dumps({"status": "queued", "dispatch_id": payload["dispatch_id"]}).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *_args):
                return

        self.server = ThreadedTcpServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def stop(self) -> None:
        if self.server is not None:
            self.server.shutdown()
            self.server.server_close()
            self.server = None
        if self.thread is not None:
            self.thread.join(timeout=2)
            self.thread = None


class FakeTelegramApiServer:
    def __init__(self):
        self.webhook_url = ""
        self.updates: list[dict] = []
        self.sent_messages: list[dict] = []
        self.delete_webhook_calls: list[dict] = []
        self.get_updates_calls: list[dict] = []
        self.server: ThreadedTcpServer | None = None
        self.thread: threading.Thread | None = None

    @property
    def base_url(self) -> str:
        assert self.server is not None
        return f"http://127.0.0.1:{self.server.server_address[1]}"

    def start(self) -> None:
        outer = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):  # noqa: N802
                self._handle_request()

            def do_POST(self):  # noqa: N802
                self._handle_request()

            def _handle_request(self):
                length = int(self.headers.get("Content-Length", "0"))
                payload = json.loads(self.rfile.read(length).decode("utf-8") or "{}")
                _, method = self.path.rsplit("/", 1)
                if method == "getWebhookInfo":
                    return self._json({"ok": True, "result": {"url": outer.webhook_url}})
                if method == "deleteWebhook":
                    outer.delete_webhook_calls.append(payload)
                    outer.webhook_url = ""
                    return self._json({"ok": True, "result": True})
                if method == "getUpdates":
                    outer.get_updates_calls.append(payload)
                    offset = payload.get("offset", 0)
                    result = [update for update in outer.updates if update["update_id"] >= offset]
                    return self._json({"ok": True, "result": result})
                if method == "sendMessage":
                    outer.sent_messages.append(payload)
                    return self._json({"ok": True, "result": {"message_id": len(outer.sent_messages)}})
                self.send_error(404)

            def _json(self, payload):
                body = json.dumps(payload).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *_args):
                return

        self.server = ThreadedTcpServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def stop(self) -> None:
        if self.server is not None:
            self.server.shutdown()
            self.server.server_close()
            self.server = None
        if self.thread is not None:
            self.thread.join(timeout=2)
            self.thread = None


class DenTelegramRelayTests(unittest.TestCase):
    maxDiff = None

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)

        self.root = pathlib.Path(self.temp_dir.name)
        self.bridge_state_root = self.root / "bridge-state"
        self.relay_state_file = self.root / "relay-state" / "offset.json"
        self.fake_bin = self.root / "bin"
        self.fake_bin.mkdir()

        self.bridge = FakeBridgeServer()
        self.bridge.start()
        self.addCleanup(self.bridge.stop)

        self.telegram = FakeTelegramApiServer()
        self.telegram.start()
        self.addCleanup(self.telegram.stop)

        state_dir = self.bridge_state_root / "sample"
        state_dir.mkdir(parents=True)
        (state_dir / "state.json").write_text(
            json.dumps(
                {
                    "project_id": "sample",
                    "status": "idle",
                    "thread_id": "thread-1",
                    "wake_port": self.bridge.port,
                    "pending_dispatch_ids": [],
                    "current_dispatch_id": None,
                    "last_error": None,
                }
            ),
            encoding="utf-8",
        )

    def run_relay(self, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            ["python3", str(RELAY_SCRIPT_PATH), *args],
            cwd=self.root,
            text=True,
            capture_output=True,
            check=False,
        )

    def test_poll_handles_status_and_wake_commands(self) -> None:
        self.telegram.webhook_url = "https://example.test/webhook"
        self.telegram.updates = [
            {
                "update_id": 100,
                "message": {
                    "chat": {"id": 123},
                    "text": "/status sample",
                },
            },
            {
                "update_id": 101,
                "message": {
                    "chat": {"id": 123},
                    "text": "/wake 41",
                },
            },
            {
                "update_id": 102,
                "message": {
                    "chat": {"id": 999},
                    "text": "/wake 999",
                },
            },
        ]

        result = self.run_relay(
            "poll",
            "--bot-token",
            "test-token",
            "--telegram-api-base",
            self.telegram.base_url,
            "--bridge-state-root",
            str(self.bridge_state_root),
            "--relay-state-file",
            str(self.relay_state_file),
            "--allowed-chat-id",
            "123",
            "--project",
            "sample",
            "--clear-webhook",
            "--once",
        )

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([{"drop_pending_updates": False}], self.telegram.delete_webhook_calls)
        self.assertEqual([41], self.bridge.wake_requests)
        self.assertEqual(2, len(self.telegram.sent_messages))
        self.assertIn("Project: sample", self.telegram.sent_messages[0]["text"])
        self.assertEqual("Queued dispatch #41 for sample.", self.telegram.sent_messages[1]["text"])
        offset_state = json.loads(self.relay_state_file.read_text(encoding="utf-8"))
        self.assertEqual(103, offset_state["offset"])

    def test_poll_rejects_active_webhook_without_clear_flag(self) -> None:
        self.telegram.webhook_url = "https://example.test/webhook"

        result = self.run_relay(
            "poll",
            "--bot-token",
            "test-token",
            "--telegram-api-base",
            self.telegram.base_url,
            "--bridge-state-root",
            str(self.bridge_state_root),
            "--relay-state-file",
            str(self.relay_state_file),
            "--allowed-chat-id",
            "123",
            "--project",
            "sample",
            "--once",
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("getUpdates will not work", result.stderr)
        self.assertEqual([], self.telegram.delete_webhook_calls)


if __name__ == "__main__":
    unittest.main()
