#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import pathlib
import shlex
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from typing import Any, Iterable


DEFAULT_BASE_URL = os.environ.get("DEN_MCP_URL") or os.environ.get("DEN_MCP_BASE_URL") or "http://127.0.0.1:5199"
DEFAULT_ROLES = {
    "implementer": "claude-code",
    "reviewer": "codex",
}
PREFERRED_ROLE_ORDER = ("implementer", "reviewer")
KNOWN_WRAPPED_AGENTS = {
    "claude-code": "claude",
    "codex": "codex",
}


class LayoutError(RuntimeError):
    pass


@dataclass(frozen=True)
class WindowPlan:
    agent: str
    title: str
    command: tuple[str, ...]


@dataclass(frozen=True)
class ProjectLayout:
    project_id: str
    tab_title: str
    root_path: str
    windows: tuple[WindowPlan, ...]


@dataclass(frozen=True)
class WindowSnapshot:
    id: int
    title: str
    user_vars: dict[str, str]

    @property
    def agent(self) -> str | None:
        value = self.user_vars.get("den_agent")
        return value or None

    @property
    def project_id(self) -> str | None:
        value = self.user_vars.get("den_project")
        return value or None


@dataclass(frozen=True)
class TabSnapshot:
    id: int
    title: str
    windows: tuple[WindowSnapshot, ...]


@dataclass(frozen=True)
class SetupAction:
    action: str
    project_id: str
    agent: str | None = None
    tab_id: int | None = None
    window_id: int | None = None


class DenApiClient:
    def __init__(self, base_url: str = DEFAULT_BASE_URL) -> None:
        self.base_url = base_url.rstrip("/")

    def list_projects(self) -> list[dict[str, Any]]:
        return self._get_json("/api/projects")

    def get_project(self, project_id: str) -> dict[str, Any]:
        return self._get_json(f"/api/projects/{urllib.parse.quote(project_id)}")

    def get_document(self, project_id: str, slug: str) -> dict[str, Any] | None:
        try:
            return self._get_json(f"/api/projects/{urllib.parse.quote(project_id)}/documents/{urllib.parse.quote(slug)}")
        except urllib.error.HTTPError as exc:
            if exc.code == 404:
                return None
            raise

    def _get_json(self, endpoint: str) -> Any:
        url = f"{self.base_url}{endpoint}"
        request = urllib.request.Request(url, method="GET")
        try:
            with urllib.request.urlopen(request, timeout=3.0) as response:
                return json.load(response)
        except urllib.error.URLError as exc:
            raise LayoutError(f"Failed to reach den-mcp at {self.base_url}: {exc.reason}") from exc


