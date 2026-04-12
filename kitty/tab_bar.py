#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import Executor, Future, ThreadPoolExecutor
from dataclasses import dataclass
from typing import TYPE_CHECKING, Any, Callable, Iterable

if TYPE_CHECKING:
    from kitty.boss import Boss
    from kitty.fast_data_types import Screen
    from kitty.tab_bar import DrawData, ExtraData, TabBarData
else:
    Boss = Any
    Screen = Any
    DrawData = Any
    ExtraData = Any
    TabBarData = Any

try:
    from kitty.boss import get_boss as _get_boss
except ModuleNotFoundError:
    def _get_boss() -> Boss | None:
        return None


ELLIPSIS = "..."
STATUS_STYLE = {
    "idle": {"label": "idle", "fg": 0xC6D0F5, "bg": 0x303446},
    "working": {"label": "working", "fg": 0xEFF1F5, "bg": 0x1E66F5},
    "reviewing": {"label": "review", "fg": 0x1E1E2E, "bg": 0xDF8E1D},
    "waiting": {"label": "waiting", "fg": 0x1E1E2E, "bg": 0xEA76CB},
    "done": {"label": "done", "fg": 0xEFF1F5, "bg": 0x40A02B},
    "error": {"label": "error", "fg": 0xEFF1F5, "bg": 0xD20F39},
}
INACTIVE_BG = 0x414559
INACTIVE_FG = 0xC6D0F5
ACTIVE_BG = 0x1E2030
ACTIVE_FG = 0xEFF1F5


@dataclass(frozen=True)
class TabStateSnapshot:
    project: str
    agent: str
    status: str | None
    task_id: str | None
    dispatch_id: str | None
    pending_dispatch_count: int | None


@dataclass(frozen=True)
class DispatchCountEntry:
    count: int
    fetched_at: float


class DispatchCountCache:
    def __init__(
        self,
        base_url: str | None = None,
        ttl_seconds: float = 10.0,
        max_entries: int = 64,
        executor: Executor | None = None,
        fetcher: Callable[[str], int | None] | None = None,
    ) -> None:
        self._base_url = (base_url or _default_base_url()).rstrip("/")
        self._ttl_seconds = ttl_seconds
        self._max_entries = max_entries
        self._executor = executor or ThreadPoolExecutor(max_workers=2, thread_name_prefix="den-tab-bar")
        self._fetcher = fetcher or self._fetch_pending_dispatch_count
        self._lock = threading.Lock()
        self._entries: dict[str, DispatchCountEntry] = {}
        self._in_flight: set[str] = set()

    def get(self, project: str | None) -> int | None:
        if not project:
            return None

        now = time.monotonic()
        should_fetch = False

        with self._lock:
            entry = self._entries.get(project)
            is_stale = entry is None or (now - entry.fetched_at) >= self._ttl_seconds
            if is_stale and project not in self._in_flight:
                self._in_flight.add(project)
                should_fetch = True
            cached_count = entry.count if entry is not None else None

        if should_fetch:
            try:
                future = self._executor.submit(self._fetcher, project)
            except Exception:
                with self._lock:
                    self._in_flight.discard(project)
                return cached_count
            future.add_done_callback(lambda completed: self._store(project, completed))

        return cached_count

    def shutdown(self) -> None:
        if isinstance(self._executor, ThreadPoolExecutor):
            self._executor.shutdown(wait=False, cancel_futures=True)

    def _store(self, project: str, future: Future[int | None]) -> None:
        try:
            count = future.result()
        except Exception:
            count = None

        with self._lock:
            self._in_flight.discard(project)
            if count is None:
                return
            self._entries[project] = DispatchCountEntry(count=count, fetched_at=time.monotonic())
            while len(self._entries) > self._max_entries:
                oldest = next(iter(self._entries))
                self._entries.pop(oldest, None)

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


def draw_tab(
    draw_data: DrawData,
    screen: Screen,
    tab: TabBarData,
    before: int,
    max_title_length: int,
    index: int,
    is_last: bool,
    extra_data: ExtraData,
) -> int:
    del before, extra_data

    snapshot = resolve_tab_snapshot(tab, watcher=_WATCHER, count_cache=_COUNT_CACHE, boss_getter=_get_boss)
    text = build_tab_text(tab, index, snapshot)

    if max_title_length > 0:
        text = truncate_text(text, max_title_length)

    fg, bg = resolve_tab_colors(tab, snapshot)
    return render_tab_text(draw_data, screen, text, fg, bg, is_last)


def resolve_tab_snapshot(
    tab: Any,
    watcher: Any,
    count_cache: DispatchCountCache,
    boss_getter: Callable[[], Any] = lambda: None,
) -> TabStateSnapshot | None:
    real_tab = resolve_real_tab(tab, boss_getter)
    if real_tab is None:
        return None

    windows = list(iter_tab_windows(real_tab))
    if not windows:
        return None

    active_window_id = resolve_active_window_id(real_tab)
    managed_states = []
    for window in windows:
        window_id = getattr(window, "id", None)
        if not isinstance(window_id, int):
            continue

        state = watcher.get_window_state(window_id)
        if state and state.is_managed:
            managed_states.append(state)

    if not managed_states:
        return None

    chosen = next((state for state in managed_states if state.window_id == active_window_id), managed_states[0])
    pending_dispatch_count = count_cache.get(chosen.project)
    return TabStateSnapshot(
        project=chosen.project or "project",
        agent=chosen.agent or "agent",
        status=chosen.status,
        task_id=chosen.task_id,
        dispatch_id=chosen.dispatch_id,
        pending_dispatch_count=pending_dispatch_count,
    )


