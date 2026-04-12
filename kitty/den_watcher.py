#!/usr/bin/env python3
from __future__ import annotations

import json
import os
import subprocess
import threading
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import Executor, Future, ThreadPoolExecutor
from dataclasses import dataclass, replace
from typing import TYPE_CHECKING, Any, Callable

if TYPE_CHECKING:
    from kitty.boss import Boss
    from kitty.window import Window
else:
    Boss = Any
    Window = Any


KNOWN_KEYS = {
    "den_agent",
    "den_project",
    "den_status",
    "den_task",
    "den_dispatch",
}

NOTIFY_STATUSES = {"done", "error", "waiting"}
STATUS_COLORS = {
    "idle": {"active_tab_background": "#303446", "active_tab_foreground": "#c6d0f5"},
    "working": {"active_tab_background": "#1e66f5", "active_tab_foreground": "#eff1f5"},
    "reviewing": {"active_tab_background": "#df8e1d", "active_tab_foreground": "#1e1e2e"},
    "waiting": {"active_tab_background": "#ea76cb", "active_tab_foreground": "#1e1e2e"},
    "done": {"active_tab_background": "#40a02b", "active_tab_foreground": "#eff1f5"},
    "error": {"active_tab_background": "#d20f39", "active_tab_foreground": "#eff1f5"},
}


@dataclass(frozen=True)
class AgentWindowState:
    window_id: int
    agent: str | None = None
    project: str | None = None
    status: str | None = None
    task_id: str | None = None
    dispatch_id: str | None = None

    def with_user_var(self, key: str, value: str | None) -> "AgentWindowState":
        field = {
            "den_agent": "agent",
            "den_project": "project",
            "den_status": "status",
            "den_task": "task_id",
            "den_dispatch": "dispatch_id",
        }[key]
        return replace(self, **{field: value})

    @property
    def is_managed(self) -> bool:
        return bool(self.agent or self.project)