class KittyController:
    def __init__(self, kitty_bin: str = "kitten") -> None:
        self.kitty_bin = kitty_bin

    def list_tabs(self) -> list[TabSnapshot]:
        payload = self._run("@", "ls")
        try:
            parsed = json.loads(payload)
        except json.JSONDecodeError as exc:
            raise LayoutError(f"Invalid JSON from `kitten @ ls`: {exc}") from exc

        tabs: list[TabSnapshot] = []
        for os_window in parsed:
            for tab in os_window.get("tabs", []):
                windows = []
                for window in tab.get("windows", []):
                    user_vars = window.get("user_vars") if isinstance(window.get("user_vars"), dict) else {}
                    windows.append(
                        WindowSnapshot(
                            id=int(window["id"]),
                            title=str(window.get("title") or ""),
                            user_vars={str(k): str(v) for k, v in user_vars.items()},
                        )
                    )
                tabs.append(
                    TabSnapshot(
                        id=int(tab["id"]),
                        title=str(tab.get("title") or ""),
                        windows=tuple(windows),
                    )
                )
        return tabs

    def launch_tab(self, layout: ProjectLayout, plan: WindowPlan, take_focus: bool = False) -> int:
        args = [
            "@",
            "launch",
            "--type",
            "tab",
            "--tab-title",
            layout.tab_title,
            "--title",
            plan.title,
            "--cwd",
            layout.root_path,
            "--var",
            f"den_project={layout.project_id}",
            "--var",
            f"den_agent={plan.agent}",
        ]
        if not take_focus:
            args.append("--dont-take-focus")
        args.extend(plan.command)
        return int(self._run(*args).strip())

    def launch_window(self, tab_id: int, project_id: str, root_path: str, plan: WindowPlan,
                      split: bool = True, take_focus: bool = False) -> int:
        args = [
            "@",
            "launch",
            "--type",
            "window",
            "--match",
            f"id:{tab_id}",
            "--title",
            plan.title,
            "--cwd",
            root_path,
            "--var",
            f"den_project={project_id}",
            "--var",
            f"den_agent={plan.agent}",
        ]
        if split:
            args.extend(["--location", "vsplit"])
        if not take_focus:
            args.append("--dont-take-focus")
        args.extend(plan.command)
        return int(self._run(*args).strip())

    def _run(self, *args: str) -> str:
        try:
            return subprocess.check_output([self.kitty_bin, *args], text=True, stderr=subprocess.PIPE)
        except FileNotFoundError as exc:
            raise LayoutError(f"`{self.kitty_bin}` is not available on PATH") from exc
        except subprocess.CalledProcessError as exc:
            stderr = (exc.stderr or "").strip()
            raise LayoutError(f"Kitty command failed: {stderr or exc}") from exc


def repo_root() -> pathlib.Path:
    return pathlib.Path(__file__).resolve().parents[1]


def resolve_project_ids(requested: list[str], cwd: pathlib.Path, api: DenApiClient) -> list[str]:
    if requested:
        return requested

    projects = api.list_projects()
    cwd_text = str(cwd.resolve())
    matches: list[tuple[int, str]] = []
    for project in projects:
        root_path = project.get("root_path")
        if not isinstance(root_path, str) or not root_path:
            continue
        normalized = str(pathlib.Path(root_path).resolve())
        if cwd_text == normalized or cwd_text.startswith(normalized + os.sep):
            matches.append((len(normalized), str(project["id"])))

    if not matches:
        raise LayoutError("No project ids were supplied and the current working directory does not match any Den project root.")

    matches.sort()
    return [matches[-1][1]]


def load_routing_agents(project_id: str, api: DenApiClient) -> list[str]:
    doc = api.get_document(project_id, "dispatch-routing")
    if doc is None:
        return ordered_agent_identities(DEFAULT_ROLES)

    content = doc.get("content")
    if not isinstance(content, str):
        raise LayoutError(f"dispatch-routing for {project_id} is missing string content")

    try:
        parsed = json.loads(content)
    except json.JSONDecodeError as exc:
        raise LayoutError(f"dispatch-routing for {project_id} is malformed: {exc}") from exc

    roles = parsed.get("roles")
    if not isinstance(roles, dict):
        raise LayoutError(f"dispatch-routing for {project_id} is missing a valid roles object")

    normalized: dict[str, str] = {}
    for role, agent in roles.items():
        if not isinstance(role, str) or not role.strip():
            raise LayoutError(f"dispatch-routing for {project_id} contains a blank role name")
        if not isinstance(agent, str) or not agent.strip():
            raise LayoutError(f"dispatch-routing for {project_id} contains a blank agent for role '{role}'")
        normalized[role] = agent

    agents = ordered_agent_identities(normalized)
    if not agents:
        raise LayoutError(f"dispatch-routing for {project_id} does not define any launchable agent roles")
    return agents


def ordered_agent_identities(roles: dict[str, str]) -> list[str]:
    ordered_roles = [role for role in PREFERRED_ROLE_ORDER if role in roles]
    ordered_roles.extend(role for role in roles if role not in ordered_roles)

    seen: set[str] = set()
    agents: list[str] = []
    for role in ordered_roles:
        agent = roles[role]
        if agent in seen:
            continue
        seen.add(agent)
        agents.append(agent)
    return agents


