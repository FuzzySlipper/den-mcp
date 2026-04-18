from __future__ import annotations

import json
import os
import pathlib
import stat
import subprocess
import tempfile
import textwrap
import time
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT_PATH = REPO_ROOT / "bin" / "den-agent"


def make_executable(path: pathlib.Path, content: str) -> None:
    path.write_text(content, encoding="utf-8")
    path.chmod(path.stat().st_mode | stat.S_IEXEC)


class DenAgentTests(unittest.TestCase):
    maxDiff = None

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)

        self.root = pathlib.Path(self.temp_dir.name)
        self.fake_bin = self.root / "bin"
        self.fake_bin.mkdir()
        self.workspace = self.root / "workspace" / "den-mcp"
        self.workspace.mkdir(parents=True)

        self.curl_log = self.root / "curl-log.jsonl"
        self.vendor_log = self.root / "vendor-log.jsonl"
        self.kitten_log = self.root / "kitten-log.jsonl"
        self.projects_file = self.root / "projects.json"
        self.dispatch_file = self.root / "dispatch.json"
        self.dispatch_context_file = self.root / "dispatch-context.json"

        self.projects_file.write_text("[]", encoding="utf-8")
        self.dispatch_file.write_text("[]", encoding="utf-8")
        self.dispatch_context_file.write_text("{}", encoding="utf-8")

        make_executable(
            self.fake_bin / "curl",
            textwrap.dedent(
                """\
                #!/usr/bin/env python3
                import json
                import os
                import sys
                from urllib.parse import urlparse

                method = "GET"
                body = None
                url = None
                args = sys.argv[1:]
                i = 0
                while i < len(args):
                    arg = args[i]
                    if arg in ("-X", "--request"):
                        method = args[i + 1]
                        i += 2
                    elif arg == "--data":
                        body = args[i + 1]
                        i += 2
                    elif arg in ("-H", "--header"):
                        i += 2
                    elif arg.startswith("http://") or arg.startswith("https://"):
                        url = arg
                        i += 1
                    else:
                        i += 1

                if os.environ.get("DEN_AGENT_TEST_FAIL_CURL") == "1":
                    sys.exit(22)

                parsed_body = None
                if body:
                    parsed_body = json.loads(body)

                with open(os.environ["DEN_AGENT_TEST_CURL_LOG"], "a", encoding="utf-8") as handle:
                    handle.write(json.dumps({"method": method, "url": url, "body": parsed_body}) + "\\n")

                path = urlparse(url).path
                if path == "/api/projects":
                    sys.stdout.write(open(os.environ["DEN_AGENT_TEST_PROJECTS_FILE"], encoding="utf-8").read())
                    raise SystemExit(0)

                if path == "/api/dispatch":
                    sys.stdout.write(open(os.environ["DEN_AGENT_TEST_DISPATCH_FILE"], encoding="utf-8").read())
                    raise SystemExit(0)

                if path.startswith("/api/dispatch/") and path.endswith("/context"):
                    sys.stdout.write(open(os.environ["DEN_AGENT_TEST_DISPATCH_CONTEXT_FILE"], encoding="utf-8").read())
                    raise SystemExit(0)

                if path == "/api/agents/checkin":
                    response = {
                        "agent": parsed_body["agent"],
                        "project_id": parsed_body["project_id"],
                        "session_id": parsed_body["session_id"],
                        "status": "active",
                        "checked_in_at": "2026-04-12T00:00:00Z",
                        "last_heartbeat": "2026-04-12T00:00:00Z",
                        "metadata": parsed_body.get("metadata"),
                    }
                    sys.stdout.write(json.dumps(response))
                    raise SystemExit(0)

                if path == "/api/agents/heartbeat":
                    sys.stdout.write('{"status":"ok"}')
                    raise SystemExit(0)

                if path == "/api/agents/checkout":
                    sys.stdout.write('{"status":"checked_out"}')
                    raise SystemExit(0)

                sys.exit(22)
                """
            ),
        )

        vendor_stub = textwrap.dedent(
            """\
            #!/usr/bin/env python3
            import json
            import os
            import pathlib
            import sys
            import time

            log_path = pathlib.Path(os.environ["DEN_AGENT_TEST_VENDOR_LOG"])
            with log_path.open("a", encoding="utf-8") as handle:
                handle.write(json.dumps({
                    "name": pathlib.Path(sys.argv[0]).name,
                    "argv": sys.argv[1:],
                }) + "\\n")

            time.sleep(float(os.environ.get("DEN_AGENT_TEST_VENDOR_SLEEP", "0")))
            raise SystemExit(int(os.environ.get("DEN_AGENT_TEST_VENDOR_EXIT", "0")))
            """
        )
        make_executable(self.fake_bin / "claude", vendor_stub)
        make_executable(self.fake_bin / "codex", vendor_stub)
        make_executable(self.fake_bin / "omp", vendor_stub)
        make_executable(
            self.fake_bin / "kitten",
            textwrap.dedent(
                """\
                #!/usr/bin/env python3
                import json
                import os
                import pathlib
                import sys

                stdin_text = sys.stdin.read()
                args = sys.argv[1:]
                entry = {"argv": args, "stdin": stdin_text}

                if "--details-file" in args:
                    details_index = args.index("--details-file")
                    if details_index + 1 < len(args):
                        details_path = pathlib.Path(args[details_index + 1])
                        entry["details_file"] = str(details_path)
                        if details_path.exists():
                            entry["details_text"] = details_path.read_text(encoding="utf-8")
                            details_path.unlink(missing_ok=True)

                with open(os.environ["DEN_AGENT_TEST_KITTEN_LOG"], "a", encoding="utf-8") as handle:
                    handle.write(json.dumps(entry) + "\\n")
                """
            ),
        )

    def base_env(self) -> dict[str, str]:
        env = os.environ.copy()
        env["PATH"] = f"{self.fake_bin}:{env['PATH']}"
        env["DEN_MCP_URL"] = "http://127.0.0.1:5199"
        env["DEN_AGENT_TEST_CURL_LOG"] = str(self.curl_log)
        env["DEN_AGENT_TEST_VENDOR_LOG"] = str(self.vendor_log)
        env["DEN_AGENT_TEST_KITTEN_LOG"] = str(self.kitten_log)
        env["DEN_AGENT_TEST_PROJECTS_FILE"] = str(self.projects_file)
        env["DEN_AGENT_TEST_DISPATCH_FILE"] = str(self.dispatch_file)
        env["DEN_AGENT_TEST_DISPATCH_CONTEXT_FILE"] = str(self.dispatch_context_file)
        env.pop("KITTY_WINDOW_ID", None)
        env.pop("KITTY_LISTEN_ON", None)
        return env

    def read_jsonl(self, path: pathlib.Path) -> list[dict]:
        if not path.exists():
            return []
        return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]

    def run_wrapper(self, *args: str, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            ["bash", str(SCRIPT_PATH), *args],
            cwd=self.workspace,
            env=env or self.base_env(),
            text=True,
            capture_output=True,
            check=False,
        )

    def write_projects(self) -> None:
        payload = [
            {
                "id": "den-mcp",
                "name": "den-mcp",
                "root_path": str(self.workspace),
                "description": "test",
                "created_at": "2026-04-12T00:00:00Z",
                "updated_at": "2026-04-12T00:00:00Z",
            }
        ]
        self.projects_file.write_text(json.dumps(payload), encoding="utf-8")

    def write_dispatch(
        self,
        *,
        prompt: str = "Dispatch prompt",
        summary: str = "Review the current implementation",
        task_id: int = 564,
        target_agent: str = "claude-code",
        dispatch_id: int = 42,
    ) -> None:
        payload = [
            {
                "id": dispatch_id,
                "project_id": "den-mcp",
                "target_agent": target_agent,
                "status": "approved",
                "trigger_type": "message_received",
                "trigger_id": 9,
                "task_id": task_id,
                "summary": summary,
                "context_prompt": prompt,
                "dedup_key": f"message_received:9:{target_agent}",
                "created_at": "2026-04-12T00:00:00Z",
                "expires_at": "2026-04-13T00:00:00Z",
                "decided_at": None,
                "completed_at": None,
                "decided_by": "user",
                "completed_by": None,
            }
        ]
        self.dispatch_file.write_text(json.dumps(payload), encoding="utf-8")

    def write_dispatch_context(
        self,
        *,
        dispatch_id: int = 42,
        task_id: int = 564,
        target_agent: str = "claude-code",
        context_kind: str = "handoff",
        target_role: str = "implementer",
        activity_hint: str | None = None,
        message_intent: str | None = "handoff",
        packet_kind: str | None = None,
        handoff_kind: str | None = None,
    ) -> None:
        payload = {
            "dispatch": {
                "id": dispatch_id,
                "project_id": "den-mcp",
                "target_agent": target_agent,
            },
            "context": {
                "schema_version": 1,
                "context_kind": context_kind,
                "project_id": "den-mcp",
                "target_agent": target_agent,
                "target_role": target_role,
                "activity_hint": activity_hint,
                "task_id": task_id,
                "sender": "codex",
                "recipient": target_agent,
                "message_intent": message_intent,
                "message_type": packet_kind or handoff_kind or message_intent,
                "packet_kind": packet_kind,
                "handoff_kind": handoff_kind,
                "branch": None,
                "from_status": None,
                "to_status": None,
                "triggering_message": None,
                "trigger_thread": None,
                "task_detail": None,
                "workflow_guardrails": [],
                "next_actions": [],
            },
        }
        self.dispatch_context_file.write_text(json.dumps(payload), encoding="utf-8")

    def test_fresh_dispatch_launch_injects_prompt_and_runs_lifecycle(self) -> None:
        self.write_projects()
        self.write_dispatch(prompt="Dispatch prompt from Den")
        self.write_dispatch_context(activity_hint="working")

        env = self.base_env()
        env["DEN_AGENT_HEARTBEAT_SECONDS"] = "0.01"
        env["DEN_AGENT_TEST_VENDOR_SLEEP"] = "0.05"
        env["KITTY_WINDOW_ID"] = "11"

        result = self.run_wrapper("claude", env=env)

        self.assertEqual(0, result.returncode, result.stderr)

        vendor_calls = self.read_jsonl(self.vendor_log)
        self.assertEqual(1, len(vendor_calls))
        self.assertEqual("claude", vendor_calls[0]["name"])
        self.assertEqual(["Dispatch prompt from Den"], vendor_calls[0]["argv"])

        curl_calls = self.read_jsonl(self.curl_log)
        urls = [call["url"] for call in curl_calls]
        self.assertIn("http://127.0.0.1:5199/api/projects", urls)
        self.assertIn("http://127.0.0.1:5199/api/agents/checkin", urls)
        self.assertTrue(any("/api/dispatch?" in url for url in urls))
        self.assertIn("http://127.0.0.1:5199/api/agents/checkout", urls)
        self.assertTrue(any(url == "http://127.0.0.1:5199/api/agents/heartbeat" for url in urls))

        self.assertEqual("", result.stdout)
        self.assertIn("starting claude with approved dispatch #42", result.stderr)

    def test_fresh_omp_dispatch_launch_injects_prompt_and_runs_lifecycle(self) -> None:
        self.write_projects()
        self.write_dispatch(prompt="Dispatch prompt from Den", target_agent="omp")
        self.write_dispatch_context(activity_hint="working", target_agent="omp")

        env = self.base_env()
        env["DEN_AGENT_HEARTBEAT_SECONDS"] = "0.01"
        env["DEN_AGENT_TEST_VENDOR_SLEEP"] = "0.05"

        result = self.run_wrapper("omp", env=env)

        self.assertEqual(0, result.returncode, result.stderr)

        vendor_calls = self.read_jsonl(self.vendor_log)
        self.assertEqual(1, len(vendor_calls))
        self.assertEqual("omp", vendor_calls[0]["name"])
        self.assertEqual(["Dispatch prompt from Den"], vendor_calls[0]["argv"])

        curl_calls = self.read_jsonl(self.curl_log)
        urls = [call["url"] for call in curl_calls]
        self.assertIn("http://127.0.0.1:5199/api/agents/checkin", urls)
        self.assertTrue(any("/api/dispatch?" in url for url in urls))
        self.assertIn("http://127.0.0.1:5199/api/agents/checkout", urls)
        self.assertIn("starting omp with approved dispatch #42", result.stderr)

    def test_review_dispatch_sets_reviewing_status_from_structured_context(self) -> None:
        self.write_projects()
        self.write_dispatch(
            prompt="Please review the branch",
            summary="Please review the current implementation",
        )
        self.write_dispatch_context(
            context_kind="review_request",
            target_role="reviewer",
            activity_hint="reviewing",
            message_intent="review_request",
            packet_kind="review_request",
        )
        env = self.base_env()
        env["KITTY_WINDOW_ID"] = "11"

        result = self.run_wrapper("claude", env=env)

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)

    def test_implementer_dispatch_sets_working_status_from_structured_context(self) -> None:
        self.write_projects()
        self.write_dispatch(
            prompt="Address the review findings and update the branch",
            summary="Review follow-up for the implementer",
        )
        self.write_dispatch_context(
            context_kind="review_feedback",
            target_role="implementer",
            activity_hint="working",
            message_intent="review_feedback",
            handoff_kind="review_feedback",
        )
        env = self.base_env()
        env["KITTY_WINDOW_ID"] = "11"

        result = self.run_wrapper("claude", env=env)

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)

    def test_summary_containing_review_stays_working_without_structured_review_hint(self) -> None:
        self.write_projects()
        self.write_dispatch(
            prompt="Continue implementing the review workflow feature",
            summary="Implement review workflow follow-up",
        )
        self.write_dispatch_context(
            context_kind="handoff",
            target_role="implementer",
            activity_hint="working",
            message_intent="handoff",
            handoff_kind="planning_summary",
        )
        env = self.base_env()
        env["KITTY_WINDOW_ID"] = "11"

        result = self.run_wrapper("claude", env=env)

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)

    def test_resume_path_keeps_vendor_args_and_prints_dispatch_fallback(self) -> None:
        self.write_projects()
        payload = [
            {
                "id": 42,
                "project_id": "den-mcp",
                "target_agent": "codex",
                "status": "approved",
                "trigger_type": "message_received",
                "trigger_id": 9,
                "task_id": 564,
                "summary": "Resume-safe dispatch",
                "context_prompt": "Prompt to paste manually",
                "dedup_key": "message_received:9:codex",
                "created_at": "2026-04-12T00:00:00Z",
                "expires_at": "2026-04-13T00:00:00Z",
                "decided_at": None,
                "completed_at": None,
                "decided_by": "user",
                "completed_by": None,
            }
        ]
        self.dispatch_file.write_text(json.dumps(payload), encoding="utf-8")

        result = self.run_wrapper("codex", "resume", "--last")

        self.assertEqual(0, result.returncode, result.stderr)

        vendor_calls = self.read_jsonl(self.vendor_log)
        self.assertEqual(1, len(vendor_calls))
        self.assertEqual("codex", vendor_calls[0]["name"])
        self.assertEqual(["resume", "--last"], vendor_calls[0]["argv"])

        self.assertIn("approved dispatch #42 is ready", result.stderr)
        self.assertIn("--- den-agent dispatch prompt start ---", result.stderr)
        self.assertIn("Prompt to paste manually", result.stderr)

    def test_omp_resume_path_keeps_vendor_args_and_prints_dispatch_fallback(self) -> None:
        self.write_projects()
        self.write_dispatch(prompt="Prompt to paste manually", summary="Resume-safe dispatch", target_agent="omp")

        result = self.run_wrapper("omp", "--continue")

        self.assertEqual(0, result.returncode, result.stderr)

        vendor_calls = self.read_jsonl(self.vendor_log)
        self.assertEqual(1, len(vendor_calls))
        self.assertEqual("omp", vendor_calls[0]["name"])
        self.assertEqual(["--continue"], vendor_calls[0]["argv"])

        self.assertIn("approved dispatch #42 is ready", result.stderr)
        self.assertIn("--- den-agent dispatch prompt start ---", result.stderr)
        self.assertIn("Prompt to paste manually", result.stderr)

    def test_omp_subcommand_passthrough_keeps_args_without_den_integration(self) -> None:
        self.write_projects()
        self.write_dispatch(prompt="Dispatch prompt from Den", target_agent="omp")

        result = self.run_wrapper("omp", "commit", "-c", "extra context", "--dry-run")

        self.assertEqual(0, result.returncode, result.stderr)

        vendor_calls = self.read_jsonl(self.vendor_log)
        self.assertEqual(1, len(vendor_calls))
        self.assertEqual("omp", vendor_calls[0]["name"])
        self.assertEqual(["commit", "-c", "extra context", "--dry-run"], vendor_calls[0]["argv"])

        self.assertEqual([], self.read_jsonl(self.curl_log))
        self.assertNotIn("Dispatch prompt from Den", result.stderr)

    def test_running_session_reports_newly_approved_dispatch(self) -> None:
        self.write_projects()
        self.write_dispatch_context(activity_hint="working")

        env = self.base_env()
        env["DEN_AGENT_DISPATCH_POLL_SECONDS"] = "0.01"
        env["DEN_AGENT_TEST_VENDOR_SLEEP"] = "0.25"

        proc = subprocess.Popen(
            ["bash", str(SCRIPT_PATH), "claude"],
            cwd=self.workspace,
            env=env,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        self.addCleanup(lambda: proc.kill() if proc.poll() is None else None)

        deadline = time.time() + 2
        while time.time() < deadline and not self.vendor_log.exists():
            time.sleep(0.01)
        self.assertTrue(self.vendor_log.exists(), "vendor process never started")

        self.write_dispatch(prompt="Prompt arriving mid-session")

        stdout, stderr = proc.communicate(timeout=5)

        self.assertEqual(0, proc.returncode, stderr)

        vendor_calls = self.read_jsonl(self.vendor_log)
        self.assertEqual(1, len(vendor_calls))
        self.assertEqual("claude", vendor_calls[0]["name"])
        self.assertEqual([], vendor_calls[0]["argv"])

        self.assertIn("new approved dispatch #42 arrived while claude is running", stderr)
        self.assertIn("kitty dispatch handoff is disabled because KITTY_WINDOW_ID is not set in this shell", stderr)
        self.assertIn("approved dispatch #42 is ready", stderr)
        self.assertIn("Prompt arriving mid-session", stderr)
        self.assertIn("--- den-agent dispatch prompt start ---", stderr)
        self.assertEqual("", stdout)
        self.assertEqual([], self.read_jsonl(self.kitten_log))

    def test_running_session_in_kitty_uses_overlay_clipboard_and_paste_without_prompt_blob(self) -> None:
        self.write_projects()
        self.write_dispatch_context(activity_hint="working")

        env = self.base_env()
        env["DEN_AGENT_DISPATCH_POLL_SECONDS"] = "0.01"
        env["DEN_AGENT_TEST_VENDOR_SLEEP"] = "0.25"
        env["KITTY_WINDOW_ID"] = "77"

        proc = subprocess.Popen(
            ["bash", str(SCRIPT_PATH), "claude"],
            cwd=self.workspace,
            env=env,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        self.addCleanup(lambda: proc.kill() if proc.poll() is None else None)

        deadline = time.time() + 2
        while time.time() < deadline and not self.vendor_log.exists():
            time.sleep(0.01)
        self.assertTrue(self.vendor_log.exists(), "vendor process never started")

        self.write_dispatch(prompt="Prompt arriving mid-session")

        stdout, stderr = proc.communicate(timeout=5)

        self.assertEqual(0, proc.returncode, stderr)

        kitten_calls = self.read_jsonl(self.kitten_log)
        self.assertEqual(4, len(kitten_calls))

        self.assertEqual(["@", "focus-window", "--match", "id:77"], kitten_calls[0]["argv"])
        self.assertEqual(["clipboard"], kitten_calls[1]["argv"])
        self.assertEqual("Prompt arriving mid-session", kitten_calls[1]["stdin"])
        self.assertEqual(
            ["@", "send-text", "--match", "id:77", "--stdin", "--bracketed-paste", "auto"],
            kitten_calls[2]["argv"],
        )
        self.assertEqual("den 42", kitten_calls[2]["stdin"])
        self.assertIn("@", kitten_calls[3]["argv"])
        self.assertIn("launch", kitten_calls[3]["argv"])
        self.assertIn("--details-file", kitten_calls[3]["argv"])
        self.assertIn("Prompt arriving mid-session", kitten_calls[3]["details_text"])
        self.assertIn("Den wake-up pasted into the active kitty session input buffer: yes", kitten_calls[3]["details_text"])
        self.assertIn('Submit "den 42" in the running session', kitten_calls[3]["details_text"])

        self.assertIn("new approved dispatch #42 arrived while claude is running", stderr)
        self.assertIn("opened kitty dispatch overlay", stderr)
        self.assertIn("pasted Den wake-up into the active kitty session input buffer", stderr)
        self.assertNotIn("--- den-agent dispatch prompt start ---", stderr)
        self.assertNotIn("Prompt arriving mid-session", stderr)
        self.assertEqual("", stdout)

    def test_den_failure_falls_back_to_manual_vendor_launch(self) -> None:
        env = self.base_env()
        env["DEN_AGENT_TEST_FAIL_CURL"] = "1"

        result = self.run_wrapper("codex", "--project", "den-mcp", "--model", "gpt-5", env=env)

        self.assertEqual(0, result.returncode, result.stderr)

        vendor_calls = self.read_jsonl(self.vendor_log)
        self.assertEqual(1, len(vendor_calls))
        self.assertEqual(["--model", "gpt-5"], vendor_calls[0]["argv"])
        self.assertIn("continuing in manual mode", result.stderr.lower())


if __name__ == "__main__":
    unittest.main()
