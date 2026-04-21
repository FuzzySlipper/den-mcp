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
import time
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
BRIDGE_SCRIPT_PATH = REPO_ROOT / "bin" / "den-codex-bridge"


def make_executable(path: pathlib.Path, content: str) -> None:
    path.write_text(content, encoding="utf-8")
    path.chmod(path.stat().st_mode | stat.S_IEXEC)


class DenServer:
    def __init__(self, root_path: pathlib.Path):
        self.root_path = root_path
        self.dispatches: dict[int, dict] = {}
        self.completions: list[int] = []
        self.project_id = "sample"
        self._server: socketserver.TCPServer | None = None
        self._thread: threading.Thread | None = None

    @property
    def base_url(self) -> str:
        assert self._server is not None
        return f"http://127.0.0.1:{self._server.server_address[1]}"

    def start(self) -> None:
        outer = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):  # noqa: N802
                if self.path == "/api/projects":
                    return self._json(
                        [
                            {
                                "id": outer.project_id,
                                "name": "sample",
                                "root_path": str(outer.root_path),
                            }
                        ]
                    )
                if self.path.startswith("/api/dispatch?"):
                    return self._json([])
                if self.path.startswith("/api/dispatch/") and self.path.endswith("/context"):
                    dispatch_id = int(self.path.split("/")[3])
                    payload = outer.dispatches[dispatch_id]["context"]
                    return self._json(payload)
                if self.path.startswith("/api/dispatch/"):
                    dispatch_id = int(self.path.split("/")[3])
                    payload = outer.dispatches[dispatch_id]["dispatch"]
                    return self._json(payload)
                self.send_error(404)

            def do_POST(self):  # noqa: N802
                if self.path.startswith("/api/dispatch/") and self.path.endswith("/complete"):
                    dispatch_id = int(self.path.split("/")[3])
                    outer.completions.append(dispatch_id)
                    return self._json({"id": dispatch_id, "status": "completed"})
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

        self._server = socketserver.TCPServer(("127.0.0.1", 0), Handler)
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)
        self._thread.start()

    def stop(self) -> None:
        if self._server is not None:
            self._server.shutdown()
            self._server.server_close()
            self._server = None
        if self._thread is not None:
            self._thread.join(timeout=2)
            self._thread = None


