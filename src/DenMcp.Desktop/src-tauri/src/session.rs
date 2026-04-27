use std::fs;
use std::path::{Path, PathBuf};

use chrono::Utc;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::den_client::Project;
use crate::settings::OperatorSettings;

const MAX_RUN_DIRS: usize = 40;
const MAX_RECENT_ACTIVITY: usize = 8;
const MAX_JSONL_BYTES: u64 = 512_000;

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalSessionSnapshot {
    pub project_id: String,
    pub request: DesktopSessionSnapshotRequest,
    pub last_publish_status: String,
    pub last_publish_error: Option<String>,
    pub last_published_at: Option<String>,
    pub artifact_root: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "snake_case")]
pub struct DesktopSessionSnapshotRequest {
    pub task_id: Option<i64>,
    pub workspace_id: Option<String>,
    pub session_id: String,
    pub parent_session_id: Option<String>,
    pub agent_identity: Option<String>,
    pub role: Option<String>,
    pub current_command: Option<String>,
    pub current_phase: Option<String>,
    pub recent_activity: Value,
    pub child_sessions: Value,
    pub control_capabilities: Value,
    pub warnings: Vec<String>,
    pub source_instance_id: String,
    pub observed_at: String,
}

#[derive(Clone, Debug)]
pub struct SessionScanResult {
    pub snapshots: Vec<LocalSessionSnapshot>,
    pub warning_count: usize,
}

#[derive(Clone, Debug, Deserialize)]
struct RunStatus {
    run_id: Option<String>,
    role: Option<String>,
    task_id: Option<i64>,
    cwd: Option<String>,
    state: Option<String>,
    backend: Option<String>,
    pid: Option<i64>,
    started_at: Option<String>,
    ended_at: Option<String>,
    exit_code: Option<i64>,
    current_command: Option<String>,
    current_phase: Option<String>,
    pi_session_id: Option<String>,
    pi_session_file_path: Option<String>,
    workspace_id: Option<String>,
    artifacts: Option<RunArtifacts>,
}

#[derive(Clone, Debug, Deserialize)]
struct RunArtifacts {
    dir: Option<String>,
    session_file_path: Option<String>,
}

#[derive(Clone, Debug)]
struct RunCandidate {
    status_path: PathBuf,
    modified_ms: i128,
}

pub fn scan_pi_session_snapshots(
    settings: &OperatorSettings,
    projects: &[Project],
) -> SessionScanResult {
    let mut warnings = Vec::new();
    let Some(root) = pi_run_root() else {
        return SessionScanResult {
            snapshots: Vec::new(),
            warning_count: 1,
        };
    };

    if !root.exists() {
        return SessionScanResult {
            snapshots: Vec::new(),
            warning_count: 0,
        };
    }

    let mut candidates = match run_candidates(&root) {
        Ok(candidates) => candidates,
        Err(error) => {
            warnings.push(format!(
                "Unable to scan Pi run artifacts at {}: {error}",
                root.display()
            ));
            Vec::new()
        }
    };
    candidates.sort_by(|a, b| b.modified_ms.cmp(&a.modified_ms));
    candidates.truncate(MAX_RUN_DIRS);

    let mut snapshots = Vec::new();
    for candidate in candidates {
        match snapshot_from_status_path(&candidate.status_path, settings, projects) {
            Ok(Some(snapshot)) => snapshots.push(snapshot),
            Ok(None) => {}
            Err(error) => warnings.push(error),
        }
    }

    SessionScanResult {
        warning_count: warnings.len(),
        snapshots,
    }
}

