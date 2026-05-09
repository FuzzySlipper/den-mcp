import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
SCRIPT = REPO_ROOT / "scripts" / "validate-pi-session-host-appsettings.py"
APPSETTINGS = REPO_ROOT / "src" / "DenMcp.Server" / "appsettings.json"
TMP_ROOT = Path(os.environ.get("DEN_MCP_TEST_TMPDIR", "/tmp/den-mcp"))

EXPECTED_PATHS = {
    "ComposeFile": "/data/services/den-mcp/pi-docker/compose.yaml",
    "DevDir": "/data/dev",
    "PiStateRootDir": "/data/services/den-mcp/pi-sessions",
    "CredentialFallbackRootDir": "/data/services/den-mcp/pi-credential-fallbacks",
}


def valid_settings():
    return {
        "DenMcp": {
            "PiSessionHost": {
                "ComposeFile": EXPECTED_PATHS["ComposeFile"],
                "DevDir": EXPECTED_PATHS["DevDir"],
                "PiStateRootDir": EXPECTED_PATHS["PiStateRootDir"],
                "CredentialFallbackRootDir": EXPECTED_PATHS["CredentialFallbackRootDir"],
                "DockerHost": "unix:///run/den-mcp/docker-rt/docker.sock",
            }
        }
    }


class ValidatePiSessionHostAppsettingsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        TMP_ROOT.mkdir(parents=True, exist_ok=True)

    def run_validator(self, appsettings_path):
        return subprocess.run(
            [
                sys.executable,
                str(SCRIPT),
                str(appsettings_path),
                "--expected-compose-file",
                EXPECTED_PATHS["ComposeFile"],
                "--expected-dev-dir",
                EXPECTED_PATHS["DevDir"],
                "--expected-pi-state-root-dir",
                EXPECTED_PATHS["PiStateRootDir"],
                "--expected-credential-fallback-root-dir",
                EXPECTED_PATHS["CredentialFallbackRootDir"],
            ],
            cwd=REPO_ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )

    def run_validator_with_json(self, settings):
        with tempfile.TemporaryDirectory(
            prefix="validate-pi-session-host-", dir=TMP_ROOT
        ) as temp_dir:
            appsettings_path = Path(temp_dir) / "appsettings.json"
            appsettings_path.write_text(json.dumps(settings), encoding="utf-8")
            return self.run_validator(appsettings_path)

    def test_repo_appsettings_passes_without_warning(self):
        result = self.run_validator(APPSETTINGS)

        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        self.assertIn("PiSessionHost deploy preflight passed.", result.stdout)
        self.assertNotIn("Warning:", result.stdout)
        self.assertEqual(result.stderr, "")

    def test_rejects_unsafe_home_paths_anywhere_in_pi_session_host(self):
        cases = [
            ("GitConfigPath", "/home/patch/.gitconfig", "contains /home/patch"),
            ("SshDir", "~/.ssh", "contains an unexpanded '~' home reference"),
            ("DockerHost", "unix:///root/docker.sock", "contains non-service home path /root"),
            ("GhConfigDir", "/Users/alice/.config/gh", "contains non-service home path /Users/alice"),
        ]
        for field, value, expected_reason in cases:
            with self.subTest(field=field, value=value):
                settings = valid_settings()
                settings["DenMcp"]["PiSessionHost"][field] = value

                result = self.run_validator_with_json(settings)

                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn("Deploy preflight failed for DenMcp:PiSessionHost", result.stderr)
                self.assertIn(expected_reason, result.stderr)

    def test_missing_file_fails(self):
        with tempfile.TemporaryDirectory(
            prefix="validate-pi-session-host-", dir=TMP_ROOT
        ) as temp_dir:
            result = self.run_validator(Path(temp_dir) / "missing-appsettings.json")

        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("appsettings file not found", result.stderr)

    def test_non_json_file_fails(self):
        with tempfile.TemporaryDirectory(
            prefix="validate-pi-session-host-", dir=TMP_ROOT
        ) as temp_dir:
            appsettings_path = Path(temp_dir) / "appsettings.json"
            appsettings_path.write_text("{not json", encoding="utf-8")

            result = self.run_validator(appsettings_path)

        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("is not valid JSON", result.stderr)

    def test_missing_or_empty_pi_session_host_warns_but_passes_with_defaults(self):
        cases = [
            ({}, "DenMcp section is missing"),
            ({"DenMcp": {}}, "DenMcp:PiSessionHost section is missing"),
            ({"DenMcp": {"PiSessionHost": {}}}, "DenMcp:PiSessionHost section is empty"),
            (
                {"DenMcp": {"PiSessionHost": {"Service": "pi"}}},
                "does not set any explicit path keys",
            ),
        ]
        for settings, expected_warning in cases:
            with self.subTest(settings=settings):
                result = self.run_validator_with_json(settings)

                self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
                self.assertIn("PiSessionHost deploy preflight warnings:", result.stdout)
                self.assertIn(expected_warning, result.stdout)
                self.assertIn("PiSessionHost deploy preflight passed.", result.stdout)
                self.assertEqual(result.stderr, "")


if __name__ == "__main__":
    unittest.main()