def build_launch_command(agent: str, project_id: str) -> tuple[str, ...]:
    wrapper = repo_root() / "bin" / "den-agent"
    wrapped_vendor = KNOWN_WRAPPED_AGENTS.get(agent)
    if wrapped_vendor and wrapper.exists():
        return (str(wrapper), wrapped_vendor, "--project", project_id)
    return (agent,)


def build_project_layout(project_id: str, api: DenApiClient) -> ProjectLayout:
    project_payload = api.get_project(project_id)
    project = project_payload.get("project")
    if not isinstance(project, dict):
        raise LayoutError(f"Project response for {project_id} did not contain a project object")

    root_path = project.get("root_path")
    if not isinstance(root_path, str) or not root_path.strip():
        raise LayoutError(f"Project {project_id} does not define root_path; den-layout needs an explicit root to launch windows.")

    agents = load_routing_agents(project_id, api)
    windows = tuple(
        WindowPlan(
            agent=agent,
            title=agent,
            command=build_launch_command(agent, project_id),
        )
        for agent in agents
    )
    return ProjectLayout(
        project_id=project_id,
        tab_title=project_id,
        root_path=root_path,
        windows=windows,
    )


def find_tab_for_project(tabs: Iterable[TabSnapshot], project_id: str) -> TabSnapshot | None:
    managed_match = next(
        (tab for tab in tabs if any(window.project_id == project_id for window in tab.windows)),
        None,
    )
    if managed_match is not None:
        return managed_match

    return next((tab for tab in tabs if tab.title == project_id), None)


def apply_layout(layouts: Iterable[ProjectLayout], kitty: KittyController, take_focus: bool = False) -> list[SetupAction]:
    actions: list[SetupAction] = []
    tabs = kitty.list_tabs()

    for layout in layouts:
        tab = find_tab_for_project(tabs, layout.project_id)
        if tab is None:
            first_window = layout.windows[0]
            created_window_id = kitty.launch_tab(layout, first_window, take_focus=take_focus)
            tabs = kitty.list_tabs()
            tab = find_tab_containing_window(tabs, created_window_id)
            if tab is None:
                raise LayoutError(f"Created window {created_window_id} for {layout.project_id}, but could not locate its tab afterward.")
            actions.append(SetupAction("created_tab", project_id=layout.project_id, agent=first_window.agent,
                                       tab_id=tab.id, window_id=created_window_id))
        else:
            actions.append(SetupAction("reused_tab", project_id=layout.project_id, tab_id=tab.id))

        existing_agents = {
            window.agent: window.id
            for window in tab.windows
            if window.project_id == layout.project_id and window.agent
        }

        for index, plan in enumerate(layout.windows):
            existing_window_id = existing_agents.get(plan.agent)
            if existing_window_id is not None:
                actions.append(SetupAction("reused_window", project_id=layout.project_id, agent=plan.agent,
                                           tab_id=tab.id, window_id=existing_window_id))
                continue

            split = bool(tab.windows) or index > 0
            created_window_id = kitty.launch_window(
                tab.id,
                layout.project_id,
                layout.root_path,
                plan,
                split=split,
                take_focus=take_focus,
            )
            actions.append(SetupAction("created_window", project_id=layout.project_id, agent=plan.agent,
                                       tab_id=tab.id, window_id=created_window_id))
            tabs = kitty.list_tabs()
            tab = find_tab_for_project(tabs, layout.project_id) or tab
            existing_agents[plan.agent] = created_window_id

    return actions


def find_tab_containing_window(tabs: Iterable[TabSnapshot], window_id: int) -> TabSnapshot | None:
    return next((tab for tab in tabs if any(window.id == window_id for window in tab.windows)), None)