fn snapshot_from_status_path(
    status_path: &Path,
    settings: &OperatorSettings,
    projects: &[Project],
) -> Result<Option<LocalSessionSnapshot>, String> {
    let status_text = fs::read_to_string(status_path).map_err(|error| {
        format!(
            "Unable to read Pi run status {}: {error}",
            status_path.display()
        )
    })?;
    let status: RunStatus = serde_json::from_str(&status_text).map_err(|error| {
        format!(
            "Unable to parse Pi run status {}: {error}",
            status_path.display()
        )
    })?;

    let Some(project_id) = project_id_for_status(&status, projects) else {
        return Ok(None);
    };

    let run_id = status.run_id.clone().unwrap_or_else(|| {
        status_path
            .parent()
            .and_then(|path| path.file_name())
            .and_then(|name| name.to_str())
            .unwrap_or("unknown-run")
            .to_string()
    });
    let session_id = status
        .pi_session_id
        .clone()
        .unwrap_or_else(|| format!("pi-run-{run_id}"));
    let session_file = status
        .pi_session_file_path
        .as_ref()
        .map(PathBuf::from)
        .or_else(|| {
            status
                .artifacts
                .as_ref()
                .and_then(|artifacts| artifacts.session_file_path.as_ref().map(PathBuf::from))
        });

    let (recent_activity, latest_tool, activity_warnings) = match session_file.as_ref() {
        Some(path) => read_recent_activity(path),
        None => (
            Vec::new(),
            None,
            vec!["Pi session file path was not recorded for this run.".to_string()],
        ),
    };

    let observed_at = Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true);
    let phase = status
        .current_phase
        .clone()
        .or_else(|| status.state.clone())
        .or_else(|| status.ended_at.as_ref().map(|_| "complete".to_string()))
        .unwrap_or_else(|| "observed".to_string());
    let command = status
        .current_command
        .clone()
        .or(latest_tool)
        .or_else(|| status.backend.clone());
    let artifact_root = status
        .artifacts
        .as_ref()
        .and_then(|artifacts| artifacts.dir.clone())
        .or_else(|| status_path.parent().map(|path| path.display().to_string()));

    let mut warnings = activity_warnings;
    if status.ended_at.is_some() {
        warnings
            .push("Session is complete; snapshot is retained for correlation/history.".to_string());
    }

    let child_sessions = json!({
        "schema": "den_desktop_session_children",
        "schema_version": 1,
        "items": [],
        "note": "Sub-agent children are available from Den run records; local artifact scan does not infer a process tree yet."
    });
    let control_capabilities = json!({
        "schema": "den_desktop_session_capabilities",
        "schema_version": 1,
        "can_focus": false,
        "can_stream_raw_terminal": false,
        "can_send_input": false,
        "can_stop": false,
        "can_launch_managed_session": false,
        "reason": "Artifact-observer mode only; no PTY ownership or safe controls are active in this spike."
    });
    let recent_activity = json!({
        "schema": "den_desktop_recent_activity",
        "schema_version": 1,
        "items": recent_activity,
        "run": {
            "run_id": run_id,
            "pid": status.pid,
            "started_at": status.started_at,
            "ended_at": status.ended_at,
            "exit_code": status.exit_code,
        }
    });

    Ok(Some(LocalSessionSnapshot {
        project_id,
        artifact_root,
        request: DesktopSessionSnapshotRequest {
            task_id: status.task_id,
            workspace_id: status.workspace_id.clone(),
            session_id,
            parent_session_id: None,
            agent_identity: Some("pi".to_string()),
            role: status.role.clone(),
            current_command: command,
            current_phase: Some(phase),
            recent_activity,
            child_sessions,
            control_capabilities,
            warnings,
            source_instance_id: settings.source_instance_id.clone(),
            observed_at,
        },
        last_publish_status: "pending".to_string(),
        last_publish_error: None,
        last_published_at: None,
    }))
}

fn project_id_for_status(status: &RunStatus, projects: &[Project]) -> Option<String> {
    let cwd = status.cwd.as_ref()?;
    projects
        .iter()
        .filter_map(|project| project.root_path.as_ref().map(|root| (project, root)))
        .filter(|(_, root)| !root.trim().is_empty() && cwd.starts_with(root.as_str()))
        .max_by_key(|(_, root)| root.len())
        .map(|(project, _)| project.id.clone())
        .or_else(|| {
            if projects.len() == 1 {
                Some(projects[0].id.clone())
            } else {
                None
            }
        })
}

fn run_candidates(root: &Path) -> Result<Vec<RunCandidate>, std::io::Error> {
    let mut candidates = Vec::new();
    for entry in fs::read_dir(root)? {
        let entry = entry?;
        if !entry.file_type()?.is_dir() {
            continue;
        }
        let status_path = entry.path().join("status.json");
        if !status_path.exists() {
            continue;
        }
        let modified_ms = entry
            .metadata()
            .and_then(|metadata| metadata.modified())
            .ok()
            .and_then(|modified| modified.duration_since(std::time::UNIX_EPOCH).ok())
            .map(|duration| duration.as_millis() as i128)
            .unwrap_or(0);
        candidates.push(RunCandidate {
            status_path,
            modified_ms,
        });
    }
    Ok(candidates)
}

fn read_recent_activity(path: &Path) -> (Vec<Value>, Option<String>, Vec<String>) {
    let mut warnings = Vec::new();
    let metadata = match fs::metadata(path) {
        Ok(metadata) => metadata,
        Err(error) => {
            return (
                Vec::new(),
                None,
                vec![format!(
                    "Unable to read Pi session file {}: {error}",
                    path.display()
                )],
            )
        }
    };
    if metadata.len() > MAX_JSONL_BYTES {
        warnings.push(format!(
            "Pi session file {} is large; reading first bounded {} bytes for the spike.",
            path.display(),
            MAX_JSONL_BYTES
        ));
    }

    let text = match fs::read_to_string(path) {
        Ok(text) => text,
        Err(error) => {
            return (
                Vec::new(),
                None,
                vec![format!(
                    "Unable to read Pi session file {}: {error}",
                    path.display()
                )],
            )
        }
    };

    let lines = text.lines().collect::<Vec<_>>();
    let start = lines.len().saturating_sub(200);
    let mut activity = Vec::new();
    let mut latest_tool = None;
    for line in &lines[start..] {
        if let Some((item, tool)) = activity_from_jsonl_line(line) {
            if tool.is_some() {
                latest_tool = tool;
            }
            activity.push(item);
        }
    }
    if activity.len() > MAX_RECENT_ACTIVITY {
        activity = activity.split_off(activity.len() - MAX_RECENT_ACTIVITY);
    }
    (activity, latest_tool, warnings)
}

