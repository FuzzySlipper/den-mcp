#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import textwrap
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import asdict, dataclass
from typing import Any


KITTY_LIB = pathlib.Path("/usr/lib/kitty")
if KITTY_LIB.exists():
    kitty_lib = str(KITTY_LIB)
    if kitty_lib not in sys.path:
        sys.path.insert(0, kitty_lib)

try:
    from kittens.tui.handler import Handler, result_handler
    from kittens.tui.loop import Loop
    from kittens.tui.operations import styled
    from kitty.key_encoding import EventType
except ModuleNotFoundError:  # pragma: no cover - tests exercise non-TUI helpers
    class Handler:  # type: ignore[no-redef]
        def __init__(self, *args: Any, **kwargs: Any) -> None:
            del args, kwargs

    class Loop:  # type: ignore[no-redef]
        return_code = 1

        def loop(self, handler: Any) -> None:
            del handler
            raise RuntimeError("Kitty TUI libraries are not available")

    def result_handler(*args: Any, **kwargs: Any):  # type: ignore[no-redef]
        del args, kwargs

        def decorator(func: Any) -> Any:
            return func

        return decorator

    def styled(text: str, **kwargs: Any) -> str:  # type: ignore[no-redef]
        del kwargs
        return text

    class EventType:  # type: ignore[no-redef]
        RELEASE = object()


DEFAULT_BASE_URL = os.environ.get("DEN_MCP_URL") or os.environ.get("DEN_MCP_BASE_URL") or "http://127.0.0.1:5199"
ELLIPSIS = "..."
REPO_ROOT = pathlib.Path(__file__).resolve().parents[1]


class DispatchApiError(RuntimeError):
    pass


@dataclass(frozen=True)
class DispatchEntry:
    id: int
    project_id: str
    target_agent: str
    status: str
    trigger_type: str
    trigger_id: int
    task_id: int | None
    summary: str | None
    context_prompt: str | None
    dedup_key: str
    created_at: str
    expires_at: str | None = None
    decided_at: str | None = None
    completed_at: str | None = None
    decided_by: str | None = None
    completed_by: str | None = None

    @classmethod
    def from_payload(cls, payload: dict[str, Any]) -> "DispatchEntry":
        task_id = payload.get("task_id")
        return cls(
            id=int(payload["id"]),
            project_id=str(payload["project_id"]),
            target_agent=str(payload["target_agent"]),
            status=str(payload["status"]),
            trigger_type=str(payload["trigger_type"]),
            trigger_id=int(payload["trigger_id"]),
            task_id=int(task_id) if task_id is not None else None,
            summary=_normalize_optional_text(payload.get("summary")),
            context_prompt=_normalize_optional_text(payload.get("context_prompt")),
            dedup_key=str(payload["dedup_key"]),
            created_at=str(payload["created_at"]),
            expires_at=_normalize_optional_text(payload.get("expires_at")),
            decided_at=_normalize_optional_text(payload.get("decided_at")),
            completed_at=_normalize_optional_text(payload.get("completed_at")),
            decided_by=_normalize_optional_text(payload.get("decided_by")),
            completed_by=_normalize_optional_text(payload.get("completed_by")),
        )

    def to_payload(self) -> dict[str, Any]:
        return asdict(self)

    @property
    def task_label(self) -> str:
        return f"#{self.task_id}" if self.task_id is not None else "project"

    @property
    def summary_text(self) -> str:
        return self.summary or f"{self.trigger_type} dispatch"


@dataclass(frozen=True)
class KittyWindowSnapshot:
    id: int
    tab_id: int
    title: str
    is_active: bool
    is_focused: bool
    user_vars: dict[str, str]


