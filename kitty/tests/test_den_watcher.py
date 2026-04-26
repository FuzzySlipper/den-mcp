from __future__ import annotations

import importlib.util
import pathlib
import sys
import unittest
from concurrent.futures import Future


MODULE_PATH = pathlib.Path(__file__).resolve().parents[1] / "den_watcher.py"
SPEC = importlib.util.spec_from_file_location("den_watcher", MODULE_PATH)
assert SPEC is not None
assert SPEC.loader is not None
den_watcher = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = den_watcher
SPEC.loader.exec_module(den_watcher)


class FakeBoss:
    def __init__(self) -> None:
        self.commands: list[tuple[str, ...]] = []

    def call_remote_control(self, window: object, command: tuple[str, ...]) -> None:
        del window
        self.commands.append(command)


class FakeWindow:
    def __init__(self, window_id: int) -> None:
        self.id = window_id


class ImmediateExecutor:
    def submit(self, fn, *args, **kwargs):  # type: ignore[no-untyped-def]
        future: Future = Future()

        try:
            future.set_result(fn(*args, **kwargs))
        except Exception as ex:  # pragma: no cover - keeps parity with executor contract
            future.set_exception(ex)

        return future


class DenWatcherTests(unittest.TestCase):
    def setUp(self) -> None:
        self.notifications: list[tuple[str, str]] = []
        self.fetches: list[str] = []
        self.watcher = den_watcher.DenWatcher(
            base_url="http://example.test",
            executor=ImmediateExecutor(),
            notify_runner=lambda title, body: self.notifications.append((title, body)),
            dispatch_count_fetcher=self._fetch_count,
        )
        self.boss = FakeBoss()
        self.window = FakeWindow(7)

    def _fetch_count(self, project: str) -> int:
        self.fetches.append(project)
        return 3

    def test_partial_state_does_not_render_or_fetch(self) -> None:
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_agent", "value": "pi-reviewer"})
        self.assertEqual([], self.boss.commands)
        self.assertEqual([], self.fetches)

        other_window = FakeWindow(8)
        self.watcher.on_set_user_var(self.boss, other_window, {"key": "den_project", "value": "den-mcp"})
        self.assertEqual([], self.boss.commands)
        self.assertEqual([], self.fetches)

    def test_on_set_user_var_updates_titles_and_colors(self) -> None:
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_agent", "value": "pi-reviewer"})
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_project", "value": "den-mcp"})
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_task", "value": "560"})
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_status", "value": "working"})

        joined = [" ".join(command) for command in self.boss.commands]
        self.assertTrue(any("set-window-title" in command and "pi-reviewer · den-mcp · #560 · working" in command for command in joined))
        self.assertTrue(any("set-tab-title" in command and "den-mcp · pi-reviewer · working · #560 · 3 pending" in command for command in joined))
        self.assertTrue(any("set-tab-color" in command and "active_tab_background=#1e66f5" in command for command in joined))

    def test_on_set_user_var_sends_notification_only_for_notable_transition(self) -> None:
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_agent", "value": "pi-reviewer"})
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_project", "value": "den-mcp"})
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_task", "value": "560"})
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_status", "value": "working"})
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_status", "value": "done"})
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_status", "value": "done"})

        self.assertEqual(1, len(self.notifications))
        title, body = self.notifications[0]
        self.assertEqual("den-mcp: pi-reviewer is done", title)
        self.assertIn("task #560", body)

    def test_project_fetch_is_scheduled_for_dispatch_related_updates(self) -> None:
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_agent", "value": "pi-reviewer"})
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_project", "value": "den-mcp"})
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_status", "value": "waiting"})

        self.assertEqual(["den-mcp", "den-mcp"], self.fetches)
        self.assertEqual(3, self.watcher._project_pending_counts["den-mcp"])

    def test_on_close_clears_project_cache_when_last_window_closes(self) -> None:
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_agent", "value": "pi-reviewer"})
        self.watcher.on_set_user_var(self.boss, self.window, {"key": "den_project", "value": "den-mcp"})
        self.assertEqual(3, self.watcher._project_pending_counts["den-mcp"])

        self.watcher.on_close(self.boss, self.window, {})

        self.assertNotIn(7, self.watcher._window_states)
        self.assertNotIn("den-mcp", self.watcher._project_pending_counts)


if __name__ == "__main__":
    unittest.main()