def render_session(layouts: Iterable[ProjectLayout]) -> str:
    lines = [
        "# Generated by den-layout",
        "# Load with: kitty --session /path/to/file.kitty-session",
    ]

    for layout in layouts:
        lines.append("")
        lines.append(f"new_tab {shlex.quote(layout.tab_title)}")
        lines.append(f"cd {shlex.quote(layout.root_path)}")
        lines.append("layout splits")

        for index, window in enumerate(layout.windows):
            launch_parts = ["launch"]
            if index > 0:
                launch_parts.extend(["--location=vsplit"])
            launch_parts.extend(
                [
                    "--title",
                    window.title,
                    "--var",
                    f"den_project={layout.project_id}",
                    "--var",
                    f"den_agent={window.agent}",
                    "--cwd",
                    layout.root_path,
                ]
            )
            launch_parts.extend(window.command)
            lines.append(shell_join(launch_parts))

    return "\n".join(lines) + "\n"


def shell_join(tokens: Iterable[str]) -> str:
    return " ".join(shlex.quote(token) for token in tokens)


def serialize_actions(actions: Iterable[SetupAction]) -> list[dict[str, Any]]:
    return [
        {
            "action": action.action,
            "project_id": action.project_id,
            "agent": action.agent,
            "tab_id": action.tab_id,
            "window_id": action.window_id,
        }
        for action in actions
    ]


def build_layouts(project_ids: list[str], api: DenApiClient, cwd: pathlib.Path | None = None) -> list[ProjectLayout]:
    resolved_project_ids = resolve_project_ids(project_ids, cwd or pathlib.Path.cwd(), api)
    return [build_project_layout(project_id, api) for project_id in resolved_project_ids]


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate and apply Den-driven Kitty layouts.")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="den-mcp base URL")
    subparsers = parser.add_subparsers(dest="command", required=True)

    setup = subparsers.add_parser("setup", help="Create or repair the current Kitty layout")
    setup.add_argument("project_ids", nargs="*", help="One or more Den project ids. If omitted, resolve from cwd.")
    setup.add_argument("--take-focus", action="store_true", help="Allow newly created tabs/windows to take focus")
    setup.add_argument("--json", action="store_true", help="Print setup actions as JSON")

    session = subparsers.add_parser("session", help="Render a kitty session file for one or more projects")
    session.add_argument("project_ids", nargs="*", help="One or more Den project ids. If omitted, resolve from cwd.")
    session.add_argument("--output", help="Write the generated session file to this path instead of stdout")

    plan = subparsers.add_parser("plan", help="Print the resolved layout plan as JSON")
    plan.add_argument("project_ids", nargs="*", help="One or more Den project ids. If omitted, resolve from cwd.")

    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    api = DenApiClient(args.base_url)

    try:
        layouts = build_layouts(getattr(args, "project_ids", []), api)
        if args.command == "session":
            session_text = render_session(layouts)
            if args.output:
                output_path = pathlib.Path(args.output)
                output_path.write_text(session_text, encoding="utf-8")
            else:
                sys.stdout.write(session_text)
            return 0

        if args.command == "plan":
            payload = [
                {
                    "project_id": layout.project_id,
                    "tab_title": layout.tab_title,
                    "root_path": layout.root_path,
                    "windows": [
                        {"agent": window.agent, "title": window.title, "command": list(window.command)}
                        for window in layout.windows
                    ],
                }
                for layout in layouts
            ]
            json.dump(payload, sys.stdout, indent=2)
            sys.stdout.write("\n")
            return 0

        kitty = KittyController()
        actions = apply_layout(layouts, kitty, take_focus=args.take_focus)
        if args.json:
            json.dump(serialize_actions(actions), sys.stdout, indent=2)
            sys.stdout.write("\n")
        else:
            for action in actions:
                if action.agent:
                    print(f"{action.action}: {action.project_id} {action.agent}")
                else:
                    print(f"{action.action}: {action.project_id}")
        return 0
    except LayoutError as exc:
        print(f"den-layout: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