class DenApiClient:
    def __init__(self, base_url: str = DEFAULT_BASE_URL) -> None:
        self.base_url = base_url.rstrip("/")

    def list_pending_dispatches(self, project_id: str | None = None) -> list[DispatchEntry]:
        params = [("status", "pending")]
        if project_id:
            params.append(("projectId", project_id))
        query = urllib.parse.urlencode(params)
        payload = self._request_json("GET", f"/api/dispatch?{query}")
        if not isinstance(payload, list):
            raise DispatchApiError("Dispatch list response was not a JSON array")
        return [DispatchEntry.from_payload(item) for item in payload if isinstance(item, dict)]

    def approve_dispatch(self, dispatch_id: int, decided_by: str) -> DispatchEntry:
        payload = self._request_json("POST", f"/api/dispatch/{dispatch_id}/approve", {"decided_by": decided_by})
        if not isinstance(payload, dict):
            raise DispatchApiError(f"Approve response for dispatch #{dispatch_id} was not a JSON object")
        return DispatchEntry.from_payload(payload)

    def reject_dispatch(self, dispatch_id: int, decided_by: str) -> DispatchEntry:
        payload = self._request_json("POST", f"/api/dispatch/{dispatch_id}/reject", {"decided_by": decided_by})
        if not isinstance(payload, dict):
            raise DispatchApiError(f"Reject response for dispatch #{dispatch_id} was not a JSON object")
        return DispatchEntry.from_payload(payload)

    def _request_json(self, method: str, endpoint: str, payload: dict[str, Any] | None = None) -> Any:
        url = f"{self.base_url}{endpoint}"
        body = None
        headers = {"Accept": "application/json"}
        if payload is not None:
            body = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json"

        request = urllib.request.Request(url, data=body, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=3.0) as response:
                return json.load(response)
        except urllib.error.HTTPError as exc:
            message = _extract_http_error_message(exc)
            raise DispatchApiError(message) from exc
        except urllib.error.URLError as exc:
            raise DispatchApiError(f"Failed to reach den-mcp at {self.base_url}: {exc.reason}") from exc
        except (json.JSONDecodeError, ValueError) as exc:
            raise DispatchApiError(f"Invalid JSON from den-mcp: {exc}") from exc


class ApprovalController:
    def __init__(self, api: DenApiClient, decided_by: str, project_id: str | None = None) -> None:
        self.api = api
        self.decided_by = decided_by
        self.project_id = project_id
        self.dispatches: list[DispatchEntry] = []
        self.approved: list[DispatchEntry] = []
        self.rejected: list[DispatchEntry] = []
        self.selected_index = 0
        self.details_open = False
        self.detail_scroll = 0
        self.status_message = ""
        self.error_message: str | None = None
        self.approved_all = False

    @property
    def current_dispatch(self) -> DispatchEntry | None:
        if not self.dispatches:
            return None
        index = max(0, min(self.selected_index, len(self.dispatches) - 1))
        return self.dispatches[index]

    def refresh(self) -> None:
        current_id = self.current_dispatch.id if self.current_dispatch is not None else None

        try:
            refreshed = self.api.list_pending_dispatches(self.project_id)
        except DispatchApiError as exc:
            self.error_message = str(exc)
            if not self.dispatches:
                self.status_message = "Unable to load pending dispatches."
            return

        self.dispatches = refreshed
        self.error_message = None

        if current_id is not None:
            for index, dispatch in enumerate(self.dispatches):
                if dispatch.id == current_id:
                    self.selected_index = index
                    break
            else:
                self.selected_index = min(self.selected_index, max(0, len(self.dispatches) - 1))
        else:
            self.selected_index = min(self.selected_index, max(0, len(self.dispatches) - 1))

        self.detail_scroll = 0
        if self.dispatches:
            self.status_message = f"{len(self.dispatches)} pending dispatches."
        else:
            self.status_message = "No pending dispatches."

    def move_selection(self, delta: int) -> None:
        if not self.dispatches:
            return
        self.selected_index = max(0, min(len(self.dispatches) - 1, self.selected_index + delta))
        self.detail_scroll = 0

    def select_first(self) -> None:
        self.selected_index = 0
        self.detail_scroll = 0

    def select_last(self) -> None:
        if self.dispatches:
            self.selected_index = len(self.dispatches) - 1
            self.detail_scroll = 0

    def toggle_details(self) -> None:
        if self.current_dispatch is None:
            return
        self.details_open = not self.details_open
        self.detail_scroll = 0

    def scroll_details(self, delta: int) -> None:
        if not self.details_open:
            return
        self.detail_scroll = max(0, self.detail_scroll + delta)

    def approve_selected(self) -> bool:
        dispatch = self.current_dispatch
        if dispatch is None:
            return False

        try:
            approved = self.api.approve_dispatch(dispatch.id, self.decided_by)
        except DispatchApiError as exc:
            self.error_message = str(exc)
            return False

        self.approved.append(approved)
        self._remove_selected()
        self.error_message = None
        self.status_message = f"Approved dispatch #{approved.id} for {approved.target_agent}."
        return True

    def reject_selected(self) -> bool:
        dispatch = self.current_dispatch
        if dispatch is None:
            return False

        try:
            rejected = self.api.reject_dispatch(dispatch.id, self.decided_by)
        except DispatchApiError as exc:
            self.error_message = str(exc)
            return False

        self.rejected.append(rejected)
        self._remove_selected()
        self.error_message = None
        self.status_message = f"Rejected dispatch #{rejected.id}."
        return True

    def approve_all_remaining(self) -> bool:
        if not self.dispatches:
            return False

        approved_count = 0
        self.approved_all = True
        self.select_first()

        while self.dispatches:
            if not self.approve_selected():
                return False
            approved_count += 1
            self.select_first()

        self.status_message = f"Approved {approved_count} dispatches."
        return True

    def result_payload(self) -> dict[str, Any]:
        return {
            "approved": [entry.to_payload() for entry in self.approved],
            "rejected": [entry.to_payload() for entry in self.rejected],
            "approved_all": self.approved_all,
        }

    def _remove_selected(self) -> None:
        if not self.dispatches:
            return
        self.dispatches.pop(self.selected_index)
        if self.selected_index >= len(self.dispatches):
            self.selected_index = max(0, len(self.dispatches) - 1)
        self.detail_scroll = 0


