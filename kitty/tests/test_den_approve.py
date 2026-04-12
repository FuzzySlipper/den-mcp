from __future__ import annotations

import importlib.util
import pathlib
import sys
import tempfile
import unittest


MODULE_PATH = pathlib.Path(__file__).resolve().parents[1] / "den_approve.py"
SPEC = importlib.util.spec_from_file_location("den_approve", MODULE_PATH)
assert SPEC is not None
assert SPEC.loader is not None
den_approve = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = den_approve
SPEC.loader.exec_module(den_approve)


class FakeApi:
    def __init__(self, dispatches: list[dict]) -> None:
        self.dispatches = [den_approve.DispatchEntry.from_payload(item) for item in dispatches]
        self.approvals: list[tuple[int, str]] = []
        self.rejections: list[tuple[int, str]] = []

    def list_pending_dispatches(self, project_id: str | None = None) -> list[den_approve.DispatchEntry]:
        if project_id is None:
            return list(self.dispatches)
        return [entry for entry in self.dispatches if entry.project_id == project_id]

    def approve_dispatch(self, dispatch_id: int, decided_by: str) -> den_approve.DispatchEntry:
        self.approvals.append((dispatch_id, decided_by))
        entry = next(entry for entry in self.dispatches if entry.id == dispatch_id)
        self.dispatches = [candidate for candidate in self.dispatches if candidate.id != dispatch_id]
        return den_approve.DispatchEntry(
            id=entry.id,
            project_id=entry.project_id,
            target_agent=entry.target_agent,
            status="approved",
            trigger_type=entry.trigger_type,
            trigger_id=entry.trigger_id,
            task_id=entry.task_id,
            summary=entry.summary,
            context_prompt=entry.context_prompt,
            dedup_key=entry.dedup_key,
            created_at=entry.created_at,
            expires_at=entry.expires_at,
            decided_by=decided_by,
        )

    def reject_dispatch(self, dispatch_id: int, decided_by: str) -> den_approve.DispatchEntry:
        self.rejections.append((dispatch_id, decided_by))
        entry = next(entry for entry in self.dispatches if entry.id == dispatch_id)
        self.dispatches = [candidate for candidate in self.dispatches if candidate.id != dispatch_id]
        return den_approve.DispatchEntry(
            id=entry.id,
            project_id=entry.project_id,
            target_agent=entry.target_agent,
            status="rejected",
            trigger_type=entry.trigger_type,
            trigger_id=entry.trigger_id,
            task_id=entry.task_id,
            summary=entry.summary,
            context_prompt=entry.context_prompt,
            dedup_key=entry.dedup_key,
            created_at=entry.created_at,
            expires_at=entry.expires_at,
            decided_by=decided_by,
        )


class FakeBridge:
    def __init__(self, windows: list[den_approve.KittyWindowSnapshot] | None = None) -> None:
        self.windows = list(windows or [])
        self.focused: list[int] = []
        self.clipboard: list[str] = []
        self.overlays: list[tuple[str, str]] = []

    def list_windows(self) -> list[den_approve.KittyWindowSnapshot]:
        return list(self.windows)

    def focus_window(self, window_id: int) -> bool:
        self.focused.append(window_id)
        return True

    def copy_to_clipboard(self, text: str) -> bool:
        self.clipboard.append(text)
        return True

    def launch_details_overlay(self, title: str, text: str) -> bool:
        self.overlays.append((title, text))
        return True


