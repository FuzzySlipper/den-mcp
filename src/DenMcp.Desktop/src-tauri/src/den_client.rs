use std::time::Duration;

use reqwest::{Client, Url};
use serde::{Deserialize, Serialize};

use crate::git::{DesktopGitSnapshotRequest, DesktopSnapshotState};

const HTTP_TIMEOUT: Duration = Duration::from_secs(8);

#[derive(Clone)]
pub struct DenClient {
    http: Client,
}

impl Default for DenClient {
    fn default() -> Self {
        Self::new()
    }
}

impl DenClient {
    pub fn new() -> Self {
        Self {
            http: Client::builder()
                .timeout(HTTP_TIMEOUT)
                .build()
                .expect("reqwest client should build"),
        }
    }

    pub async fn health(&self, base_url: &str) -> Result<DenHealth, String> {
        let url = join_url(base_url, "/health")?;
        let response = self
            .http
            .get(url)
            .send()
            .await
            .map_err(|error| format!("Den health check failed: {error}"))?;
        if !response.status().is_success() {
            return Err(format!("Den health check returned HTTP {}", response.status()));
        }
        response
            .json::<DenHealth>()
            .await
            .map_err(|error| format!("Unable to parse Den health response: {error}"))
    }

    pub async fn list_projects(&self, base_url: &str) -> Result<Vec<Project>, String> {
        let url = join_url(base_url, "/api/projects")?;
        let response = self
            .http
            .get(url)
            .send()
            .await
            .map_err(|error| format!("Unable to fetch Den projects: {error}"))?;
        if !response.status().is_success() {
            return Err(format!("Den projects request returned HTTP {}", response.status()));
        }
        response
            .json::<Vec<Project>>()
            .await
            .map_err(|error| format!("Unable to parse Den projects: {error}"))
    }

    pub async fn list_agent_workspaces(&self, base_url: &str) -> Result<Vec<AgentWorkspace>, String> {
        let mut url = join_url(base_url, "/api/agent-workspaces")?;
        url.query_pairs_mut().append_pair("limit", "200");
        let response = self
            .http
            .get(url)
            .send()
            .await
            .map_err(|error| format!("Unable to fetch Den agent workspaces: {error}"))?;
        if !response.status().is_success() {
            return Err(format!("Den agent workspaces request returned HTTP {}", response.status()));
        }
        response
            .json::<Vec<AgentWorkspace>>()
            .await
            .map_err(|error| format!("Unable to parse Den agent workspaces: {error}"))
    }

    pub async fn publish_git_snapshot(
        &self,
        base_url: &str,
        project_id: &str,
        snapshot: &DesktopGitSnapshotRequest,
    ) -> Result<(), String> {
        let path = format!("/api/projects/{}/desktop/git-snapshots", url_escape(project_id));
        let response = self
            .http
            .put(join_url(base_url, &path)?)
            .json(snapshot)
            .send()
            .await
            .map_err(|error| format!("Unable to publish desktop git snapshot for {project_id}: {error}"))?;
        if !response.status().is_success() {
            let status = response.status();
            let body = response.text().await.unwrap_or_default();
            return Err(format!(
                "Desktop git snapshot publish for {project_id} returned HTTP {status}: {body}"
            ));
        }
        Ok(())
    }

    pub async fn latest_diff_snapshot(
        &self,
        base_url: &str,
        request: &LatestDiffSnapshotRequest,
    ) -> Result<DesktopDiffSnapshotLatestResult, String> {
        let path = format!(
            "/api/projects/{}/desktop/diff-snapshots/latest",
            url_escape(&request.project_id)
        );
        let mut url = join_url(base_url, &path)?;
        {
            let mut query = url.query_pairs_mut();
            query.append_pair("sourceInstanceId", &request.source_instance_id);
            query.append_pair("rootPath", &request.root_path);
            query.append_pair("staleAfterSeconds", "120");
            if let Some(path) = request.path.as_ref().filter(|value| !value.trim().is_empty()) {
                query.append_pair("path", path);
            }
            if let Some(workspace_id) = request.workspace_id.as_ref().filter(|value| !value.trim().is_empty()) {
                query.append_pair("workspaceId", workspace_id);
            }
            if let Some(task_id) = request.task_id {
                query.append_pair("taskId", &task_id.to_string());
            }
        }

        let response = self
            .http
            .get(url)
            .send()
            .await
            .map_err(|error| format!("Unable to fetch latest desktop diff snapshot: {error}"))?;
        if !response.status().is_success() {
            let status = response.status();
            let body = response.text().await.unwrap_or_default();
            return Err(format!("Latest desktop diff snapshot returned HTTP {status}: {body}"));
        }
        response
            .json::<DesktopDiffSnapshotLatestResult>()
            .await
            .map_err(|error| format!("Unable to parse desktop diff snapshot response: {error}"))
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub struct DenHealth {
    pub status: String,
    pub version: Option<String>,
    pub informational_version: Option<String>,
    pub commit: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub struct Project {
    pub id: String,
    pub name: String,
    pub root_path: Option<String>,
    pub description: Option<String>,
    pub created_at: Option<String>,
    pub updated_at: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub struct AgentWorkspace {
    pub id: String,
    pub project_id: String,
    pub task_id: i64,
    pub branch: String,
    pub worktree_path: String,
    pub base_branch: String,
    pub base_commit: Option<String>,
    pub head_commit: Option<String>,
    pub state: String,
    pub created_by_run_id: Option<String>,
    pub dev_server_url: Option<String>,
    pub preview_url: Option<String>,
    pub cleanup_policy: Option<String>,
    pub changed_file_summary: Option<serde_json::Value>,
    pub created_at: Option<String>,
    pub updated_at: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LatestDiffSnapshotRequest {
    pub project_id: String,
    pub task_id: Option<i64>,
    pub workspace_id: Option<String>,
    pub root_path: String,
    pub path: Option<String>,
    pub source_instance_id: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub struct DesktopDiffSnapshotLatestResult {
    pub project_id: String,
    pub task_id: Option<i64>,
    pub workspace_id: Option<String>,
    pub root_path: Option<String>,
    pub path: Option<String>,
    pub source_instance_id: Option<String>,
    pub state: DesktopSnapshotState,
    pub is_stale: bool,
    pub freshness_status: String,
    pub snapshot: Option<DesktopDiffSnapshot>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub struct DesktopDiffSnapshot {
    pub id: i64,
    pub project_id: String,
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
    pub received_at: String,
    pub updated_at: String,
    pub is_stale: bool,
    pub freshness_seconds: i64,
}

fn join_url(base_url: &str, path: &str) -> Result<Url, String> {
    let base = Url::parse(base_url).map_err(|error| format!("Invalid Den server URL '{base_url}': {error}"))?;
    base.join(path.trim_start_matches('/'))
        .map_err(|error| format!("Unable to construct Den URL for {path}: {error}"))
}

fn url_escape(value: &str) -> String {
    value
        .bytes()
        .flat_map(|byte| match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                vec![byte as char]
            }
            other => format!("%{other:02X}").chars().collect::<Vec<char>>(),
        })
        .collect()
}