class DenWatcher:
    def __init__(
        self,
        base_url: str | None = None,
        executor: Executor | None = None,
        notify_runner: Callable[[str, str], None] | None = None,
        dispatch_count_fetcher: Callable[[str], int | None] | None = None,
    ) -> None:
        self._base_url = (base_url or _default_base_url()).rstrip("/")
        self._executor = executor or ThreadPoolExecutor(max_workers=2, thread_name_prefix="den-watcher")
        self._notify_runner = notify_runner or self._default_notify_runner
        self._dispatch_count_fetcher = dispatch_count_fetcher or self._fetch_pending_dispatch_count
        self._lock = threading.Lock()
        self._window_states: dict[int, AgentWindowState] = {}
        self._project_pending_counts: dict[str, int] = {}
        self._project_fetches_in_flight: set[str] = set()

    def on_load(self, boss: Boss, data: dict[str, Any]) -> None:
        del boss, data

    def on_set_user_var(self, boss: Boss, window: Window, data: dict[str, Any]) -> None:
        key = str(data.get("key") or "")
        if key not in KNOWN_KEYS:
            return

        value = _normalize_user_var_value(data.get("value"))
        previous_status: str | None = None

        with self._lock:
            state = self._window_states.get(window.id, AgentWindowState(window_id=window.id))
            previous_status = state.status
            updated = state.with_user_var(key, value)
            self._window_states[window.id] = updated
            pending_dispatch_count = self._project_pending_counts.get(updated.project or "")

        self._apply_presentation(boss, window, updated, pending_dispatch_count)

        if updated.project and key in {"den_project", "den_status", "den_dispatch"}:
            self._schedule_pending_dispatch_refresh(updated.project)

        if updated.status and updated.status != previous_status and updated.status in NOTIFY_STATUSES:
            self._schedule_notification(updated, pending_dispatch_count)

    def on_close(self, boss: Boss, window: Window, data: dict[str, Any]) -> None:
        del boss, data

        with self._lock:
            removed = self._window_states.pop(window.id, None)
            if removed is None or not removed.project:
                return

            if not any(state.project == removed.project for state in self._window_states.values()):
                self._project_pending_counts.pop(removed.project, None)
                self._project_fetches_in_flight.discard(removed.project)

    def shutdown(self) -> None:
        if isinstance(self._executor, ThreadPoolExecutor):
            self._executor.shutdown(wait=False, cancel_futures=True)

    def _apply_presentation(
        self,
        boss: Boss,
        window: Window,
        state: AgentWindowState,
        pending_dispatch_count: int | None,
    ) -> None:
        if not state.is_managed:
            return

        window_title = self._build_window_title(state)
        tab_title = self._build_tab_title(state, pending_dispatch_count)

        self._safe_remote_control(
            boss,
            window,
            "set-window-title",
            "--temporary",
            "--match",
            f"id:{window.id}",
            window_title,
        )
        self._safe_remote_control(
            boss,
            window,
            "set-tab-title",
            "--match",
            f"window_id:{window.id}",
            tab_title,
        )

        colors = STATUS_COLORS.get(state.status or "")
        if colors:
            self._safe_remote_control(
                boss,
                window,
                "set-tab-color",
                "--match",
                f"window_id:{window.id}",
                *(f"{name}={value}" for name, value in colors.items()),
            )

    def _schedule_pending_dispatch_refresh(self, project: str) -> None:
        with self._lock:
            if project in self._project_fetches_in_flight:
                return
            self._project_fetches_in_flight.add(project)

        future = self._executor.submit(self._dispatch_count_fetcher, project)
        future.add_done_callback(lambda completed: self._store_pending_dispatch_count(project, completed))

    def _store_pending_dispatch_count(self, project: str, future: Future[int | None]) -> None:
        try:
            count = future.result()
        except Exception:
            count = None

        with self._lock:
            self._project_fetches_in_flight.discard(project)
            if count is not None:
                self._project_pending_counts[project] = count
                _trim_mapping(self._project_pending_counts, 64)

    def _schedule_notification(self, state: AgentWindowState, pending_dispatch_count: int | None) -> None:
        title = self._build_notification_title(state)
        body = self._build_notification_body(state, pending_dispatch_count)
        self._executor.submit(self._notify_runner, title, body)

    def _fetch_pending_dispatch_count(self, project: str) -> int | None:
        if not self._base_url:
            return None

        url = f"{self._base_url}/api/dispatch/pending/count?projectId={urllib.parse.quote(project)}"

        try:
            with urllib.request.urlopen(url, timeout=1.5) as response:
                payload = json.load(response)
        except (urllib.error.URLError, TimeoutError, ValueError, json.JSONDecodeError):
            return None

        count = payload.get("count")
        return count if isinstance(count, int) else None

    def _default_notify_runner(self, title: str, body: str) -> None:
        try:
            subprocess.Popen(
                ["kitten", "notify", title, body],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
        except OSError:
            return

    def _safe_remote_control(self, boss: Boss, window: Window, *command: str) -> None:
        try:
            boss.call_remote_control(window, command)
        except Exception:
            return

    @staticmethod
    def _build_window_title(state: AgentWindowState) -> str:
        parts = [part for part in (state.agent, state.project) if part]

        if state.task_id:
            parts.append(f"#{state.task_id}")
        if state.dispatch_id:
            parts.append(f"dispatch {state.dispatch_id}")
        if state.status:
            parts.append(state.status)

        return " · ".join(parts) if parts else f"window {state.window_id}"

    @staticmethod
    def _build_tab_title(state: AgentWindowState, pending_dispatch_count: int | None) -> str:
        parts = [part for part in (state.project, state.agent) if part]

        if state.status:
            parts.append(state.status)
        if state.task_id:
            parts.append(f"#{state.task_id}")
        if pending_dispatch_count:
            parts.append(f"{pending_dispatch_count} pending")

        return " · ".join(parts) if parts else f"window {state.window_id}"

    @staticmethod
    def _build_notification_title(state: AgentWindowState) -> str:
        status = state.status or "updated"
        project = state.project or "project"
        agent = state.agent or "agent"
        return f"{project}: {agent} is {status}"

    @staticmethod
    def _build_notification_body(state: AgentWindowState, pending_dispatch_count: int | None) -> str:
        parts = []

        if state.task_id:
            parts.append(f"task #{state.task_id}")
        if state.dispatch_id:
            parts.append(f"dispatch {state.dispatch_id}")
        if pending_dispatch_count:
            parts.append(f"{pending_dispatch_count} pending dispatches")

        return ", ".join(parts) if parts else "No active task context."


def _default_base_url() -> str:
    return (
        os.environ.get("DEN_MCP_URL")
        or os.environ.get("DEN_MCP_BASE_URL")
        or "http://127.0.0.1:5199"
    )


def _normalize_user_var_value(value: Any) -> str | None:
    if value is None:
        return None

    text = str(value).strip()
    return text or None


def _trim_mapping(mapping: dict[str, int], max_entries: int) -> None:
    while len(mapping) > max_entries:
        oldest_key = next(iter(mapping))
        mapping.pop(oldest_key, None)


_WATCHER = DenWatcher()


def on_load(boss: Boss, data: dict[str, Any]) -> None:
    _WATCHER.on_load(boss, data)


def on_set_user_var(boss: Boss, window: Window, data: dict[str, Any]) -> None:
    _WATCHER.on_set_user_var(boss, window, data)


def on_close(boss: Boss, window: Window, data: dict[str, Any]) -> None:
    _WATCHER.on_close(boss, window, data)
