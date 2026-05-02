import type { AppAgentSelection } from '../electron/sidecarProtocol.ts';

export function normalizeAppAgentSelection(selection: AppAgentSelection): AppAgentSelection {
  return {
    project_id: selection.project_id ?? null,
    task_id: selection.task_id ?? null,
    workspace_id: selection.workspace_id ?? null,
    current_route: selection.current_route ?? null,
    current_tab: selection.current_tab ?? null,
    session_id: selection.session_id ?? null,
    selected_file_path: selection.selected_file_path ?? null,
    selected_diff_range: selection.selected_diff_range ?? null,
  };
}