class DenApproveTests(unittest.TestCase):
    def make_dispatch(self, dispatch_id: int, *, project_id: str = "den-mcp", target_agent: str = "claude-code") -> dict:
        return {
            "id": dispatch_id,
            "project_id": project_id,
            "target_agent": target_agent,
            "status": "pending",
            "trigger_type": "message",
            "trigger_id": dispatch_id * 10,
            "task_id": 563,
            "summary": f"Dispatch summary {dispatch_id}",
            "context_prompt": f"Prompt {dispatch_id}",
            "dedup_key": f"message:{dispatch_id * 10}:{target_agent}",
            "created_at": "2026-04-12T00:00:00Z",
            "expires_at": "2026-04-13T00:00:00Z",
        }

    def test_controller_approves_and_removes_selected_dispatch(self) -> None:
        api = FakeApi([self.make_dispatch(41), self.make_dispatch(42)])
        controller = den_approve.ApprovalController(api, decided_by="george")

        controller.refresh()
        approved = controller.approve_selected()

        self.assertTrue(approved)
        self.assertEqual([(41, "george")], api.approvals)
        self.assertEqual([42], [entry.id for entry in controller.dispatches])
        self.assertEqual([41], [entry.id for entry in controller.approved])

    def test_controller_rejects_selected_dispatch(self) -> None:
        api = FakeApi([self.make_dispatch(41), self.make_dispatch(42)])
        controller = den_approve.ApprovalController(api, decided_by="george")

        controller.refresh()
        controller.move_selection(1)
        rejected = controller.reject_selected()

        self.assertTrue(rejected)
        self.assertEqual([(42, "george")], api.rejections)
        self.assertEqual([41], [entry.id for entry in controller.dispatches])
        self.assertEqual([42], [entry.id for entry in controller.rejected])

    def test_controller_approve_all_marks_batch_and_clears_queue(self) -> None:
        api = FakeApi([self.make_dispatch(41), self.make_dispatch(42)])
        controller = den_approve.ApprovalController(api, decided_by="george")

        controller.refresh()
        result = controller.approve_all_remaining()

        self.assertTrue(result)
        self.assertTrue(controller.approved_all)
        self.assertEqual([], controller.dispatches)
        self.assertEqual([41, 42], [entry.id for entry in controller.approved])

    def test_find_target_window_prefers_exact_dispatch_then_active_window(self) -> None:
        dispatch = den_approve.DispatchEntry.from_payload(self.make_dispatch(41))
        windows = [
            den_approve.KittyWindowSnapshot(
                id=11,
                tab_id=1,
                title="older",
                is_active=False,
                is_focused=False,
                user_vars={"den_project": "den-mcp", "den_agent": "claude-code"},
            ),
            den_approve.KittyWindowSnapshot(
                id=12,
                tab_id=1,
                title="exact",
                is_active=False,
                is_focused=False,
                user_vars={"den_project": "den-mcp", "den_agent": "claude-code", "den_dispatch": "41"},
            ),
            den_approve.KittyWindowSnapshot(
                id=13,
                tab_id=1,
                title="active",
                is_active=True,
                is_focused=False,
                user_vars={"den_project": "den-mcp", "den_agent": "claude-code"},
            ),
        ]

        resolved = den_approve.find_target_window(dispatch, windows)

        self.assertIsNotNone(resolved)
        assert resolved is not None
        self.assertEqual(12, resolved.id)

    def test_deliver_handoff_focuses_target_copies_prompt_and_opens_overlay_for_single_dispatch(self) -> None:
        bridge = FakeBridge(
            [
                den_approve.KittyWindowSnapshot(
                    id=55,
                    tab_id=7,
                    title="claude-code",
                    is_active=True,
                    is_focused=True,
                    user_vars={"den_project": "den-mcp", "den_agent": "claude-code"},
                )
            ]
        )
        payload = {"approved": [self.make_dispatch(41)], "approved_all": False}

        den_approve.deliver_handoff(payload, bridge)

        self.assertEqual([55], bridge.focused)
        self.assertEqual(["Prompt 41"], bridge.clipboard)
        self.assertEqual(1, len(bridge.overlays))
        self.assertEqual("Dispatch #41", bridge.overlays[0][0])
        self.assertIn("Dispatch approved.", bridge.overlays[0][1])

    def test_deliver_handoff_uses_batch_overlay_without_copy_for_multiple_dispatches(self) -> None:
        bridge = FakeBridge(
            [
                den_approve.KittyWindowSnapshot(
                    id=60,
                    tab_id=8,
                    title="codex",
                    is_active=True,
                    is_focused=True,
                    user_vars={"den_project": "den-mcp", "den_agent": "codex"},
                )
            ]
        )
        payload = {
            "approved": [
                self.make_dispatch(41, target_agent="claude-code"),
                self.make_dispatch(42, target_agent="codex"),
            ],
            "approved_all": True,
        }

        den_approve.deliver_handoff(payload, bridge)

        self.assertEqual([60], bridge.focused)
        self.assertEqual([], bridge.clipboard)
        self.assertEqual([("Approved dispatches", bridge.overlays[0][1])], bridge.overlays)
        self.assertIn("Approved 2 dispatches.", bridge.overlays[0][1])

    def test_wrap_text_for_width_preserves_blank_lines(self) -> None:
        wrapped = den_approve.wrap_text_for_width("one\n\ntwo", 8)
        self.assertEqual(["one", "", "two"], wrapped)

    def test_compute_visible_window_scrolls_with_selection(self) -> None:
        self.assertEqual((0, 3), den_approve.compute_visible_window(5, 0, 3))
        self.assertEqual((1, 4), den_approve.compute_visible_window(5, 3, 3))
        self.assertEqual((2, 5), den_approve.compute_visible_window(5, 4, 3))

    def test_load_details_text_only_deletes_den_dispatch_tempfiles(self) -> None:
        with tempfile.NamedTemporaryFile(
            "w",
            delete=False,
            encoding="utf-8",
            prefix="den-dispatch-",
            suffix=".txt",
        ) as handle:
            handle.write("detail text")
            safe_path = pathlib.Path(handle.name)

        loaded = den_approve.load_details_text(str(safe_path))

        self.assertEqual("detail text", loaded)
        self.assertFalse(safe_path.exists())

    def test_load_details_text_rejects_non_dispatch_paths_without_deleting_them(self) -> None:
        with tempfile.NamedTemporaryFile(
            "w",
            delete=False,
            encoding="utf-8",
            prefix="notes-",
            suffix=".txt",
        ) as handle:
            handle.write("keep me")
            unsafe_path = pathlib.Path(handle.name)

        with self.assertRaisesRegex(ValueError, "--details-file must be a den-dispatch temp file"):
            den_approve.load_details_text(str(unsafe_path))

        self.assertTrue(unsafe_path.exists())
        unsafe_path.unlink(missing_ok=True)


if __name__ == "__main__":
    unittest.main()
