from __future__ import annotations

import importlib.util
import pathlib
import sys
import unittest


MODULE_PATH = pathlib.Path(__file__).resolve().parents[1] / "den_layout.py"
SPEC = importlib.util.spec_from_file_location("den_layout", MODULE_PATH)
assert SPEC is not None
assert SPEC.loader is not None
den_layout = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = den_layout
SPEC.loader.exec_module(den_layout)


class FakeApi:
    def __init__(self, *, projects: dict[str, dict], documents: dict[tuple[str, str], dict | None]) -> None:
        self.projects = projects
        self.documents = documents

    def list_projects(self) -> list[dict]:
        return list(self.projects.values())

    def get_project(self, project_id: str) -> dict:
        project = self.projects[project_id]
        return {
            "project": project,
            "task_counts_by_status": {},
            "unread_message_count": 0,
        }

    def get_document(self, project_id: str, slug: str) -> dict | None:
        return self.documents.get((project_id, slug))


class FakeKittyController:
    def __init__(self, tabs: list[den_layout.TabSnapshot] | None = None) -> None:
        self.tabs = list(tabs or [])
        self.actions: list[tuple[str, int | None, str, str, str]] = []
        self._next_tab_id = max((tab.id for tab in self.tabs), default=0) + 1
        self._next_window_id = max((window.id for tab in self.tabs for window in tab.windows), default=0) + 1

    def list_tabs(self) -> list[den_layout.TabSnapshot]:
        return list(self.tabs)

    def launch_tab(self, layout: den_layout.ProjectLayout, plan: den_layout.WindowPlan, take_focus: bool = False) -> int:
        del take_focus
        tab_id = self._next_tab_id
        self._next_tab_id += 1
        window_id = self._next_window_id
        self._next_window_id += 1

        window = den_layout.WindowSnapshot(
            id=window_id,
            title=plan.title,
            user_vars={"den_project": layout.project_id, "den_agent": plan.agent},
        )
        self.tabs.append(den_layout.TabSnapshot(id=tab_id, title=layout.tab_title, windows=(window,)))
        self.actions.append(("launch_tab", tab_id, layout.project_id, plan.agent, layout.root_path))
        return window_id

    def launch_window(self, tab_id: int, project_id: str, root_path: str, plan: den_layout.WindowPlan,
                      split: bool = True, take_focus: bool = False) -> int:
        del split, take_focus
        window_id = self._next_window_id
        self._next_window_id += 1
        new_window = den_layout.WindowSnapshot(
            id=window_id,
            title=plan.title,
            user_vars={"den_project": project_id, "den_agent": plan.agent},
        )

        updated_tabs: list[den_layout.TabSnapshot] = []
        for tab in self.tabs:
            if tab.id == tab_id:
                updated_tabs.append(
                    den_layout.TabSnapshot(id=tab.id, title=tab.title, windows=tab.windows + (new_window,))
                )
            else:
                updated_tabs.append(tab)
        self.tabs = updated_tabs
        self.actions.append(("launch_window", tab_id, project_id, plan.agent, root_path))
        return window_id


class DenLayoutTests(unittest.TestCase):
    def make_api(self, *, routing_content: str | None = None) -> FakeApi:
        project = {
            "id": "den-mcp",
            "name": "den-mcp",
            "root_path": "/workspace/den-mcp",
            "description": "test",
            "created_at": "2026-04-12T00:00:00Z",
            "updated_at": "2026-04-12T00:00:00Z",
        }
        documents = {
            ("den-mcp", "dispatch-routing"): None if routing_content is None else {
                "project_id": "den-mcp",
                "slug": "dispatch-routing",
                "content": routing_content,
            }
        }
        return FakeApi(projects={"den-mcp": project}, documents=documents)

    def test_build_project_layout_prefers_standard_roles_and_wrapper_commands(self) -> None:
        api = self.make_api(
            routing_content="""
            {
              "roles": {
                "planner": "codex",
                "reviewer": "codex",
                "implementer": "claude-code"
              }
            }
            """
        )

        layout = den_layout.build_project_layout("den-mcp", api)

        self.assertEqual("den-mcp", layout.project_id)
        self.assertEqual("/workspace/den-mcp", layout.root_path)
        self.assertEqual(["claude-code", "codex"], [window.agent for window in layout.windows])
        self.assertEqual(
            (str(den_layout.repo_root() / "bin" / "den-agent"), "claude", "--project", "den-mcp"),
            layout.windows[0].command,
        )

    def test_render_session_outputs_split_layout_and_wrapper_commands(self) -> None:
        api = self.make_api()
        layout = den_layout.build_project_layout("den-mcp", api)

        session_text = den_layout.render_session([layout])

        self.assertIn("new_tab den-mcp", session_text)
        self.assertIn("layout splits", session_text)
        self.assertIn("launch --title claude-code --var den_project=den-mcp --var den_agent=claude-code --cwd /workspace/den-mcp", session_text)
        self.assertIn("launch --location=vsplit --title codex", session_text)
        self.assertIn("/home/patch/dev/den-mcp/bin/den-agent claude --project den-mcp", session_text)

    def test_apply_layout_reuses_existing_managed_window_and_only_launches_missing_agent(self) -> None:
        api = self.make_api()
        layout = den_layout.build_project_layout("den-mcp", api)
        existing_tab = den_layout.TabSnapshot(
            id=7,
            title="den-mcp",
            windows=(
                den_layout.WindowSnapshot(
                    id=11,
                    title="claude-code",
                    user_vars={"den_project": "den-mcp", "den_agent": "claude-code"},
                ),
            ),
        )
        kitty = FakeKittyController([existing_tab])

        first_actions = den_layout.apply_layout([layout], kitty)
        second_actions = den_layout.apply_layout([layout], kitty)

        self.assertEqual(
            [("launch_window", 7, "den-mcp", "codex", "/workspace/den-mcp")],
            kitty.actions,
        )
        self.assertTrue(any(action.action == "reused_window" and action.agent == "claude-code" for action in first_actions))
        self.assertTrue(any(action.action == "created_window" and action.agent == "codex" for action in first_actions))
        self.assertFalse(any(action.action == "created_window" for action in second_actions))

    def test_resolve_project_ids_uses_cwd_when_no_projects_are_supplied(self) -> None:
        api = self.make_api()
        resolved = den_layout.resolve_project_ids([], pathlib.Path("/workspace/den-mcp/src"), api)
        self.assertEqual(["den-mcp"], resolved)


if __name__ == "__main__":
    unittest.main()