fn activity_from_jsonl_line(line: &str) -> Option<(Value, Option<String>)> {
    let value: Value = serde_json::from_str(line).ok()?;
    let timestamp = value
        .get("timestamp")
        .and_then(Value::as_str)
        .map(str::to_string);
    let message = value.get("message")?;
    let role = message
        .get("role")
        .and_then(Value::as_str)
        .unwrap_or("event");
    if role == "toolResult" {
        let tool_name = message
            .get("toolName")
            .and_then(Value::as_str)
            .unwrap_or("tool result");
        let summary = summarize_tool_result(message);
        return Some((
            json!({ "kind": "tool_result", "role": role, "tool": tool_name, "summary": summary, "timestamp": timestamp }),
            None,
        ));
    }

    let content = message.get("content").and_then(Value::as_array)?;
    let mut text_fragments = Vec::new();
    let mut latest_tool = None;
    for item in content {
        match item.get("type").and_then(Value::as_str) {
            Some("text") => {
                if let Some(text) = item.get("text").and_then(Value::as_str) {
                    text_fragments.push(text.to_string());
                }
            }
            Some("thinking") => {
                if let Some(text) = item.get("thinking").and_then(Value::as_str) {
                    text_fragments.push(format!("thinking: {text}"));
                }
            }
            Some("toolCall") => {
                if let Some(name) = item.get("name").and_then(Value::as_str) {
                    latest_tool = Some(name.to_string());
                    text_fragments.push(format!("tool: {name}"));
                }
            }
            _ => {}
        }
    }

    if text_fragments.is_empty() {
        return None;
    }
    Some((
        json!({
            "kind": if latest_tool.is_some() { "assistant_tool_call" } else { "message" },
            "role": role,
            "summary": truncate_summary(&text_fragments.join(" | ")),
            "timestamp": timestamp
        }),
        latest_tool,
    ))
}

fn summarize_tool_result(message: &Value) -> String {
    let text = message
        .get("content")
        .and_then(Value::as_array)
        .and_then(|items| {
            items
                .iter()
                .find_map(|item| item.get("text").and_then(Value::as_str))
        })
        .unwrap_or("tool result");
    truncate_summary(text)
}

fn truncate_summary(text: &str) -> String {
    let normalized = text.split_whitespace().collect::<Vec<_>>().join(" ");
    if normalized.chars().count() <= 180 {
        normalized
    } else {
        format!("{}…", normalized.chars().take(180).collect::<String>())
    }
}

fn pi_run_root() -> Option<PathBuf> {
    std::env::var_os("PI_SUBAGENT_RUNS_DIR")
        .map(PathBuf::from)
        .or_else(|| {
            std::env::var_os("HOME")
                .map(|home| PathBuf::from(home).join(".pi/agent/den-subagent-runs"))
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn activity_parser_extracts_tool_calls_and_text() {
        let line = r#"{"type":"message","timestamp":"2026-04-27T12:00:00Z","message":{"role":"assistant","content":[{"type":"text","text":"Reviewing status"},{"type":"toolCall","name":"bash","arguments":{"command":"git status"}}]}}"#;
        let (activity, tool) = activity_from_jsonl_line(line).expect("activity");
        assert_eq!(tool.as_deref(), Some("bash"));
        assert_eq!(activity["kind"], "assistant_tool_call");
        assert!(activity["summary"].as_str().unwrap().contains("tool: bash"));
    }

    #[test]
    fn project_matching_prefers_longest_visible_root() {
        let status = RunStatus {
            run_id: Some("run-1".to_string()),
            role: None,
            task_id: Some(882),
            cwd: Some("/home/patch/dev/den-mcp/src/DenMcp.Desktop".to_string()),
            state: None,
            backend: None,
            pid: None,
            started_at: None,
            ended_at: None,
            exit_code: None,
            current_command: None,
            current_phase: None,
            pi_session_id: None,
            pi_session_file_path: None,
            workspace_id: None,
            artifacts: None,
        };
        let projects = vec![
            Project {
                id: "dev".to_string(),
                name: "Dev".to_string(),
                root_path: Some("/home/patch/dev".to_string()),
                description: None,
                created_at: None,
                updated_at: None,
            },
            Project {
                id: "den-mcp".to_string(),
                name: "Den MCP".to_string(),
                root_path: Some("/home/patch/dev/den-mcp".to_string()),
                description: None,
                created_at: None,
                updated_at: None,
            },
        ];
        assert_eq!(
            project_id_for_status(&status, &projects).as_deref(),
            Some("den-mcp")
        );
    }
}