class DenCodexBridgeTests(unittest.TestCase):
    maxDiff = None

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)

        self.root = pathlib.Path(self.temp_dir.name)
        self.fake_bin = self.root / "bin"
        self.fake_bin.mkdir()
        self.project_root = self.root / "workspace" / "sample"
        self.project_root.mkdir(parents=True)
        self.state_root = self.root / "state"

        self.codex_log = self.root / "codex-log.jsonl"
        self.den_agent_log = self.root / "den-agent-log.jsonl"

        make_executable(
            self.fake_bin / "codex",
            textwrap.dedent(
                """\
                #!/usr/bin/env python3
                import json
                import os
                import pathlib
                import sys
                import time

                log_path = pathlib.Path(os.environ["DEN_CODEX_BRIDGE_TEST_CODEX_LOG"])
                if sys.argv[1:] != ["app-server"]:
                    with log_path.open("a", encoding="utf-8") as handle:
                        handle.write(json.dumps({"argv": sys.argv[1:]}) + "\\n")
                    raise SystemExit(0)

                thread_id = "thread-1"
                turn_counter = 0
                turn_delay = float(os.environ.get("DEN_CODEX_BRIDGE_TEST_TURN_DELAY", "0.1"))

                for raw in sys.stdin:
                    message = json.loads(raw)
                    with log_path.open("a", encoding="utf-8") as handle:
                        handle.write(json.dumps(message) + "\\n")

                    method = message.get("method")
                    if method == "initialize":
                        print(json.dumps({"jsonrpc": "2.0", "id": message["id"], "result": {"serverInfo": {"name": "fake"}}}), flush=True)
                    elif method == "initialized":
                        continue
                    elif method == "thread/start":
                        print(json.dumps({"jsonrpc": "2.0", "id": message["id"], "result": {"thread": {"id": thread_id}}}), flush=True)
                        print(json.dumps({"jsonrpc": "2.0", "method": "thread/started", "params": {"thread": {"id": thread_id}}}), flush=True)
                    elif method == "turn/start":
                        turn_counter += 1
                        turn_id = f"turn-{turn_counter}"
                        text_input = message["params"]["input"][0]["text"]
                        print(json.dumps({"jsonrpc": "2.0", "id": message["id"], "result": {"turn": {"id": turn_id}}}), flush=True)
                        print(json.dumps({"jsonrpc": "2.0", "method": "turn/started", "params": {"threadId": thread_id, "turn": {"id": turn_id}}}), flush=True)
                        time.sleep(turn_delay)
                        print(json.dumps({"jsonrpc": "2.0", "method": "item/agentMessage/delta", "params": {"delta": text_input[:20]}}), flush=True)
                        print(json.dumps({"jsonrpc": "2.0", "method": "turn/completed", "params": {"threadId": thread_id, "turn": {"id": turn_id}}}), flush=True)
                """
            ),
        )

        make_executable(
            self.fake_bin / "den-agent",
            textwrap.dedent(
                """\
                #!/usr/bin/env python3
                import json
                import os
                import pathlib
                import sys

                log_path = pathlib.Path(os.environ["DEN_CODEX_BRIDGE_TEST_DEN_AGENT_LOG"])
                with log_path.open("a", encoding="utf-8") as handle:
                    handle.write(json.dumps({"argv": sys.argv[1:]}) + "\\n")
                """
            ),
        )

        self.den_server = DenServer(self.project_root)
        self.den_server.start()
        self.addCleanup(self.den_server.stop)

        self.den_server.dispatches = {
            41: {
                "dispatch": {
                    "id": 41,
                    "project_id": "sample",
                    "summary": "First queued task",
                    "context_prompt": "Prompt 41",
                },
                "context": {
                    "dispatch": {"id": 41, "project_id": "sample", "target_agent": "codex"},
                    "context": {"task_id": 41, "context_kind": "handoff", "next_actions": ["Do first thing"]},
                },
            },
            42: {
                "dispatch": {
                    "id": 42,
                    "project_id": "sample",
                    "summary": "Second queued task",
                    "context_prompt": "Prompt 42",
                },
                "context": {
                    "dispatch": {"id": 42, "project_id": "sample", "target_agent": "codex"},
                    "context": {"task_id": 42, "context_kind": "handoff", "next_actions": ["Do second thing"]},
                },
            },
        }

    def base_env(self) -> dict[str, str]:
        env = os.environ.copy()
        env["PATH"] = f"{self.fake_bin}:{env['PATH']}"
        env["DEN_CODEX_BRIDGE_TEST_CODEX_LOG"] = str(self.codex_log)
        env["DEN_CODEX_BRIDGE_TEST_DEN_AGENT_LOG"] = str(self.den_agent_log)
        return env

    def read_jsonl(self, path: pathlib.Path) -> list[dict]:
        if not path.exists():
            return []
        return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]

    def wait_for_state(self) -> dict:
        state_path = self.state_root / "sample" / "state.json"
        deadline = time.time() + 5
        while time.time() < deadline:
            if state_path.exists():
                state = json.loads(state_path.read_text(encoding="utf-8"))
                if state.get("wake_port"):
                    return state
            time.sleep(0.05)
        self.fail("bridge state file never became ready")

    def wait_for(self, predicate) -> None:
        deadline = time.time() + 5
        while time.time() < deadline:
            if predicate():
                return
            time.sleep(0.05)
        self.fail("timed out waiting for condition")

    def test_wake_queues_dispatch_and_drains_when_turn_completes(self) -> None:
        env = self.base_env()
        env["DEN_CODEX_BRIDGE_TEST_TURN_DELAY"] = "0.3"

        proc = subprocess.Popen(
            [
                "python3",
                str(BRIDGE_SCRIPT_PATH),
                "serve",
                "--project",
                "sample",
                "--project-root",
                str(self.project_root),
                "--base-url",
                self.den_server.base_url,
                "--state-root",
                str(self.state_root),
            ],
            cwd=self.project_root,
            env=env,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        self.addCleanup(lambda: proc.kill() if proc.poll() is None else None)

        try:
            self.wait_for_state()

            subprocess.run(
                [
                    "python3",
                    str(BRIDGE_SCRIPT_PATH),
                    "wake",
                    "--project",
                    "sample",
                    "--dispatch-id",
                    "41",
                    "--state-root",
                    str(self.state_root),
                ],
                cwd=self.project_root,
                env=env,
                check=True,
            )
            subprocess.run(
                [
                    "python3",
                    str(BRIDGE_SCRIPT_PATH),
                    "wake",
                    "--project",
                    "sample",
                    "--dispatch-id",
                    "42",
                    "--state-root",
                    str(self.state_root),
                ],
                cwd=self.project_root,
                env=env,
                check=True,
            )

            def have_two_turns() -> bool:
                starts = [entry for entry in self.read_jsonl(self.codex_log) if entry.get("method") == "turn/start"]
                return len(starts) == 2

            def bridge_is_idle() -> bool:
                state = json.loads((self.state_root / "sample" / "state.json").read_text(encoding="utf-8"))
                return state.get("status") == "idle" and state.get("pending_dispatch_ids") == []

            self.wait_for(have_two_turns)
            self.wait_for(lambda: self.den_server.completions == [41, 42])
            self.wait_for(bridge_is_idle)

            state = json.loads((self.state_root / "sample" / "state.json").read_text(encoding="utf-8"))
            self.assertEqual("idle", state["status"])
            self.assertEqual([], state["pending_dispatch_ids"])

            turn_starts = [entry for entry in self.read_jsonl(self.codex_log) if entry.get("method") == "turn/start"]
            self.assertEqual(2, len(turn_starts))
            self.assertIn("Dispatch: #41", turn_starts[0]["params"]["input"][0]["text"])
            self.assertIn("Dispatch: #42", turn_starts[1]["params"]["input"][0]["text"])
        finally:
            proc.terminate()
            proc.communicate(timeout=5)

    def test_fallback_uses_den_agent_resume_with_saved_prompt(self) -> None:
        env = self.base_env()
        result = subprocess.run(
            [
                "python3",
                str(BRIDGE_SCRIPT_PATH),
                "fallback",
                "--project",
                "sample",
                "--dispatch-id",
                "41",
                "--base-url",
                self.den_server.base_url,
                "--den-agent-bin",
                str(self.fake_bin / "den-agent"),
            ],
            cwd=self.project_root,
            env=env,
            text=True,
            capture_output=True,
            check=False,
        )

        self.assertEqual(0, result.returncode, result.stderr)
        calls = self.read_jsonl(self.den_agent_log)
        self.assertEqual(1, len(calls))
        self.assertEqual(
            ["codex", "--project", "sample", "resume", "--last", "Prompt 41"],
            calls[0]["argv"],
        )


if __name__ == "__main__":
    unittest.main()