class ApprovalHandler(Handler):
    def __init__(self, controller: ApprovalController) -> None:
        super().__init__()
        self.controller = controller

    def initialize(self) -> None:
        self.cmd.set_cursor_visible(False)
        self.controller.refresh()
        self.draw_screen()

    def on_resize(self, screen_size: Any) -> None:
        super().on_resize(screen_size)
        self.draw_screen()

    def on_text(self, text: str, in_bracketed_paste: bool = False) -> None:
        del in_bracketed_paste
        for char in text:
            if self._handle_char(char):
                return

    def on_key(self, key_event: Any) -> None:
        if getattr(key_event, "type", None) is EventType.RELEASE:
            return

        if key_event.matches("esc") or key_event.matches("q"):
            self.quit_loop(0)
            return
        if key_event.matches("enter"):
            self.controller.toggle_details()
            self.draw_screen()
            return
        if key_event.matches("up") or key_event.matches("k"):
            if self.controller.details_open and key_event.matches("k"):
                self.controller.scroll_details(-1)
            else:
                self.controller.move_selection(-1)
            self.draw_screen()
            return
        if key_event.matches("down") or key_event.matches("j"):
            if self.controller.details_open and key_event.matches("j"):
                self.controller.scroll_details(1)
            else:
                self.controller.move_selection(1)
            self.draw_screen()
            return
        if key_event.matches("page_up"):
            self.controller.scroll_details(-10)
            self.draw_screen()
            return
        if key_event.matches("page_down") or key_event.matches("space"):
            self.controller.scroll_details(10)
            self.draw_screen()
            return
        if key_event.matches("home") or key_event.matches("g"):
            if self.controller.details_open:
                self.controller.detail_scroll = 0
            else:
                self.controller.select_first()
            self.draw_screen()
            return
        if key_event.matches("end") or key_event.matches("G"):
            if self.controller.details_open:
                self.controller.detail_scroll = 10**9
            else:
                self.controller.select_last()
            self.draw_screen()
            return
        if key_event.matches("r"):
            self.controller.refresh()
            self.draw_screen()

    def _handle_char(self, char: str) -> bool:
        lower = char.lower()
        if lower == "q":
            self.quit_loop(0)
            return True
        if lower == "y":
            if self.controller.approve_selected() and not self.controller.dispatches:
                self.quit_loop(0)
            self.draw_screen()
            return True
        if lower == "n":
            if self.controller.reject_selected() and not self.controller.dispatches:
                self.quit_loop(0)
            self.draw_screen()
            return True
        if lower == "a":
            if self.controller.approve_all_remaining():
                self.quit_loop(0)
            else:
                self.draw_screen()
            return True
        if lower == "r":
            self.controller.refresh()
            self.draw_screen()
            return True
        return False

    def draw_screen(self) -> None:
        rows = max(10, getattr(self.screen_size, "rows", 24))
        cols = max(40, getattr(self.screen_size, "cols", 80))
        current = self.controller.current_dispatch

        self.cmd.clear_screen()
        self.print(styled("Dispatch Approval", bold=True, fg="cyan"))
        self.print(f"Approver: {self.controller.decided_by}    Pending: {len(self.controller.dispatches)}")
        self.print(self.controller.status_message)
        if self.controller.error_message:
            self.print(styled(self.controller.error_message, fg="red"))
        else:
            self.print("")

        reserved_lines = 6
        detail_lines_available = 0
        if self.controller.details_open and current is not None:
            detail_lines_available = max(6, rows // 2)

        list_lines_available = max(3, rows - reserved_lines - detail_lines_available)
        list_start, list_end = compute_visible_window(
            len(self.controller.dispatches),
            self.controller.selected_index,
            list_lines_available,
        )
        visible_dispatches = self.controller.dispatches[list_start:list_end]

        if not visible_dispatches:
            self.print(styled("No pending dispatches.", fg="yellow"))
            self.print("")
        else:
            hidden_above = list_start
            if hidden_above > 0:
                self.print(styled(f"... {hidden_above} earlier dispatches above", fg="yellow"))

            for offset, dispatch in enumerate(visible_dispatches):
                index = list_start + offset
                marker = ">" if index == self.controller.selected_index else " "
                row = build_dispatch_row(dispatch, cols - 2)
                if index == self.controller.selected_index:
                    row = styled(row, fg="black", bg="green")
                self.print(f"{marker} {row}")

            hidden_below = len(self.controller.dispatches) - (list_start + len(visible_dispatches))
            if hidden_below > 0:
                self.print(styled(f"... {hidden_below} more pending dispatches below", fg="yellow"))

        if self.controller.details_open and current is not None:
            self.print("")
            self.print(styled(f"Details for dispatch #{current.id}", bold=True, fg="magenta"))
            detail_lines = wrap_text_for_width(format_dispatch_detail(current), cols)
            max_start = max(0, len(detail_lines) - detail_lines_available)
            start = min(self.controller.detail_scroll, max_start)
            end = start + detail_lines_available
            for line in detail_lines[start:end]:
                self.print(line)
            if end < len(detail_lines):
                self.print(styled("... more details below", fg="yellow"))

        self.print("")
        self.print("Keys: Up/Down select  Enter details  Y approve  N reject  A approve all  R refresh  Q quit")


class DetailViewer(Handler):
    def __init__(self, title: str, text: str) -> None:
        super().__init__()
        self.title = title
        self.text = text
        self.scroll = 0

    def initialize(self) -> None:
        self.cmd.set_cursor_visible(False)
        self.draw_screen()

    def on_resize(self, screen_size: Any) -> None:
        super().on_resize(screen_size)
        self.draw_screen()

    def on_text(self, text: str, in_bracketed_paste: bool = False) -> None:
        del in_bracketed_paste
        for char in text:
            lower = char.lower()
            if lower in {"q", "\r", "\n"}:
                self.quit_loop(0)
                return
            if lower == "j":
                self.scroll += 1
            elif lower == "k":
                self.scroll = max(0, self.scroll - 1)
        self.draw_screen()

    def on_key(self, key_event: Any) -> None:
        if getattr(key_event, "type", None) is EventType.RELEASE:
            return
        if key_event.matches("esc") or key_event.matches("q") or key_event.matches("enter"):
            self.quit_loop(0)
            return
        if key_event.matches("up") or key_event.matches("k"):
            self.scroll = max(0, self.scroll - 1)
        elif key_event.matches("down") or key_event.matches("j"):
            self.scroll += 1
        elif key_event.matches("page_up"):
            self.scroll = max(0, self.scroll - 10)
        elif key_event.matches("page_down") or key_event.matches("space"):
            self.scroll += 10
        elif key_event.matches("home") or key_event.matches("g"):
            self.scroll = 0
        elif key_event.matches("end") or key_event.matches("G"):
            self.scroll = 10**9
        self.draw_screen()

    def draw_screen(self) -> None:
        rows = max(10, getattr(self.screen_size, "rows", 24))
        cols = max(40, getattr(self.screen_size, "cols", 80))
        body_lines = wrap_text_for_width(self.text, cols)
        viewport = max(3, rows - 3)
        max_start = max(0, len(body_lines) - viewport)
        start = min(self.scroll, max_start)
        end = start + viewport

        self.cmd.clear_screen()
        self.print(styled(self.title, bold=True, fg="cyan"))
        self.print(styled("Up/Down scroll  Enter or Q close", fg="yellow"))
        self.print("")
        for line in body_lines[start:end]:
            self.print(line)
        if end < len(body_lines):
            self.print(styled("... more below", fg="yellow"))


class SubprocessKittyBridge:
    def __init__(
        self,
        kitty_bin: str = "kitten",
        python_executable: str | None = None,
        script_path: pathlib.Path | None = None,
    ) -> None:
        self.kitty_bin = kitty_bin
        self.python_executable = python_executable or sys.executable
        self.script_path = script_path or pathlib.Path(__file__).resolve()

    def list_windows(self) -> list[KittyWindowSnapshot]:
        result = self._run([self.kitty_bin, "@", "ls"])
        if result.returncode != 0:
            return []
        try:
            payload = json.loads(result.stdout)
        except json.JSONDecodeError:
            return []

        windows: list[KittyWindowSnapshot] = []
        for os_window in payload:
            tabs = os_window.get("tabs")
            if not isinstance(tabs, list):
                continue
            for tab in tabs:
                tab_id = int(tab.get("id", 0))
                tab_windows = tab.get("windows")
                if not isinstance(tab_windows, list):
                    continue
                for window in tab_windows:
                    user_vars = window.get("user_vars") if isinstance(window.get("user_vars"), dict) else {}
                    normalized_user_vars = {str(key): str(value) for key, value in user_vars.items()}
                    windows.append(
                        KittyWindowSnapshot(
                            id=int(window.get("id", 0)),
                            tab_id=tab_id,
                            title=str(window.get("title") or ""),
                            is_active=bool(window.get("is_active")),
                            is_focused=bool(window.get("is_focused")),
                            user_vars=normalized_user_vars,
                        )
                    )
        return windows

    def focus_window(self, window_id: int) -> bool:
        result = self._run([self.kitty_bin, "@", "focus-window", "--match", f"id:{window_id}"])
        return result.returncode == 0

    def copy_to_clipboard(self, text: str) -> bool:
        result = self._run([self.kitty_bin, "clipboard"], input_text=text)
        return result.returncode == 0

    def launch_details_overlay(self, title: str, text: str) -> bool:
        with tempfile.NamedTemporaryFile("w", delete=False, encoding="utf-8", prefix="den-dispatch-", suffix=".txt") as handle:
            handle.write(text)
            details_path = pathlib.Path(handle.name)

        result = self._run(
            [
                self.kitty_bin,
                "@",
                "launch",
                "--type",
                "overlay",
                "--cwd",
                str(REPO_ROOT),
                "--title",
                title,
                self.python_executable,
                str(self.script_path),
                "--details-file",
                str(details_path),
                "--details-title",
                title,
            ]
        )
        if result.returncode != 0:
            details_path.unlink(missing_ok=True)
            return False
        return True

    def _run(self, args: list[str], input_text: str | None = None) -> subprocess.CompletedProcess[str]:
        try:
            return subprocess.run(
                args,
                input=input_text,
                text=True,
                capture_output=True,
                check=False,
            )
        except OSError as exc:
            return subprocess.CompletedProcess(args, 1, "", str(exc))


def build_dispatch_row(dispatch: DispatchEntry, width: int) -> str:
    parts = [f"#{dispatch.id}", dispatch.project_id, dispatch.target_agent]
    if dispatch.task_id is not None:
        parts.append(f"task #{dispatch.task_id}")
    parts.append(dispatch.summary_text)
    return truncate_text(" | ".join(parts), max(12, width))


def format_dispatch_detail(dispatch: DispatchEntry) -> str:
    lines = [
        f"Dispatch #{dispatch.id}",
        f"Project: {dispatch.project_id}",
        f"Target agent: {dispatch.target_agent}",
        f"Status: {dispatch.status}",
        f"Trigger: {dispatch.trigger_type} #{dispatch.trigger_id}",
        f"Task: {dispatch.task_label}",
        f"Created: {dispatch.created_at}",
    ]
    if dispatch.expires_at:
        lines.append(f"Expires: {dispatch.expires_at}")
    if dispatch.summary:
        lines.extend(["", "Summary:", dispatch.summary])
    if dispatch.context_prompt:
        lines.extend(["", "Context prompt:", dispatch.context_prompt])
    else:
        lines.extend(["", "Context prompt:", "(none stored)"])
    return "\n".join(lines)


def build_single_handoff_text(
    dispatch: DispatchEntry,
    *,
    focused_window: KittyWindowSnapshot | None,
    prompt_copied: bool,
) -> str:
    notes = []
    if focused_window is not None:
        notes.append(f"Focused target window: {focused_window.title or f'id {focused_window.id}'}")
    else:
        notes.append("No matching managed agent window was found, so details are shown in the current window.")

    if prompt_copied:
        notes.append("The generated prompt was copied to the system clipboard for explicit handoff.")
    elif dispatch.context_prompt:
        notes.append("Clipboard copy failed. Use the prompt shown below for manual handoff.")
    else:
        notes.append("No generated prompt was stored for this dispatch.")

    return "\n".join(
        [
            "Dispatch approved.",
            "",
            *notes,
            "",
            format_dispatch_detail(dispatch),
        ]
    )


def build_batch_handoff_text(
    dispatches: list[DispatchEntry],
    *,
    focused_window: KittyWindowSnapshot | None,
) -> str:
    lines = [f"Approved {len(dispatches)} dispatches."]
    lines.append("")
    if focused_window is not None:
        lines.append(f"Focused window: {focused_window.title or f'id {focused_window.id}'}")
    else:
        lines.append("No matching managed agent window was found for the most recent approval.")
    lines.append("Prompts were not copied in batch mode. Let den-agent pick them up on next launch, or use the details below for manual handoff.")
    lines.append("")
    for dispatch in dispatches:
        lines.append(build_dispatch_row(dispatch, 200))
    lines.append("")
    lines.append("Most recent dispatch details:")
    lines.append("")
    lines.append(format_dispatch_detail(dispatches[-1]))
    return "\n".join(lines)


def find_target_window(dispatch: DispatchEntry, windows: list[KittyWindowSnapshot]) -> KittyWindowSnapshot | None:
    matching = [
        window for window in windows
        if window.user_vars.get("den_project") == dispatch.project_id
        and window.user_vars.get("den_agent") == dispatch.target_agent
    ]
    if not matching:
        return None

    exact_dispatch = [window for window in matching if window.user_vars.get("den_dispatch") == str(dispatch.id)]
    candidates = exact_dispatch or matching
    return sorted(candidates, key=lambda window: (not window.is_focused, not window.is_active, window.id))[0]


def deliver_handoff(result_data: dict[str, Any], bridge: SubprocessKittyBridge | None = None) -> None:
    approved_payload = result_data.get("approved")
    if not isinstance(approved_payload, list) or not approved_payload:
        return

    approved = [DispatchEntry.from_payload(item) for item in approved_payload if isinstance(item, dict)]
    if not approved:
        return

    bridge = bridge or SubprocessKittyBridge()
    windows = bridge.list_windows()
    target_window = find_target_window(approved[-1], windows)
    if target_window is not None:
        bridge.focus_window(target_window.id)

    if len(approved) == 1:
        prompt_copied = bool(approved[0].context_prompt) and bridge.copy_to_clipboard(approved[0].context_prompt or "")
        overlay_text = build_single_handoff_text(
            approved[0],
            focused_window=target_window,
            prompt_copied=prompt_copied,
        )
        bridge.launch_details_overlay(f"Dispatch #{approved[0].id}", overlay_text)
        return

    overlay_text = build_batch_handoff_text(approved, focused_window=target_window)
    bridge.launch_details_overlay("Approved dispatches", overlay_text)


def truncate_text(text: str, width: int) -> str:
    if width <= len(ELLIPSIS):
        return ELLIPSIS[:width]
    if len(text) <= width:
        return text
    return text[: width - len(ELLIPSIS)] + ELLIPSIS


def compute_visible_window(total_items: int, selected_index: int, visible_count: int) -> tuple[int, int]:
    if total_items <= 0 or visible_count <= 0:
        return 0, 0

    start = 0
    if selected_index >= visible_count:
        start = selected_index - visible_count + 1

    max_start = max(0, total_items - visible_count)
    start = max(0, min(start, max_start))
    end = min(total_items, start + visible_count)
    return start, end


def wrap_text_for_width(text: str, width: int) -> list[str]:
    usable_width = max(10, width)
    wrapped: list[str] = []
    for raw_line in text.splitlines() or [""]:
        expanded = raw_line.expandtabs(4)
        if not expanded:
            wrapped.append("")
            continue
        wrapped.extend(
            textwrap.wrap(
                expanded,
                width=usable_width,
                replace_whitespace=False,
                drop_whitespace=False,
            )
            or [""]
        )
    return wrapped


def load_details_text(details_file: str) -> str:
    path = pathlib.Path(details_file).resolve()
    tempdir = pathlib.Path(tempfile.gettempdir()).resolve()
    if not path.is_relative_to(tempdir) or not path.name.startswith("den-dispatch-"):
        raise ValueError(f"--details-file must be a den-dispatch temp file, got: {details_file}")
    try:
        return path.read_text(encoding="utf-8")
    finally:
        path.unlink(missing_ok=True)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Dispatch approval kitten for den-mcp")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="Base URL for the den-mcp server")
    parser.add_argument("--project", help="Optional project filter for pending dispatches")
    parser.add_argument("--decided-by", default=_default_approver(), help="Identity recorded as the approver/rejector")
    parser.add_argument("--details-file", help="Internal mode: show a details overlay for the provided file")
    parser.add_argument("--details-title", default="Dispatch details", help="Window title for details mode")
    return parser.parse_args(argv[1:])


@result_handler()
def handle_result(args: list[str], data: dict[str, Any], target_window_id: int, boss: Any) -> None:
    del args, target_window_id, boss
    if isinstance(data, dict):
        deliver_handoff(data)


def main(argv: list[str]) -> dict[str, Any] | None:
    options = parse_args(argv)

    if options.details_file:
        text = load_details_text(options.details_file)
        loop = Loop()
        handler = DetailViewer(options.details_title, text)
        loop.loop(handler)
        return None

    controller = ApprovalController(
        api=DenApiClient(options.base_url),
        decided_by=options.decided_by,
        project_id=options.project,
    )
    loop = Loop()
    handler = ApprovalHandler(controller)
    loop.loop(handler)
    return controller.result_payload()


def _normalize_optional_text(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    return text or None


def _extract_http_error_message(exc: urllib.error.HTTPError) -> str:
    try:
        payload = json.load(exc)
    except Exception:
        payload = None

    if isinstance(payload, dict):
        error = payload.get("error")
        if isinstance(error, str) and error.strip():
            return error

    return f"den-mcp returned HTTP {exc.code}"


def _default_approver() -> str:
    return os.environ.get("DEN_APPROVER") or os.environ.get("USER") or "user"


if __name__ == "__main__":
    main(sys.argv)
