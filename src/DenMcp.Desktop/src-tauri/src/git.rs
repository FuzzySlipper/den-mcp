use std::collections::HashSet;
use std::path::{Component, Path};
use std::process::Command;

use chrono::Utc;
use serde::{Deserialize, Serialize};

use crate::den_client::{AgentWorkspace, Project};
use crate::settings::OperatorSettings;

const MAX_DIFF_FILES_PER_SCOPE: usize = 20;
const MAX_DIFF_BYTES: usize = 64 * 1024;

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
#[serde(rename_all = "snake_case")]
pub struct DesktopDiffSnapshotRequest {
    pub task_id: Option<i64>,
    pub workspace_id: Option<String>,
    pub root_path: String,
    pub path: Option<String>,
    pub base_ref: Option<String>,
    pub head_ref: Option<String>,
    pub max_bytes: i64,
    pub staged: bool,
    pub diff: String,
    pub truncated: bool,
    pub binary: bool,
    pub warnings: Vec<String>,
    pub source_instance_id: String,
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
        if let Some(root_path) = project
            .root_path
            .as_ref()
            .and_then(|value| trim_to_option(value))
        {
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
        warnings.push(format!(
            "Path is not visible on this machine: {}",
            scope.root_path
        ));
        base_request(
            scope,
            settings,
            observed_at,
            DesktopSnapshotState::PathNotVisible,
            warnings,
        )
    } else {
        match inspect_visible_git_scope(
            scope,
            settings,
            observed_at.clone(),
            settings.max_changed_files,
        ) {
            Ok(snapshot) => snapshot,
            Err(error) => {
                warnings.push(error);
                base_request(
                    scope,
                    settings,
                    observed_at,
                    DesktopSnapshotState::GitError,
                    warnings,
                )
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
    let status = run_git(
        &scope.root_path,
        &[
            "status",
            "--porcelain=v2",
            "--branch",
            "--untracked-files=all",
        ],
    )?;
    Ok(snapshot_from_git_status(
        scope,
        settings,
        observed_at,
        status,
        max_changed_files,
    ))
}

fn snapshot_from_git_status(
    scope: &GitScope,
    settings: &OperatorSettings,
    observed_at: String,
    status: GitCommandResult,
    max_changed_files: usize,
) -> DesktopGitSnapshotRequest {
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
        return request;
    }

    let mut request = parse_porcelain_v2(
        scope,
        settings,
        observed_at,
        &status.stdout,
        max_changed_files,
    );
    request.truncated = status.truncated || request.changed_files.len() >= max_changed_files;
    request.warnings.extend(status.warnings);
    if request.upstream.is_none() {
        request
            .warnings
            .push("No upstream branch reported by git status.".to_string());
    }
    if request.is_detached {
        request
            .warnings
            .push("Repository is in detached HEAD state.".to_string());
    }
    request
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
    let mut request = base_request(
        scope,
        settings,
        observed_at,
        DesktopSnapshotState::Ok,
        Vec::new(),
    );

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
        request.head_sha = if oid == "(initial)" {
            None
        } else {
            Some(oid.to_string())
        };
    } else if let Some(value) = header.strip_prefix("branch.head ") {
        let head = value.trim();
        request.is_detached = head == "(detached)";
        request.branch = if request.is_detached {
            None
        } else {
            Some(head.to_string())
        };
    } else if let Some(value) = header.strip_prefix("branch.upstream ") {
        request.upstream = trim_to_option(value);
    } else if let Some(value) = header.strip_prefix("branch.ab ") {
        for token in value.split_whitespace() {
            if let Some(ahead) = token
                .strip_prefix('+')
                .and_then(|raw| raw.parse::<i64>().ok())
            {
                request.ahead = Some(ahead);
            }
            if let Some(behind) = token
                .strip_prefix('-')
                .and_then(|raw| raw.parse::<i64>().ok())
            {
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
        let paths: Vec<&str> = split_rename_paths(parts[9]).collect();
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

fn split_rename_paths(value: &str) -> std::str::SplitN<'_, char> {
    if value.contains('\0') {
        value.splitn(2, '\0')
    } else {
        value.splitn(2, '\t')
    }
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

pub fn inspect_diff_snapshots(snapshot: &LocalGitSnapshot) -> Vec<DesktopDiffSnapshotRequest> {
    if snapshot.request.state != DesktopSnapshotState::Ok {
        return Vec::new();
    }

    let mut seen = HashSet::new();
    let mut diff_snapshots = Vec::new();
    for file in &snapshot.request.changed_files {
        if diff_snapshots.len() >= MAX_DIFF_FILES_PER_SCOPE {
            break;
        }
        if !seen.insert(file.path.clone()) {
            continue;
        }
        if !is_safe_relative_git_path(&file.path) {
            continue;
        }
        if let Some(diff) = build_diff_snapshot(snapshot, &file.path, false) {
            diff_snapshots.push(diff);
        }
        if file
            .index_status
            .as_deref()
            .is_some_and(|status| status != "." && status != "?")
        {
            if let Some(diff) = build_diff_snapshot(snapshot, &file.path, true) {
                diff_snapshots.push(diff);
            }
        }
    }
    diff_snapshots
}

fn build_diff_snapshot(
    snapshot: &LocalGitSnapshot,
    path: &str,
    staged: bool,
) -> Option<DesktopDiffSnapshotRequest> {
    let args = diff_args(path, staged);
    let result = run_git(&snapshot.request.root_path, &args).ok()?;
    if result.exit_code != 0 {
        return Some(diff_warning_snapshot(
            snapshot,
            path,
            staged,
            format_git_error(
                if staged {
                    "git diff --cached"
                } else {
                    "git diff HEAD"
                },
                &result,
            ),
        ));
    }
    let mut warnings = result.warnings;
    if result.stdout.is_empty() {
        return None;
    }
    let (diff, output_truncated) = bound_text(&result.stdout, MAX_DIFF_BYTES);
    let truncated = result.truncated || output_truncated;
    if truncated {
        warnings.push(format!("Diff output truncated to {MAX_DIFF_BYTES} bytes."));
    }
    let binary = looks_like_binary_diff(&result.stdout);
    if binary {
        warnings.push("Diff appears to describe binary content.".to_string());
    }

    Some(DesktopDiffSnapshotRequest {
        task_id: snapshot.request.task_id,
        workspace_id: snapshot.request.workspace_id.clone(),
        root_path: snapshot.request.root_path.clone(),
        path: Some(path.to_string()),
        base_ref: Some("HEAD".to_string()),
        head_ref: None,
        max_bytes: MAX_DIFF_BYTES as i64,
        staged,
        diff,
        truncated,
        binary,
        warnings,
        source_instance_id: snapshot.request.source_instance_id.clone(),
        observed_at: now_string(),
    })
}

fn diff_warning_snapshot(
    snapshot: &LocalGitSnapshot,
    path: &str,
    staged: bool,
    warning: String,
) -> DesktopDiffSnapshotRequest {
    DesktopDiffSnapshotRequest {
        task_id: snapshot.request.task_id,
        workspace_id: snapshot.request.workspace_id.clone(),
        root_path: snapshot.request.root_path.clone(),
        path: Some(path.to_string()),
        base_ref: Some("HEAD".to_string()),
        head_ref: None,
        max_bytes: MAX_DIFF_BYTES as i64,
        staged,
        diff: String::new(),
        truncated: false,
        binary: false,
        warnings: vec![warning],
        source_instance_id: snapshot.request.source_instance_id.clone(),
        observed_at: now_string(),
    }
}

fn diff_args(path: &str, staged: bool) -> Vec<&str> {
    if staged {
        vec!["diff", "--cached", "--", path]
    } else {
        vec!["diff", "HEAD", "--", path]
    }
}

fn is_safe_relative_git_path(path: &str) -> bool {
    let path = Path::new(path);
    !path.as_os_str().is_empty()
        && !path.is_absolute()
        && path
            .components()
            .all(|component| matches!(component, Component::Normal(_) | Component::CurDir))
}

fn looks_like_binary_diff(diff: &str) -> bool {
    diff.contains("Binary files ") || diff.contains("GIT binary patch")
}

fn bound_text(value: &str, max_bytes: usize) -> (String, bool) {
    if value.len() <= max_bytes {
        return (value.to_string(), false);
    }
    let mut end = max_bytes;
    while !value.is_char_boundary(end) {
        end -= 1;
    }
    (value[..end].to_string(), true)
}

fn now_string() -> String {
    Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
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
        format!(
            "{command} failed with exit code {}: {stderr}",
            result.exit_code
        )
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

        let snapshot = parse_porcelain_v2(
            &scope(),
            &settings(),
            "2026-04-27T00:00:00.000Z".to_string(),
            output,
            200,
        );

        assert_eq!(snapshot.state, DesktopSnapshotState::Ok);
        assert_eq!(
            snapshot.branch.as_deref(),
            Some("task/880-tauri-operator-app")
        );
        assert_eq!(snapshot.head_sha.as_deref(), Some("1234567890abcdef"));
        assert_eq!(
            snapshot.upstream.as_deref(),
            Some("origin/task/880-tauri-operator-app")
        );
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
    fn parses_rename_paths_with_tab_or_nul_separator() {
        let tab = parse_porcelain_file(
            "2 R. N... 100644 100644 100644 aaa bbb R100 new-name.txt\told-name.txt",
        )
        .expect("tab-separated rename should parse");
        assert_eq!(tab.path, "new-name.txt");
        assert_eq!(tab.old_path.as_deref(), Some("old-name.txt"));
        assert_eq!(tab.category, "renamed");

        let nul = parse_porcelain_file(
            "2 R. N... 100644 100644 100644 aaa bbb R100 new-name.txt\0old-name.txt",
        )
        .expect("nul-separated rename should parse");
        assert_eq!(nul.path, "new-name.txt");
        assert_eq!(nul.old_path.as_deref(), Some("old-name.txt"));
        assert_eq!(nul.category, "renamed");
    }

    #[test]
    fn inspect_scope_reports_missing_path_without_running_git() {
        let mut missing_scope = scope();
        missing_scope.root_path = std::env::temp_dir()
            .join(format!("den-missing-{}", uuid::Uuid::new_v4().simple()))
            .display()
            .to_string();

        let snapshot = inspect_scope(&missing_scope, &settings());

        assert_eq!(snapshot.request.state, DesktopSnapshotState::PathNotVisible);
        assert_eq!(snapshot.request.dirty_counts.total, 0);
        assert!(snapshot
            .request
            .warnings
            .iter()
            .any(|warning| warning.contains("Path is not visible")));
    }

    #[test]
    fn inspect_scope_reports_visible_non_git_directory() {
        let root = temp_dir("den-non-git");
        let mut visible_scope = scope();
        visible_scope.root_path = root.display().to_string();

        let snapshot = inspect_scope(&visible_scope, &settings());

        assert_eq!(
            snapshot.request.state,
            DesktopSnapshotState::NotGitRepository
        );
        assert!(snapshot
            .request
            .warnings
            .iter()
            .any(|warning| warning.contains("git status failed")));
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn non_repository_status_maps_to_not_git_and_other_failures_map_to_git_error() {
        let not_git = snapshot_from_git_status(
            &scope(),
            &settings(),
            "2026-04-27T00:00:00.000Z".to_string(),
            GitCommandResult {
                exit_code: 128,
                stdout: String::new(),
                stderr: "fatal: not a git repository".to_string(),
                truncated: false,
                warnings: Vec::new(),
            },
            200,
        );
        assert_eq!(not_git.state, DesktopSnapshotState::NotGitRepository);

        let git_error = snapshot_from_git_status(
            &scope(),
            &settings(),
            "2026-04-27T00:00:00.000Z".to_string(),
            GitCommandResult {
                exit_code: 129,
                stdout: String::new(),
                stderr: "fatal: unsupported git invocation".to_string(),
                truncated: true,
                warnings: vec!["stdout truncated".to_string()],
            },
            200,
        );
        assert_eq!(git_error.state, DesktopSnapshotState::GitError);
        assert!(git_error.truncated);
    }

    #[test]
    fn build_git_scopes_includes_project_and_workspace_roots() {
        let projects = vec![crate::den_client::Project {
            id: "den-mcp".to_string(),
            name: "Den MCP".to_string(),
            root_path: Some("/home/patch/dev/den-mcp".to_string()),
            description: None,
            created_at: None,
            updated_at: None,
        }];
        let workspaces = vec![crate::den_client::AgentWorkspace {
            id: "ws_test".to_string(),
            project_id: "den-mcp".to_string(),
            task_id: 880,
            branch: "task/880-tauri-operator-app".to_string(),
            worktree_path: "/home/patch/dev/den-mcp".to_string(),
            base_branch: "main".to_string(),
            base_commit: None,
            head_commit: None,
            state: "active".to_string(),
            created_by_run_id: None,
            dev_server_url: None,
            preview_url: None,
            cleanup_policy: None,
            changed_file_summary: None,
            created_at: None,
            updated_at: None,
        }];

        let scopes = build_git_scopes(&projects, &workspaces);

        assert_eq!(scopes.len(), 2);
        assert!(scopes
            .iter()
            .any(|scope| scope.source_kind == "project_root"));
        assert!(scopes
            .iter()
            .any(|scope| scope.workspace_id.as_deref() == Some("ws_test")));
    }

    #[test]
    fn build_git_scopes_deduplicates_roots_and_filters_inactive_workspaces() {
        let projects = vec![
            crate::den_client::Project {
                id: "den-mcp".to_string(),
                name: "Den MCP".to_string(),
                root_path: Some(" /repo ".to_string()),
                description: None,
                created_at: None,
                updated_at: None,
            },
            crate::den_client::Project {
                id: "den-mcp".to_string(),
                name: "Den MCP duplicate".to_string(),
                root_path: Some("/repo".to_string()),
                description: None,
                created_at: None,
                updated_at: None,
            },
            crate::den_client::Project {
                id: "blank".to_string(),
                name: "Blank".to_string(),
                root_path: Some("   ".to_string()),
                description: None,
                created_at: None,
                updated_at: None,
            },
        ];
        let workspaces = vec![
            workspace("active", "ws_active", "/repo-worktree"),
            workspace("archived", "ws_archived", "/archived"),
            workspace("complete", "ws_complete", "/complete"),
            workspace("active", "ws_blank", "   "),
            workspace("active", "ws_active", "/repo-worktree"),
        ];

        let scopes = build_git_scopes(&projects, &workspaces);

        assert_eq!(scopes.len(), 2);
        assert_eq!(
            scopes
                .iter()
                .filter(|scope| scope.source_kind == "project_root")
                .count(),
            1
        );
        assert_eq!(
            scopes
                .iter()
                .filter(|scope| scope.source_kind == "agent_workspace")
                .count(),
            1
        );
        assert!(scopes
            .iter()
            .any(|scope| scope.workspace_id.as_deref() == Some("ws_active")));
    }

    #[test]
    fn diff_helpers_validate_paths_and_build_safe_arguments() {
        assert!(is_safe_relative_git_path("src/main.rs"));
        assert!(is_safe_relative_git_path("./src/main.rs"));
        assert!(!is_safe_relative_git_path(""));
        assert!(!is_safe_relative_git_path("../secret"));
        assert!(!is_safe_relative_git_path("/tmp/secret"));

        assert_eq!(
            diff_args("src/main.rs", false),
            vec!["diff", "HEAD", "--", "src/main.rs"]
        );
        assert_eq!(
            diff_args("src/main.rs", true),
            vec!["diff", "--cached", "--", "src/main.rs"]
        );
    }

    #[test]
    fn bounded_diff_output_preserves_char_boundaries_and_binary_warning_detection() {
        let (bounded, truncated) = bound_text("αβγδε", 5);
        assert_eq!(bounded, "αβ");
        assert!(truncated);
        assert!(looks_like_binary_diff(
            "diff --git a/bin b/bin\nBinary files a/bin and b/bin differ"
        ));
        assert!(looks_like_binary_diff("GIT binary patch\nliteral 0"));
    }

    #[test]
    fn inspect_diff_snapshots_skips_non_ok_snapshots_and_unsafe_paths() {
        let mut local = LocalGitSnapshot {
            scope: scope(),
            request: base_request(
                &scope(),
                &settings(),
                "2026-04-27T00:00:00.000Z".to_string(),
                DesktopSnapshotState::GitError,
                Vec::new(),
            ),
            last_publish_status: "pending".to_string(),
            last_publish_error: None,
            last_published_at: None,
        };
        assert!(inspect_diff_snapshots(&local).is_empty());

        local.request.state = DesktopSnapshotState::Ok;
        local.request.changed_files = vec![GitFileStatus {
            path: "../outside".to_string(),
            old_path: None,
            index_status: Some("M".to_string()),
            worktree_status: Some("M".to_string()),
            category: "modified".to_string(),
            is_untracked: false,
        }];
        assert!(inspect_diff_snapshots(&local).is_empty());
    }

    #[test]
    fn truncates_changed_file_list_at_configured_limit() {
        let output = "? one.txt\n? two.txt\n? three.txt\n";
        let snapshot = parse_porcelain_v2(
            &scope(),
            &settings(),
            "2026-04-27T00:00:00.000Z".to_string(),
            output,
            2,
        );

        assert!(snapshot.truncated);
        assert_eq!(snapshot.changed_files.len(), 2);
        assert_eq!(snapshot.dirty_counts.total, 2);
    }

    fn temp_dir(prefix: &str) -> std::path::PathBuf {
        let path = std::env::temp_dir().join(format!("{prefix}-{}", uuid::Uuid::new_v4().simple()));
        std::fs::create_dir_all(&path).expect("temp dir should be created");
        path
    }

    fn workspace(state: &str, id: &str, path: &str) -> crate::den_client::AgentWorkspace {
        crate::den_client::AgentWorkspace {
            id: id.to_string(),
            project_id: "den-mcp".to_string(),
            task_id: 880,
            branch: "task/880-tauri-operator-app".to_string(),
            worktree_path: path.to_string(),
            base_branch: "main".to_string(),
            base_commit: None,
            head_commit: None,
            state: state.to_string(),
            created_by_run_id: None,
            dev_server_url: None,
            preview_url: None,
            cleanup_policy: None,
            changed_file_summary: None,
            created_at: None,
            updated_at: None,
        }
    }
}
