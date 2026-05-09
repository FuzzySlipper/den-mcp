#!/usr/bin/env python3
"""Validate deployed DenMcp:PiSessionHost appsettings for den-srv deploys."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple


DEN_SRV_CONVENTIONS = {
    "ComposeFile": "/data/services/den-mcp/pi-docker/compose.yaml",
    "DevDir": "/data/dev",
    "PiStateRootDir": "/data/services/den-mcp/pi-sessions",
    "CredentialFallbackRootDir": "/data/services/den-mcp/pi-credential-fallbacks",
}

FIELD_LABELS = {
    "ComposeFile": "compose file",
    "DevDir": "development root",
    "PiStateRootDir": "Pi state root",
    "CredentialFallbackRootDir": "credential fallback root",
}

HOME_PATH_PATTERNS = [
    re.compile(r"(?:^|[\s:=,])(/home/[^\s:'\",]+)"),
    re.compile(r"(?:^|[\s:=,])(/root(?:/[^\s:'\",]*)?)"),
    re.compile(r"(?:^|[\s:=,])(/Users/[^\s:'\",]+)"),
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Validate a deployed appsettings.json DenMcp:PiSessionHost section."
    )
    parser.add_argument("appsettings", help="Path to deployed appsettings.json")
    parser.add_argument("--expected-compose-file", required=True)
    parser.add_argument("--expected-dev-dir", required=True)
    parser.add_argument("--expected-pi-state-root-dir", required=True)
    parser.add_argument("--expected-credential-fallback-root-dir", required=True)
    return parser.parse_args()


def iter_strings(value: Any, path: str) -> Iterable[Tuple[str, str]]:
    if isinstance(value, str):
        yield path, value
    elif isinstance(value, dict):
        for key, child in value.items():
            child_path = f"{path}:{key}" if path else str(key)
            yield from iter_strings(child, child_path)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            child_path = f"{path}:{index}"
            yield from iter_strings(child, child_path)


def unsafe_path_reason(value: str) -> Optional[str]:
    if value == "":
        return None

    # DockerHost may be a unix:// URI; validate the path portion as a host path.
    check_value = value[len("unix://") :] if value.startswith("unix://") else value

    if "~" in check_value:
        return "contains an unexpanded '~' home reference"

    if "/home/patch" in check_value:
        return "contains /home/patch, which is not service-accessible"

    for home_path_pattern in HOME_PATH_PATTERNS:
        home_match = home_path_pattern.search(check_value)
        if home_match:
            return f"contains non-service home path {home_match.group(1)}"

    return None


def print_expected_paths(expected: Dict[str, str]) -> None:
    print("Expected PiSessionHost path conventions for this deploy:")
    for key, expected_value in expected.items():
        convention_value = DEN_SRV_CONVENTIONS[key]
        source = "den-srv convention"
        if expected_value != convention_value:
            source = "explicit deploy override"
        print(f"  {key} ({FIELD_LABELS[key]}, {source}): {expected_value}")


def main() -> int:
    args = parse_args()
    appsettings_path = Path(args.appsettings)
    expected = {
        "ComposeFile": args.expected_compose_file,
        "DevDir": args.expected_dev_dir,
        "PiStateRootDir": args.expected_pi_state_root_dir,
        "CredentialFallbackRootDir": args.expected_credential_fallback_root_dir,
    }

    print(f"Validating DenMcp:PiSessionHost in {appsettings_path}")
    print_expected_paths(expected)

    try:
        with appsettings_path.open("r", encoding="utf-8") as handle:
            settings = json.load(handle)
    except FileNotFoundError:
        print(
            f"Deploy preflight failed: appsettings file not found: {appsettings_path}",
            file=sys.stderr,
        )
        return 1
    except json.JSONDecodeError as exc:
        print(
            f"Deploy preflight failed: {appsettings_path} is not valid JSON: {exc}",
            file=sys.stderr,
        )
        return 1
    except OSError as exc:
        print(
            f"Deploy preflight failed: could not read {appsettings_path}: {exc}",
            file=sys.stderr,
        )
        return 1

    if not isinstance(settings, dict):
        print(
            "Deploy preflight failed: appsettings root must be a JSON object.",
            file=sys.stderr,
        )
        return 1

    den_mcp = settings.get("DenMcp", {})
    if den_mcp is None:
        den_mcp = {}
    if not isinstance(den_mcp, dict):
        print(
            "Deploy preflight failed: DenMcp must be a JSON object.",
            file=sys.stderr,
        )
        return 1

    pi_session_host = den_mcp.get("PiSessionHost", {})
    if pi_session_host is None:
        pi_session_host = {}
    if not isinstance(pi_session_host, dict):
        print(
            "Deploy preflight failed: DenMcp:PiSessionHost must be a JSON object.",
            file=sys.stderr,
        )
        return 1

    errors: List[str] = []

    print("Deployed PiSessionHost path values:")
    for key, expected_value in expected.items():
        actual_value = pi_session_host.get(key)
        convention_value = DEN_SRV_CONVENTIONS[key]
        if actual_value in (None, ""):
            print(f"  {key}: not set in appsettings")
            if expected_value != convention_value:
                errors.append(
                    f"{key} is not set, but this deploy explicitly expects {expected_value!r}; "
                    f"the built-in default convention is {convention_value!r}."
                )
            continue

        print(f"  {key}: {actual_value}")
        if not isinstance(actual_value, str):
            errors.append(f"{key} must be a string path, got {type(actual_value).__name__}.")
        elif actual_value != expected_value:
            errors.append(
                f"{key} is {actual_value!r}, expected {expected_value!r}. "
                "If this is intentional, rerun the deploy with the matching REMOTE_* override; "
                "otherwise update the preserved live appsettings.json."
            )

    for value_path, string_value in iter_strings(pi_session_host, "DenMcp:PiSessionHost"):
        reason = unsafe_path_reason(string_value)
        if reason:
            errors.append(f"{value_path}={string_value!r} {reason}.")

    if errors:
        sys.stdout.flush()
        print("\nDeploy preflight failed for DenMcp:PiSessionHost:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        print(
            "\nManual migration required: edit the preserved live appsettings.json "
            "before restarting the service, then rerun the deploy. Set:",
            file=sys.stderr,
        )
        for key, expected_value in expected.items():
            print(f"  DenMcp:PiSessionHost:{key} = {expected_value}", file=sys.stderr)
        print(
            "Also remove any /home/<user> or unexpanded ~ host paths from PiSessionHost "
            "credential, Docker, tmux, and related runtime path settings.",
            file=sys.stderr,
        )
        return 1

    print("PiSessionHost deploy preflight passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
