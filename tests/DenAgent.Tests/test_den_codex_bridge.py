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
import urllib.parse
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
        self.approved_dispatch_ids: list[int] = []
        self.completions: list[int] = []
        self.stream_entries: dict[int, dict] = {}
        self.ops_entries: list[dict] = []
        self.checkins: list[dict] = []
        self.heartbeats: list[dict] = []
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
                parsed = urllib.parse.urlparse(self.path)
                if parsed.path == "/api/agents/bindings":
                    query = urllib.parse.parse_qs(parsed.query)
                    bindings = self._filter_bindings(outer.checkins, query)
                    return self._json(bindings)
                if parsed.path == "/api/agent-stream":
                    query = urllib.parse.parse_qs(parsed.query)
                    entries = list(outer.stream_entries.values())
                    entries = self._filter_entries(entries, query)
                    entries.sort(key=lambda entry: entry["id"], reverse=True)
                    limit = int(query.get("limit", ["50"])[0])
                    return self._json(entries[:limit])
                if parsed.path.startswith("/api/agent-stream/"):
                    entry_id = int(parsed.path.split("/")[3])
                    entry = outer.stream_entries.get(entry_id)
                    if entry is None:
                        self.send_error(404)
                        return
                    return self._json(entry)
                if self.path.startswith("/api/dispatch?"):
                    return self._json([outer.dispatches[dispatch_id]["dispatch"] for dispatch_id in outer.approved_dispatch_ids])
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
                if self.path == "/api/agents/checkin":
                    payload = self._read_json()
                    outer.checkins.append(payload)
                    return self._json({"agent": payload["agent"], "project_id": payload["project_id"]})
                if self.path == "/api/agents/heartbeat":
                    payload = self._read_json()
                    outer.heartbeats.append(payload)
                    return self._json({"status": "ok"})
                if self.path.startswith("/api/projects/") and self.path.endswith("/agent-stream/ops"):
                    project_id = self.path.split("/")[3]
                    payload = self._read_json()
                    entry = dict(payload)
                    entry.setdefault("id", max([0, *outer.stream_entries.keys()]) + 1)
                    entry["project_id"] = entry.get("project_id") or project_id
                    entry["stream_kind"] = "ops"
                    entry.setdefault("created_at", "2026-04-23T00:00:00Z")
                    outer.ops_entries.append(entry)
                    outer.stream_entries[entry["id"]] = entry
                    return self._json(entry, status=201)
                if self.path.startswith("/api/dispatch/") and self.path.endswith("/complete"):
                    dispatch_id = int(self.path.split("/")[3])
                    outer.completions.append(dispatch_id)
                    return self._json({"id": dispatch_id, "status": "completed"})
                self.send_error(404)

            def _filter_entries(self, entries, query):
                filters = {
                    "projectId": "project_id",
                    "streamKind": "stream_kind",
                    "eventType": "event_type",
                    "recipientAgent": "recipient_agent",
                    "recipientRole": "recipient_role",
                    "recipientInstanceId": "recipient_instance_id",
                }
                for query_name, entry_name in filters.items():
                    expected = query.get(query_name, [None])[0]
                    if expected:
                        entries = [entry for entry in entries if entry.get(entry_name) == expected]
                return entries

            def _filter_bindings(self, checkins, query):
                bindings = []
                for checkin in checkins:
                    binding = {
                        "instance_id": checkin.get("instance_id"),
                        "project_id": checkin.get("project_id"),
                        "agent_identity": checkin.get("agent"),
                        "role": checkin.get("role"),
                        "transport_kind": checkin.get("transport_kind"),
                        "status": "active",
                    }
                    if query.get("projectId", [binding["project_id"]])[0] != binding["project_id"]:
                        continue
                    if query.get("agentIdentity", [binding["agent_identity"]])[0] != binding["agent_identity"]:
                        continue
                    if query.get("role", [binding["role"]])[0] != binding["role"]:
                        continue
                    bindings.append(binding)
                return bindings

            def _read_json(self):
                length = int(self.headers.get("Content-Length", "0"))
                body = self.rfile.read(length)
                return json.loads(body.decode("utf-8") or "{}")

            def _json(self, payload, status=200):
                body = json.dumps(payload).encode("utf-8")
                self.send_response(status)
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
                turn_request_counter = 0
                turn_delay = float(os.environ.get("DEN_CODEX_BRIDGE_TEST_TURN_DELAY", "0.1"))
                fail_turn_start_count = int(os.environ.get("DEN_CODEX_BRIDGE_TEST_FAIL_TURN_START_COUNT", "0"))
                emit_bad_json = os.environ.get("DEN_CODEX_BRIDGE_TEST_EMIT_BAD_JSON") == "1"
                bad_json_emitted = False

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
                        turn_request_counter += 1
                        if emit_bad_json and not bad_json_emitted:
                            print("{not valid json", flush=True)
                            bad_json_emitted = True
                        if turn_request_counter <= fail_turn_start_count:
                            print(json.dumps({"jsonrpc": "2.0", "id": message["id"], "error": {"code": -32000, "message": "synthetic turn failure"}}), flush=True)
                            continue
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
                    "target_agent": "codex",
                    "status": "approved",
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
                    "target_agent": "codex",
                    "status": "approved",
                    "summary": "Second queued task",
                    "context_prompt": "Prompt 42",
                },
                "context": {
                    "dispatch": {"id": 42, "project_id": "sample", "target_agent": "codex"},
                    "context": {"task_id": 42, "context_kind": "handoff", "next_actions": ["Do second thing"]},
                },
            },
            43: {
                "dispatch": {
                    "id": 43,
                    "project_id": "sample",
                    "target_agent": "claude-code",
                    "status": "approved",
                    "summary": "Wrong target agent",
                    "context_prompt": "Prompt 43",
                },
                "context": {
                    "dispatch": {"id": 43, "project_id": "sample", "target_agent": "claude-code"},
                    "context": {"task_id": 43, "context_kind": "handoff", "next_actions": ["Do not run in codex"]},
                },
            },
            44: {
                "dispatch": {
                    "id": 44,
                    "project_id": "sample",
                    "target_agent": "codex",
                    "status": "rejected",
                    "summary": "Rejected dispatch",
                    "context_prompt": "Prompt 44",
                },
                "context": {
                    "dispatch": {"id": 44, "project_id": "sample", "target_agent": "codex"},
                    "context": {"task_id": 44, "context_kind": "handoff", "next_actions": ["Should stay rejected"]},
                },
            },
            45: {
                "dispatch": {
                    "id": 45,
                    "project_id": "other-project",
                    "target_agent": "codex",
                    "status": "approved",
                    "summary": "Wrong project",
                    "context_prompt": "Prompt 45",
                },
                "context": {
                    "dispatch": {"id": 45, "project_id": "other-project", "target_agent": "codex"},
                    "context": {"task_id": 45, "context_kind": "handoff", "next_actions": ["Wrong bridge"]},
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

    def read_state(self) -> dict:
        return json.loads((self.state_root / "sample" / "state.json").read_text(encoding="utf-8"))

    def add_stream_entry(self, entry_id: int, **overrides) -> dict:
        entry = {
            "id": entry_id,
            "stream_kind": "message",
            "event_type": "question",
            "project_id": "sample",
            "task_id": None,
            "thread_id": None,
            "dispatch_id": None,
            "sender": "user",
            "sender_instance_id": None,
            "recipient_agent": "codex",
            "recipient_role": None,
            "recipient_instance_id": "codex-sample-bridge",
            "delivery_mode": "wake",
            "body": "Can you check the stream path?",
            "metadata": {"source": "test"},
            "dedup_key": None,
            "created_at": "2026-04-23T00:00:00Z",
        }
        entry.update(overrides)
        self.den_server.stream_entries[entry_id] = entry
        return entry

    def turn_starts(self) -> list[dict]:
        return [entry for entry in self.read_jsonl(self.codex_log) if entry.get("method") == "turn/start"]

    def start_bridge(self, env: dict[str, str]) -> subprocess.Popen[str]:
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
                "--instance-id",
                "codex-sample-bridge",
                "--role",
                "implementer",
                "--stream-poll-interval",
                "0.1",
            ],
            cwd=self.project_root,
            env=env,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        self.addCleanup(lambda: proc.kill() if proc.poll() is None else None)
        self.wait_for_state()
        return proc

    def wake_dispatch(self, env: dict[str, str], dispatch_id: int) -> None:
        subprocess.run(
            [
                "python3",
                str(BRIDGE_SCRIPT_PATH),
                "wake",
                "--project",
                "sample",
                "--dispatch-id",
                str(dispatch_id),
                "--state-root",
                str(self.state_root),
            ],
            cwd=self.project_root,
            env=env,
            check=True,
        )

    def wake_dispatch_result(self, env: dict[str, str], dispatch_id: int) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                "python3",
                str(BRIDGE_SCRIPT_PATH),
                "wake",
                "--project",
                "sample",
                "--dispatch-id",
                str(dispatch_id),
                "--state-root",
                str(self.state_root),
            ],
            cwd=self.project_root,
            env=env,
            text=True,
            capture_output=True,
            check=False,
        )

    def test_wake_queues_dispatch_and_drains_when_turn_completes(self) -> None:
        env = self.base_env()
        env["DEN_CODEX_BRIDGE_TEST_TURN_DELAY"] = "0.3"
        proc = self.start_bridge(env)

        try:
            self.wake_dispatch(env, 41)
            self.wake_dispatch(env, 42)

            def have_two_turns() -> bool:
                starts = [entry for entry in self.read_jsonl(self.codex_log) if entry.get("method") == "turn/start"]
                return len(starts) == 2

            def bridge_is_idle() -> bool:
                state = self.read_state()
                return state.get("status") == "idle" and state.get("pending_dispatch_ids") == []

            self.wait_for(have_two_turns)
            self.wait_for(lambda: self.den_server.completions == [41, 42])
            self.wait_for(bridge_is_idle)

            state = self.read_state()
            self.assertEqual("idle", state["status"])
            self.assertEqual([], state["pending_dispatch_ids"])

            turn_starts = [entry for entry in self.read_jsonl(self.codex_log) if entry.get("method") == "turn/start"]
            self.assertEqual(2, len(turn_starts))
            self.assertIn("Dispatch: #41", turn_starts[0]["params"]["input"][0]["text"])
            self.assertIn("Dispatch: #42", turn_starts[1]["params"]["input"][0]["text"])
        finally:
            proc.terminate()
            proc.communicate(timeout=5)

    def test_malformed_stdout_is_ignored_and_delivered_dispatch_is_not_requeued(self) -> None:
        env = self.base_env()
        env["DEN_CODEX_BRIDGE_TEST_EMIT_BAD_JSON"] = "1"
        proc = self.start_bridge(env)

        try:
            self.wake_dispatch(env, 41)
            self.wait_for(lambda: self.den_server.completions == [41])
            self.wait_for(lambda: self.read_state().get("status") == "idle")

            self.wake_dispatch(env, 41)
            time.sleep(0.3)

            turn_starts = [entry for entry in self.read_jsonl(self.codex_log) if entry.get("method") == "turn/start"]
            self.assertEqual(1, len(turn_starts))

            state = self.read_state()
            self.assertEqual("idle", state["status"])
            self.assertEqual([], state["pending_dispatch_ids"])
            self.assertEqual([41], state["delivered_dispatch_ids"])
        finally:
            proc.terminate()
            _stdout, stderr = proc.communicate(timeout=5)

        self.assertIn("ignoring malformed app-server stdout line", stderr)

    def test_repeated_turn_failures_degrade_bridge_and_stop_retrying(self) -> None:
        env = self.base_env()
        env["DEN_CODEX_BRIDGE_TEST_FAIL_TURN_START_COUNT"] = "3"
        proc = self.start_bridge(env)

        try:
            self.wake_dispatch(env, 41)
            self.wait_for(lambda: len([entry for entry in self.read_jsonl(self.codex_log) if entry.get("method") == "turn/start"]) == 1)
            self.wait_for(lambda: self.read_state().get("status") == "idle")

            self.wake_dispatch(env, 41)
            self.wait_for(lambda: len([entry for entry in self.read_jsonl(self.codex_log) if entry.get("method") == "turn/start"]) == 2)
            self.wait_for(lambda: self.read_state().get("status") == "idle")

            self.wake_dispatch(env, 41)
            self.wait_for(lambda: self.read_state().get("status") == "degraded")

            self.wake_dispatch(env, 41)
            time.sleep(0.3)

            turn_starts = [entry for entry in self.read_jsonl(self.codex_log) if entry.get("method") == "turn/start"]
            self.assertEqual(3, len(turn_starts))
            self.assertEqual([], self.den_server.completions)

            state = self.read_state()
            self.assertEqual("degraded", state["status"])
            self.assertEqual([41], state["pending_dispatch_ids"])
            self.assertEqual([], state["delivered_dispatch_ids"])
            self.assertEqual(3, len(state["failure_timestamps"]))
            self.assertIn("failed to start turn for dispatch #41", state["last_error"])
        finally:
            proc.terminate()
            proc.communicate(timeout=5)

    def test_wake_rejects_non_actionable_dispatch_ids(self) -> None:
        env = self.base_env()
        proc = self.start_bridge(env)

        try:
            wrong_target = self.wake_dispatch_result(env, 43)
            rejected = self.wake_dispatch_result(env, 44)
            wrong_project = self.wake_dispatch_result(env, 45)

            self.assertNotEqual(0, wrong_target.returncode)
            self.assertIn("dispatch #43 targets claude-code", wrong_target.stderr)

            self.assertNotEqual(0, rejected.returncode)
            self.assertIn("dispatch #44 is rejected", rejected.stderr)

            self.assertNotEqual(0, wrong_project.returncode)
            self.assertIn("dispatch #45 belongs to project other-project", wrong_project.stderr)

            time.sleep(0.3)

            turn_starts = [entry for entry in self.read_jsonl(self.codex_log) if entry.get("method") == "turn/start"]
            self.assertEqual([], turn_starts)

            state = self.read_state()
            self.assertEqual("idle", state["status"])
            self.assertEqual([], state["pending_dispatch_ids"])
            self.assertEqual([], state["delivered_dispatch_ids"])
            self.assertEqual([], self.den_server.completions)
        finally:
            proc.terminate()
            proc.communicate(timeout=5)

    def test_stream_wake_message_is_polled_and_delivered(self) -> None:
        self.add_stream_entry(101, recipient_instance_id=None, recipient_agent=None, recipient_role="implementer")
        env = self.base_env()
        proc = self.start_bridge(env)

        try:
            self.wait_for(lambda: len(self.turn_starts()) == 1)
            self.wait_for(
                lambda: self.read_state().get("status") == "idle"
                and self.read_state().get("delivered_stream_entry_ids") == [101]
            )
            self.wait_for(lambda: len(self.den_server.ops_entries) == 1)

            turn_start = self.turn_starts()[0]
            prompt = turn_start["params"]["input"][0]["text"]
            self.assertIn("Agent stream entry: #101", prompt)
            self.assertIn("Can you check the stream path?", prompt)

            state = self.read_state()
            self.assertEqual([], state["pending_stream_entry_ids"])
            self.assertEqual([101], state["delivered_stream_entry_ids"])
            self.assertEqual("codex-sample-bridge", state["instance_id"])
            self.assertEqual("implementer", state["role"])

            checkin = self.den_server.checkins[0]
            self.assertEqual("codex-sample-bridge", checkin["instance_id"])
            self.assertEqual("implementer", checkin["role"])
            self.assertEqual("codex_app_server", checkin["transport_kind"])

            ops_entry = self.den_server.ops_entries[0]
            self.assertEqual("wake_delivered", ops_entry["event_type"])
            self.assertEqual("record_only", ops_entry["delivery_mode"])
            self.assertEqual("codex-sample-bridge", ops_entry["recipient_instance_id"])
            self.assertEqual("wake-delivered:agent-stream:101:codex-sample-bridge", ops_entry["dedup_key"])
            self.assertEqual(101, json.loads(ops_entry["metadata"])["source_entry_id"])
        finally:
            proc.terminate()
            proc.communicate(timeout=5)

    def test_stream_wake_message_is_not_replayed_after_restart(self) -> None:
        self.add_stream_entry(101)
        env = self.base_env()
        proc = self.start_bridge(env)

        try:
            self.wait_for(lambda: len(self.turn_starts()) == 1)
            self.wait_for(lambda: self.read_state().get("delivered_stream_entry_ids") == [101])
        finally:
            proc.terminate()
            proc.communicate(timeout=5)

        proc = self.start_bridge(env)
        try:
            time.sleep(0.4)
            self.assertEqual(1, len(self.turn_starts()))
            state = self.read_state()
            self.assertEqual("idle", state["status"])
            self.assertEqual([], state["pending_stream_entry_ids"])
            self.assertEqual([101], state["delivered_stream_entry_ids"])
            self.assertEqual(1, len(self.den_server.ops_entries))
        finally:
            proc.terminate()
            proc.communicate(timeout=5)

    def test_stream_poll_ignores_non_target_entries(self) -> None:
        self.add_stream_entry(101, recipient_instance_id="codex-other-bridge")
        self.add_stream_entry(102, recipient_instance_id=None, recipient_agent="claude-code")
        self.add_stream_entry(103, recipient_instance_id=None, delivery_mode="notify")
        self.add_stream_entry(104, recipient_instance_id=None, recipient_agent=None, recipient_role="reviewer")
        self.add_stream_entry(105, project_id="other-project")

        env = self.base_env()
        proc = self.start_bridge(env)

        try:
            time.sleep(0.4)
            self.assertEqual([], self.turn_starts())
            self.assertEqual([], self.den_server.ops_entries)
            state = self.read_state()
            self.assertEqual("idle", state["status"])
            self.assertEqual([], state["pending_stream_entry_ids"])
            self.assertEqual([], state["delivered_stream_entry_ids"])
        finally:
            proc.terminate()
            proc.communicate(timeout=5)

    def test_stream_poll_records_wake_dropped_for_ambiguous_binding_resolution(self) -> None:
        self.den_server.checkins.append(
            {
                "agent": "codex",
                "project_id": "sample",
                "instance_id": "codex-other-implementer",
                "role": "implementer",
                "transport_kind": "codex_app_server",
            }
        )
        self.add_stream_entry(106, recipient_instance_id=None, recipient_agent=None, recipient_role="implementer")

        env = self.base_env()
        proc = self.start_bridge(env)

        try:
            self.wait_for(lambda: len(self.den_server.ops_entries) == 1)
            self.assertEqual([], self.turn_starts())
            state = self.read_state()
            self.assertEqual("idle", state["status"])
            self.assertEqual([], state["pending_stream_entry_ids"])
            self.assertEqual([106], state["delivered_stream_entry_ids"])

            ops_entry = self.den_server.ops_entries[0]
            self.assertEqual("wake_dropped", ops_entry["event_type"])
            self.assertEqual("record_only", ops_entry["delivery_mode"])
            self.assertEqual("wake-dropped:106:ambiguous", ops_entry["dedup_key"])
            metadata = json.loads(ops_entry["metadata"])
            self.assertEqual(106, metadata["source_entry_id"])
            self.assertEqual("ambiguous", metadata["resolution_status"])
            self.assertEqual(
                ["codex-other-implementer", "codex-sample-bridge"],
                sorted(metadata["candidate_instance_ids"]),
            )
        finally:
            proc.terminate()
            proc.communicate(timeout=5)

    def test_dispatch_and_stream_queues_alternate_when_both_are_pending(self) -> None:
        self.den_server.approved_dispatch_ids = [41, 42]
        self.add_stream_entry(101)
        env = self.base_env()
        proc = self.start_bridge(env)

        try:
            self.wait_for(lambda: len(self.turn_starts()) == 3)
            prompts = [entry["params"]["input"][0]["text"] for entry in self.turn_starts()]
            self.assertIn("Dispatch: #41", prompts[0])
            self.assertIn("Agent stream entry: #101", prompts[1])
            self.assertIn("Dispatch: #42", prompts[2])
            self.wait_for(lambda: self.den_server.completions == [41, 42])
            self.wait_for(lambda: self.read_state().get("status") == "idle")

            state = self.read_state()
            self.assertEqual("idle", state["status"])
            self.assertEqual([], state["pending_dispatch_ids"])
            self.assertEqual([], state["pending_stream_entry_ids"])
            self.assertEqual([41, 42], state["delivered_dispatch_ids"])
            self.assertEqual([101], state["delivered_stream_entry_ids"])
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