def build_tab_text(tab: Any, index: int, snapshot: TabStateSnapshot | None) -> str:
    if snapshot is None:
        return fallback_tab_title(tab, index)

    parts = [snapshot.project, snapshot.agent]
    if snapshot.status:
        parts.append(STATUS_STYLE.get(snapshot.status, {"label": snapshot.status})["label"])
    if snapshot.task_id:
        parts.append(f"#{snapshot.task_id}")
    if snapshot.pending_dispatch_count and snapshot.pending_dispatch_count > 0:
        parts.append(f"{snapshot.pending_dispatch_count} pending")

    return " | ".join(parts)


def resolve_tab_colors(tab: Any, snapshot: TabStateSnapshot | None) -> tuple[int, int]:
    is_active = bool(getattr(tab, "is_active", False))

    if snapshot is None or not snapshot.status:
        return (ACTIVE_FG, ACTIVE_BG) if is_active else (INACTIVE_FG, INACTIVE_BG)

    style = STATUS_STYLE.get(snapshot.status)
    if style is None:
        return (ACTIVE_FG, ACTIVE_BG) if is_active else (INACTIVE_FG, INACTIVE_BG)

    if is_active:
        return style["fg"], style["bg"]
    return style["fg"], INACTIVE_BG


def render_tab_text(draw_data: Any, screen: Any, text: str, fg: int, bg: int, is_last: bool) -> int:
    leading = int(getattr(draw_data, "leading_spaces", 0) or 0)
    trailing = int(getattr(draw_data, "trailing_spaces", 0) or 0)

    if leading > 0:
        screen.draw(" " * leading)

    original_fg = getattr(screen.cursor, "fg", None)
    original_bg = getattr(screen.cursor, "bg", None)

    screen.cursor.fg = fg
    screen.cursor.bg = bg
    screen.draw(text)

    if trailing > 0:
        screen.draw(" " * trailing)
    if not is_last:
        screen.draw(" ")

    screen.cursor.fg = original_fg
    screen.cursor.bg = original_bg
    return getattr(screen.cursor, "x", 0)


def fallback_tab_title(tab: Any, index: int) -> str:
    title = str(getattr(tab, "title", "") or "").strip()
    return title or f"Tab {index}"


def truncate_text(text: str, max_length: int) -> str:
    if max_length <= 0:
        return ""
    if len(text) <= max_length:
        return text
    if max_length <= len(ELLIPSIS):
        return ELLIPSIS[:max_length]
    return text[: max_length - len(ELLIPSIS)] + ELLIPSIS


def resolve_real_tab(tab: Any, boss_getter: Callable[[], Any]) -> Any | None:
    boss = boss_getter()
    if boss is None:
        return None

    tab_id = resolve_tab_id(tab)
    if tab_id is None:
        return None

    for method_name in ("tab_for_id", "tab_by_id"):
        method = getattr(boss, method_name, None)
        if callable(method):
            try:
                resolved = method(tab_id)
            except Exception:
                resolved = None
            if resolved is not None:
                return resolved

    active_tab_manager = getattr(boss, "active_tab_manager", None)
    tabs = getattr(active_tab_manager, "tabs", None)
    if tabs:
        for candidate in tabs:
            if getattr(candidate, "id", None) == tab_id:
                return candidate

    all_tabs = getattr(boss, "all_tabs", None)
    if callable(all_tabs):
        try:
            for candidate in all_tabs():
                if getattr(candidate, "id", None) == tab_id:
                    return candidate
        except Exception:
            return None

    return None


def resolve_tab_id(tab: Any) -> int | None:
    for attr in ("tab_id", "id"):
        value = getattr(tab, attr, None)
        if isinstance(value, int):
            return value
    return None


def resolve_active_window_id(tab: Any) -> int | None:
    active_window = getattr(tab, "active_window", None)
    if active_window is not None:
        active_id = getattr(active_window, "id", None)
        if isinstance(active_id, int):
            return active_id

    active_window_id = getattr(tab, "active_window_id", None)
    if isinstance(active_window_id, int):
        return active_window_id

    return None


def iter_tab_windows(tab: Any) -> Iterable[Any]:
    for attr in ("windows", "all_windows", "window_list"):
        value = getattr(tab, attr, None)
        if value is None:
            continue
        if callable(value):
            try:
                value = value()
            except Exception:
                continue
        if value:
            return list(value)
    return []


def _default_base_url() -> str:
    return (
        os.environ.get("DEN_MCP_URL")
        or os.environ.get("DEN_MCP_BASE_URL")
        or "http://127.0.0.1:5199"
    )


def _load_den_watcher() -> Any:
    if "den_watcher" in sys.modules:
        return sys.modules["den_watcher"]

    module_path = pathlib.Path(__file__).with_name("den_watcher.py")
    spec = importlib.util.spec_from_file_location("den_watcher", module_path)
    if spec is None or spec.loader is None:
        raise RuntimeError("Unable to load den_watcher.py")

    module = importlib.util.module_from_spec(spec)
    sys.modules["den_watcher"] = module
    spec.loader.exec_module(module)
    return module


_WATCHER_MODULE = _load_den_watcher()
_WATCHER = _WATCHER_MODULE._WATCHER
_COUNT_CACHE = DispatchCountCache()
