from __future__ import annotations

import json
import os
import pathlib
import stat
import subprocess
import tempfile
import textwrap
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
        self.projects_file = self.root / "projects.json"
        self.dispatch_file = self.root / "dispatch.json"

        self.projects_file.write_text("[]", encoding="utf-8")
        self.dispatch_file.write_text("[]", encoding="utf-8")

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

    def base_env(self) -> dict[str, str]:
        env = os.environ.copy()
        env["PATH"] = f"{self.fake_bin}:{env['PATH']}"
        env["DEN_AGENT_TEST_CURL_LOG"] = str(self.curl_log)
        env["DEN_AGENT_TEST_VENDOR_LOG"] = str(self.vendor_log)
        env["DEN_AGENT_TEST_PROJECTS_FILE"] = str(self.projects_file)
        env["DEN_AGENT_TEST_DISPATCH_FILE"] = str(self.dispatch_file)
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

    def write_dispatch(self, *, prompt: str = "Dispatch prompt", task_id: int = 564) -> None:
        payload = [
            {
                "id": 42,
                "project_id": "den-mcp",
                "target_agent": "claude-code",
                "status": "approved",
                "trigger_type": "message_received",
                "trigger_id": 9,
                "task_id": task_id,
                "summary": "Review the current implementation",
                "context_prompt": prompt,
                "dedup_key": "message_received:9:claude-code",
                "created_at": "2026-04-12T00:00:00Z",
                "expires_at": "2026-04-13T00:00:00Z",
                "decided_at": None,
                "completed_at": None,
                "decided_by": "user",
                "completed_by": None,
            }
        ]
        self.dispatch_file.write_text(json.dumps(payload), encoding="utf-8")

    def test_fresh_dispatch_launch_injects_prompt_and_runs_lifecycle(self) -> None:
        self.write_projects()
        self.write_dispatch(prompt="Dispatch prompt from Den")

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

        self.assertIn("SetUserVar=den_agent=", result.stdout)
        self.assertIn("SetUserVar=den_project=", result.stdout)
        self.assertIn("SetUserVar=den_dispatch=", result.stdout)
        self.assertIn("starting claude with approved dispatch #42", result.stderr)

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
