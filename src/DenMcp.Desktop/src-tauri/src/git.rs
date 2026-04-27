use std::path::Path;
use std::process::Command;

use chrono::Utc;
use serde::{Deserialize, Serialize};

use crate::den_client::{AgentWorkspace, Project};
use crate::settings::OperatorSettings;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GitScope {
    pub project_id: String,
    pub project_name: Option<String>,
    pub task_id: Option<i64>,
    pub workspace_id: Option<String>,
    pub root_path: String,
    pub source_kind: String,
}

impl GitScope {
    fn key(&self) -> String {
        format!(
            "{}:{}:{}",
            self.project_id,
            self.workspace_id.clone().unwrap_or_default(),
            self.root_path
        )
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct DesktopGitSnapshotRequest {
    pub task_id: Option<i64>,
    pub workspace_id: Option<String>,
    pub root_path: String,
    pub state: DesktopSnapshotState,
    pub branch: Option<String>,
    pub is_detached: bool,
    pub head_sha: Option<String>,
    pub upstream: Option<String>,
    pub ahead: Option<i64>,
    pub behind: Option<i64>,
    pub dirty_counts: GitDirtyCounts,
    pub changed_files: Vec<GitFileStatus>,
    pub warnings: Vec<String>,
    pub truncated: bool,
    pub source_instance_id: String,
    pub source_display_name: Option<String>,
    pub observed_at: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalGitSnapshot {
    pub scope: GitScope,
    pub request: DesktopGitSnapshotRequest,
    pub last_publish_status: String,
    pub last_publish_error: Option<String>,
    pub last_published_at: Option<String>,
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum DesktopSnapshotState {
    Ok,
    PathNotVisible,
    NotGitRepository,
    GitError,
    SourceOffline,
    Missing,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct GitDirtyCounts {
    pub total: i64,
    pub staged: i64,
    pub unstaged: i64,
    pub untracked: i64,
    pub modified: i64,
    pub added: i64,
    pub deleted: i64,
    pub renamed: i64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct GitFileStatus {
    pub path: String,
    pub old_path: Option<String>,
    pub index_status: Option<String>,
    pub worktree_status: Option<String>,
    pub category: String,
    pub is_untracked: bool,
}

pub fn build_git_scopes(projects: &[Project], workspaces: &[AgentWorkspace]) -> Vec<GitScope> {
    let mut scopes = Vec::new();
    let mut seen = std::collections::HashSet::new();

    for project in projects {
        if let Some(root_path) = project.root_path.as_ref().and_then(|value| trim_to_option(value)) {
            let scope = GitScope {
                project_id: project.id.clone(),
                project_name: Some(project.name.clone()),
                task_id: None,
                workspace_id: None,
                root_path,
                source_kind: "project_root".to_string(),
            };
            if seen.insert(scope.key()) {
                scopes.push(scope);
            }
        }
    }

    for workspace in workspaces {
        if matches!(workspace.state.as_str(), "archived" | "complete" | "failed") {
            continue;
        }
        if let Some(root_path) = trim_to_option(&workspace.worktree_path) {
            let scope = GitScope {
                project_id: workspace.project_id.clone(),
                project_name: None,
                task_id: Some(workspace.task_id),
                workspace_id: Some(workspace.id.clone()),
                root_path,
                source_kind: "agent_workspace".to_string(),
            };
            if seen.insert(scope.key()) {
                scopes.push(scope);
            }
        }
    }

    scopes
}

pub fn inspect_scope(scope: &GitScope, settings: &OperatorSettings) -> LocalGitSnapshot {
    let mut warnings = Vec::new();
    let observed_at = Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true);

    let request = if !Path::new(&scope.root_path).is_dir() {
        warnings.push(format!("Path is not visible on this machine: {}", scope.root_path));
        base_request(scope, settings, observed_at, DesktopSnapshotState::PathNotVisible, warnings)
    } else {
        match inspect_visible_git_scope(scope, settings, observed_at.clone(), settings.max_changed_files) {
            Ok(snapshot) => snapshot,
            Err(error) => {
                warnings.push(error);
                base_request(scope, settings, observed_at, DesktopSnapshotState::GitError, warnings)
            }
        }
    };

    LocalGitSnapshot {
        scope: scope.clone(),
        request,
        last_publish_status: "pending".to_string(),
        last_publish_error: None,
        last_published_at: None,
    }
}

fn inspect_visible_git_scope(
    scope: &GitScope,
    settings: &OperatorSettings,
    observed_at: String,
    max_changed_files: usize,
) -> Result<DesktopGitSnapshotRequest, String> {
    let status = run_git(&scope.root_path, &["status", "--porcelain=v2", "--branch", "--untracked-files=all"])?;
    if status.exit_code != 0 {
        let state = if status.stderr.contains("not a git repository") {
            DesktopSnapshotState::NotGitRepository
        } else {
            DesktopSnapshotState::GitError
        };
        let mut request = base_request(
            scope,
            settings,
            observed_at,
            state,
            vec![format_git_error("git status", &status)],
        );
        request.truncated = status.truncated;
        return Ok(request);
    }

    let mut request = parse_porcelain_v2(scope, settings, observed_at, &status.stdout, max_changed_files);
    request.truncated = status.truncated || request.changed_files.len() >= max_changed_files;
    request.warnings.extend(status.warnings);
    if request.upstream.is_none() {
        request.warnings.push("No upstream branch reported by git status.".to_string());
    }
    if request.is_detached {
        request.warnings.push("Repository is in detached HEAD state.".to_string());
    }
    Ok(request)
}

fn base_request(
    scope: &GitScope,
    settings: &OperatorSettings,
    observed_at: String,
    state: DesktopSnapshotState,
    warnings: Vec<String>,
) -> DesktopGitSnapshotRequest {
    DesktopGitSnapshotRequest {
        task_id: scope.task_id,
        workspace_id: scope.workspace_id.clone(),
        root_path: scope.root_path.clone(),
        state,
        branch: None,
        is_detached: false,
        head_sha: None,
        upstream: None,
        ahead: None,
        behind: None,
        dirty_counts: GitDirtyCounts::default(),
        changed_files: Vec::new(),
        warnings,
        truncated: false,
        source_instance_id: settings.source_instance_id.clone(),
        source_display_name: settings.source_display_name.clone(),
        observed_at,
    }
}

fn parse_porcelain_v2(
    scope: &GitScope,
    settings: &OperatorSettings,
    observed_at: String,
    output: &str,
    max_changed_files: usize,
) -> DesktopGitSnapshotRequest {
    let mut request = base_request(scope, settings, observed_at, DesktopSnapshotState::Ok, Vec::new());

    for raw_line in output.lines() {
        let line = raw_line.trim_end_matches('\r');
        if let Some(header) = line.strip_prefix("# ") {
            parse_branch_header(&mut request, header);
            continue;
        }

        if request.changed_files.len() >= max_changed_files {
            request.truncated = true;
            continue;
        }

        if let Some(file) = parse_porcelain_file(line) {
            request.changed_files.push(file);
        }
    }

    request.dirty_counts = count_dirty(&request.changed_files);
    request
}

fn parse_branch_header(request: &mut DesktopGitSnapshotRequest, header: &str) {
    if let Some(value) = header.strip_prefix("branch.oid ") {
        let oid = value.trim();
        request.head_sha = if oid == "(initial)" { None } else { Some(oid.to_string()) };
    } else if let Some(value) = header.strip_prefix("branch.head ") {
        let head = value.trim();
        request.is_detached = head == "(detached)";
        request.branch = if request.is_detached { None } else { Some(head.to_string()) };
    } else if let Some(value) = header.strip_prefix("branch.upstream ") {
        request.upstream = trim_to_option(value);
    } else if let Some(value) = header.strip_prefix("branch.ab ") {
        for token in value.split_whitespace() {
            if let Some(ahead) = token.strip_prefix('+').and_then(|raw| raw.parse::<i64>().ok()) {
                request.ahead = Some(ahead);
            }
            if let Some(behind) = token.strip_prefix('-').and_then(|raw| raw.parse::<i64>().ok()) {
                request.behind = Some(behind);
            }
        }
    }
}

fn parse_porcelain_file(line: &str) -> Option<GitFileStatus> {
    if let Some(path) = line.strip_prefix("? ") {
        return Some(GitFileStatus {
            path: path.to_string(),
            old_path: None,
            index_status: Some("?".to_string()),
            worktree_status: Some("?".to_string()),
            category: "untracked".to_string(),
            is_untracked: true,
        });
    }

    if line.starts_with("1 ") {
        let parts: Vec<&str> = line.splitn(9, ' ').collect();
        if parts.len() < 9 {
            return None;
        }
        let xy = parts[1];
        let index = xy.chars().next().unwrap_or('.').to_string();
        let worktree = xy.chars().nth(1).unwrap_or('.').to_string();
        return Some(GitFileStatus {
            path: parts[8].to_string(),
            old_path: None,
            index_status: Some(index.clone()),
            worktree_status: Some(worktree.clone()),
            category: category_from_status(&index, &worktree, false),
            is_untracked: false,
        });
    }

    if line.starts_with("2 ") {
        let parts: Vec<&str> = line.splitn(10, ' ').collect();
        if parts.len() < 10 {
            return None;
        }
        let xy = parts[1];
        let index = xy.chars().next().unwrap_or('R').to_string();
        let worktree = xy.chars().nth(1).unwrap_or('.').to_string();
        let paths: Vec<&str> = parts[9].splitn(2, '\t').collect();
        return Some(GitFileStatus {
            path: paths[0].to_string(),
            old_path: paths.get(1).map(|value| value.to_string()),
            index_status: Some(index),
            worktree_status: Some(worktree),
            category: "renamed".to_string(),
            is_untracked: false,
        });
    }

    None
}

fn count_dirty(files: &[GitFileStatus]) -> GitDirtyCounts {
    let mut counts = GitDirtyCounts {
        total: files.len() as i64,
        ..GitDirtyCounts::default()
    };

    for file in files {
        if file.is_untracked {
            counts.untracked += 1;
        }
        if file
            .index_status
            .as_deref()
            .is_some_and(|value| value != "." && value != "?" && value != " ")
        {
            counts.staged += 1;
        }
        if file
            .worktree_status
            .as_deref()
            .is_some_and(|value| value != "." && value != "?" && value != " ")
        {
            counts.unstaged += 1;
        }
        match file.category.as_str() {
            "modified" => counts.modified += 1,
            "added" => counts.added += 1,
            "deleted" => counts.deleted += 1,
            "renamed" => counts.renamed += 1,
            _ => {}
        }
    }

    counts
}

fn category_from_status(index: &str, worktree: &str, untracked: bool) -> String {
    if untracked || index == "?" || worktree == "?" {
        "untracked".to_string()
    } else if index == "R" || worktree == "R" {
        "renamed".to_string()
    } else if index == "D" || worktree == "D" {
        "deleted".to_string()
    } else if index == "A" || worktree == "A" {
        "added".to_string()
    } else if index == "M" || worktree == "M" {
        "modified".to_string()
    } else {
        "changed".to_string()
    }
}

#[derive(Debug)]
struct GitCommandResult {
    exit_code: i32,
    stdout: String,
    stderr: String,
    truncated: bool,
    warnings: Vec<String>,
}

fn run_git(root_path: &str, args: &[&str]) -> Result<GitCommandResult, String> {
    let output = Command::new("git")
        .arg("-C")
        .arg(root_path)
        .args(args)
        .output()
        .map_err(|error| format!("Failed to start git: {error}"))?;

    let stdout = String::from_utf8_lossy(&output.stdout).to_string();
    let stderr = String::from_utf8_lossy(&output.stderr).to_string();
    Ok(GitCommandResult {
        exit_code: output.status.code().unwrap_or(-1),
        stdout,
        stderr,
        truncated: false,
        warnings: Vec::new(),
    })
}

fn format_git_error(command: &str, result: &GitCommandResult) -> String {
    let stderr = result.stderr.trim();
    if stderr.is_empty() {
        format!("{command} failed with exit code {}", result.exit_code)
    } else {
        format!("{command} failed with exit code {}: {stderr}", result.exit_code)
    }
}

fn trim_to_option(value: &str) -> Option<String> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn settings() -> OperatorSettings {
        OperatorSettings {
            den_base_url: "http://localhost:5199".to_string(),
            source_instance_id: "test-source".to_string(),
            source_display_name: Some("Test Desktop".to_string()),
            poll_interval_seconds: 30,
            max_changed_files: 200,
        }
    }

    fn scope() -> GitScope {
        GitScope {
            project_id: "den-mcp".to_string(),
            project_name: Some("Den MCP".to_string()),
            task_id: Some(880),
            workspace_id: Some("ws_test".to_string()),
            root_path: "/tmp/den-mcp".to_string(),
            source_kind: "agent_workspace".to_string(),
        }
    }

    #[test]
    fn parses_porcelain_v2_branch_and_dirty_counts() {
        let output = "# branch.oid 1234567890abcdef\n# branch.head task/880-tauri-operator-app\n# branch.upstream origin/task/880-tauri-operator-app\n# branch.ab +2 -1\n1 M. N... 100644 100644 100644 aaa bbb src/lib.rs\n1 .D N... 100644 100644 000000 aaa bbb old.txt\n? scratch.txt\n";

        let snapshot = parse_porcelain_v2(&scope(), &settings(), "2026-04-27T00:00:00.000Z".to_string(), output, 200);

        assert_eq!(snapshot.state, DesktopSnapshotState::Ok);
        assert_eq!(snapshot.branch.as_deref(), Some("task/880-tauri-operator-app"));
        assert_eq!(snapshot.head_sha.as_deref(), Some("1234567890abcdef"));
        assert_eq!(snapshot.upstream.as_deref(), Some("origin/task/880-tauri-operator-app"));
        assert_eq!(snapshot.ahead, Some(2));
        assert_eq!(snapshot.behind, Some(1));
        assert_eq!(snapshot.dirty_counts.total, 3);
        assert_eq!(snapshot.dirty_counts.staged, 1);
        assert_eq!(snapshot.dirty_counts.unstaged, 1);
        assert_eq!(snapshot.dirty_counts.untracked, 1);
        assert_eq!(snapshot.changed_files[0].category, "modified");
        assert_eq!(snapshot.changed_files[1].category, "deleted");
    }

    #[test]
    fn truncates_changed_file_list_at_configured_limit() {
        let output = "? one.txt\n? two.txt\n? three.txt\n";
        let snapshot = parse_porcelain_v2(&scope(), &settings(), "2026-04-27T00:00:00.000Z".to_string(), output, 2);

        assert!(snapshot.truncated);
        assert_eq!(snapshot.changed_files.len(), 2);
        assert_eq!(snapshot.dirty_counts.total, 2);
    }
}
