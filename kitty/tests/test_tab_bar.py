from __future__ import annotations

import importlib.util
import pathlib
import sys
import unittest
from concurrent.futures import Future
from dataclasses import dataclass


MODULE_PATH = pathlib.Path(__file__).resolve().parents[1] / "tab_bar.py"
SPEC = importlib.util.spec_from_file_location("tab_bar", MODULE_PATH)
assert SPEC is not None
assert SPEC.loader is not None
tab_bar = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = tab_bar
SPEC.loader.exec_module(tab_bar)


class ImmediateExecutor:
    def submit(self, fn, *args, **kwargs):  # type: ignore[no-untyped-def]
        future: Future = Future()
        try:
            future.set_result(fn(*args, **kwargs))
        except Exception as ex:  # pragma: no cover
            future.set_exception(ex)
        return future


@dataclass
class FakeState:
    window_id: int
    agent: str | None = None
    project: str | None = None
    status: str | None = None
    task_id: str | None = None
    dispatch_id: str | None = None
    is_managed: bool = True


class FakeWatcher:
    def __init__(self, states: dict[int, FakeState]) -> None:
        self.states = states

    def get_window_state(self, window_id: int) -> FakeState | None:
        return self.states.get(window_id)


class FakeCountCache:
    def __init__(self, values: dict[str, int | None]) -> None:
        self.values = values
        self.projects: list[str] = []

    def get(self, project: str | None) -> int | None:
        if project is not None:
            self.projects.append(project)
        return self.values.get(project or "")


@dataclass
class FakeWindow:
    id: int


@dataclass
class FakeRealTab:
    id: int
    windows: list[FakeWindow]
    active_window_id: int | None = None


class FakeBoss:
    def __init__(self, tab: FakeRealTab) -> None:
        self.tab = tab

    def tab_for_id(self, tab_id: int) -> FakeRealTab | None:
        return self.tab if self.tab.id == tab_id else None


@dataclass
class FakeTabData:
    tab_id: int
    title: str = ""
    is_active: bool = True


class FakeCursor:
    def __init__(self) -> None:
        self.fg: int | None = None
        self.bg: int | None = None
        self.x = 0


class FakeScreen:
    def __init__(self) -> None:
        self.cursor = FakeCursor()
        self.buffer = ""

    def draw(self, text: str) -> None:
        self.buffer += text
        self.cursor.x += len(text)


@dataclass
class FakeDrawData:
    leading_spaces: int = 1
    trailing_spaces: int = 1


class TabBarTests(unittest.TestCase):
    def test_dispatch_count_cache_fetches_in_background(self) -> None:
        fetches: list[str] = []

        def fetcher(project: str) -> int:
            fetches.append(project)
            return 4

        cache = tab_bar.DispatchCountCache(
            base_url="http://example.test",
            ttl_seconds=10.0,
            executor=ImmediateExecutor(),
            fetcher=fetcher,
        )

        self.assertIsNone(cache.get("den-mcp"))
        self.assertEqual(4, cache.get("den-mcp"))
        self.assertEqual(["den-mcp"], fetches)

    def test_resolve_tab_snapshot_prefers_active_managed_window(self) -> None:
        real_tab = FakeRealTab(id=11, windows=[FakeWindow(1), FakeWindow(2)], active_window_id=2)
        watcher = FakeWatcher({
            1: FakeState(window_id=1, agent="claude-code", project="quillforge", status="working", task_id="10"),
            2: FakeState(window_id=2, agent="codex", project="den-mcp", status="reviewing", task_id="561"),
        })
        count_cache = FakeCountCache({"den-mcp": 3})

        snapshot = tab_bar.resolve_tab_snapshot(
            FakeTabData(tab_id=11),
            watcher=watcher,
            count_cache=count_cache,
            boss_getter=lambda: FakeBoss(real_tab),
        )

        self.assertIsNotNone(snapshot)
        assert snapshot is not None
        self.assertEqual("den-mcp", snapshot.project)
        self.assertEqual("codex", snapshot.agent)
        self.assertEqual("561", snapshot.task_id)
        self.assertEqual(3, snapshot.pending_dispatch_count)
        self.assertEqual(["den-mcp"], count_cache.projects)

    def test_build_tab_text_and_colors_for_managed_tab(self) -> None:
        snapshot = tab_bar.TabStateSnapshot(
            project="den-mcp",
            agent="codex",
            status="reviewing",
            task_id="561",
            dispatch_id=None,
            pending_dispatch_count=2,
        )

        text = tab_bar.build_tab_text(FakeTabData(tab_id=1, title="Fallback"), 0, snapshot)
        fg, bg = tab_bar.resolve_tab_colors(FakeTabData(tab_id=1, is_active=True), snapshot)

        self.assertEqual("den-mcp | codex | review | #561 | 2 pending", text)
        self.assertEqual(tab_bar.STATUS_STYLE["reviewing"]["fg"], fg)
        self.assertEqual(tab_bar.STATUS_STYLE["reviewing"]["bg"], bg)

    def test_draw_tab_falls_back_for_unmanaged_tab(self) -> None:
        original_watcher = tab_bar._WATCHER
        original_cache = tab_bar._COUNT_CACHE
        original_boss = tab_bar._get_boss
        screen = FakeScreen()

        try:
            tab_bar._WATCHER = FakeWatcher({})
            tab_bar._COUNT_CACHE = FakeCountCache({})
            tab_bar._get_boss = lambda: FakeBoss(FakeRealTab(id=3, windows=[]))
            tab_bar.draw_tab(FakeDrawData(), screen, FakeTabData(tab_id=3, title="Shell"), 0, 20, 0, False, object())
        finally:
            tab_bar._WATCHER = original_watcher
            tab_bar._COUNT_CACHE = original_cache
            tab_bar._get_boss = original_boss

        self.assertIn("Shell", screen.buffer)

    def test_truncate_text_adds_ellipsis(self) -> None:
        self.assertEqual("den-m...", tab_bar.truncate_text("den-mcp | codex", 8))


if __name__ == "__main__":
    unittest.main()
