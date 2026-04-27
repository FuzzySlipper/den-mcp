use std::collections::VecDeque;
use std::time::Duration;

use chrono::Utc;
use serde::Serialize;
use tauri::async_runtime::{spawn, Mutex};
use tauri::{AppHandle, Emitter, Manager, State};
use tokio::time::sleep;

use crate::den_client::{
    AgentWorkspace, DenClient, DesktopDiffSnapshotLatestResult, LatestDiffSnapshotRequest, Project,
};
use crate::git::{
    build_git_scopes, inspect_diff_snapshots, inspect_scope, GitScope, LocalGitSnapshot,
};
use crate::session::{scan_pi_session_snapshots, LocalSessionSnapshot};
use crate::settings::{
    load_settings, save_settings, OperatorSettings, SaveOperatorSettingsRequest,
};

const STATUS_EVENT: &str = "den://operator-status";
const GIT_SNAPSHOT_EVENT: &str = "den://git-snapshot-updated";
const SESSION_SNAPSHOT_EVENT: &str = "den://session-snapshot-updated";
const MAX_DIAGNOSTICS: usize = 200;

#[derive(Default)]
pub struct OperatorRuntime {
    inner: Mutex<RuntimeState>,
    den: DenClient,
}

#[derive(Clone)]
struct RuntimeState {
    generation: u64,
    settings: OperatorSettings,
    status: OperatorStatus,
    projects: Vec<Project>,
    workspaces: Vec<AgentWorkspace>,
    local_snapshots: Vec<LocalGitSnapshot>,
    local_session_snapshots: Vec<LocalSessionSnapshot>,
    diagnostics: VecDeque<DiagnosticEntry>,
}

impl Default for RuntimeState {
    fn default() -> Self {
        let settings = OperatorSettings::default().normalized();
        Self {
            generation: 0,
            status: OperatorStatus::starting(&settings),
            settings,
            projects: Vec::new(),
            workspaces: Vec::new(),
            local_snapshots: Vec::new(),
            local_session_snapshots: Vec::new(),
            diagnostics: VecDeque::new(),
        }
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OperatorStatus {
    pub phase: String,
    pub den_connection: DenConnectionStatus,
    pub source_instance_id: String,
    pub den_base_url: String,
    pub last_sync_at: Option<String>,
    pub last_publish_at: Option<String>,
    pub observer_statuses: Vec<ObserverStatus>,
    pub diagnostics: Vec<DiagnosticEntry>,
    pub project_count: usize,
    pub workspace_count: usize,
    pub local_snapshot_count: usize,
    pub local_session_snapshot_count: usize,
}

impl OperatorStatus {
    fn starting(settings: &OperatorSettings) -> Self {
        Self {
            phase: "starting".to_string(),
            den_connection: DenConnectionStatus::unknown("Preparing Den operator runtime."),
            source_instance_id: settings.source_instance_id.clone(),
            den_base_url: settings.den_base_url.clone(),
            last_sync_at: None,
            last_publish_at: None,
            observer_statuses: vec![
                ObserverStatus::stopped("git"),
                ObserverStatus::stopped("session"),
            ],
            diagnostics: Vec::new(),
            project_count: 0,
            workspace_count: 0,
            local_snapshot_count: 0,
            local_session_snapshot_count: 0,
        }
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DenConnectionStatus {
    pub state: String,
    pub message: Option<String>,
    pub last_success_at: Option<String>,
    pub last_failure_at: Option<String>,
    pub next_retry_at: Option<String>,
}

impl DenConnectionStatus {
    fn unknown(message: impl Into<String>) -> Self {
        Self {
            state: "unknown".to_string(),
            message: Some(message.into()),
            last_success_at: None,
            last_failure_at: None,
            next_retry_at: None,
        }
    }

    fn connected(previous: &Self, message: impl Into<String>, at: String) -> Self {
        Self {
            state: "connected".to_string(),
            message: Some(message.into()),
            last_success_at: Some(at),
            last_failure_at: previous.last_failure_at.clone(),
            next_retry_at: None,
        }
    }

    fn offline(
        previous: &Self,
        message: impl Into<String>,
        at: String,
        next_retry_at: String,
    ) -> Self {
        Self {
            state: "offline".to_string(),
            message: Some(message.into()),
            last_success_at: previous.last_success_at.clone(),
            last_failure_at: Some(at),
            next_retry_at: Some(next_retry_at),
        }
    }

    fn degraded(
        previous: &Self,
        message: impl Into<String>,
        at: String,
        next_retry_at: String,
    ) -> Self {
        Self {
            state: "degraded".to_string(),
            message: Some(message.into()),
            last_success_at: previous.last_success_at.clone(),
            last_failure_at: Some(at),
            next_retry_at: Some(next_retry_at),
        }
    }

    fn misconfigured(previous: &Self, message: impl Into<String>, at: String) -> Self {
        Self {
            state: "misconfigured".to_string(),
            message: Some(message.into()),
            last_success_at: previous.last_success_at.clone(),
            last_failure_at: Some(at),
            next_retry_at: None,
        }
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ObserverStatus {
    pub kind: String,
    pub state: String,
    pub scopes_scanned: usize,
    pub warning_count: usize,
    pub last_run_at: Option<String>,
    pub next_run_at: Option<String>,
}

impl ObserverStatus {
    fn stopped(kind: impl Into<String>) -> Self {
        Self {
            kind: kind.into(),
            state: "stopped".to_string(),
            scopes_scanned: 0,
            warning_count: 0,
            last_run_at: None,
            next_run_at: None,
        }
    }

    fn running(kind: impl Into<String>) -> Self {
        Self {
            kind: kind.into(),
            state: "running".to_string(),
            scopes_scanned: 0,
            warning_count: 0,
            last_run_at: None,
            next_run_at: None,
        }
    }

    fn ready(
        kind: impl Into<String>,
        scopes_scanned: usize,
        warning_count: usize,
        next_run_at: Option<String>,
    ) -> Self {
        Self {
            kind: kind.into(),
            state: "ready".to_string(),
            scopes_scanned,
            warning_count,
            last_run_at: Some(now_string()),
            next_run_at,
        }
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiagnosticEntry {
    pub level: String,
    pub source: String,
    pub message: String,
    pub observed_at: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalSnapshotList {
    pub scopes: Vec<GitScope>,
    pub snapshots: Vec<LocalGitSnapshot>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalSessionSnapshotList {
    pub snapshots: Vec<LocalSessionSnapshot>,
}

#[tauri::command]
pub async fn get_operator_status(
    state: State<'_, OperatorRuntime>,
) -> Result<OperatorStatus, String> {
    Ok(state.inner.lock().await.status.clone())
}

#[tauri::command]
pub async fn get_settings(state: State<'_, OperatorRuntime>) -> Result<OperatorSettings, String> {
    Ok(state.inner.lock().await.settings.clone())
}

#[tauri::command]
pub async fn save_operator_settings(
    app: AppHandle,
    state: State<'_, OperatorRuntime>,
    request: SaveOperatorSettingsRequest,
) -> Result<OperatorSettings, String> {
    let current = state.inner.lock().await.settings.clone();
    let next = OperatorSettings::from_save_request(&current, request);
    save_settings(&app, &next)?;
    {
        let mut runtime = state.inner.lock().await;
        runtime.generation += 1;
        runtime.settings = next.clone();
        runtime.status.phase = "running".to_string();
        runtime.status.source_instance_id = next.source_instance_id.clone();
        runtime.status.den_base_url = next.den_base_url.clone();
        push_diagnostic(
            &mut runtime,
            "info",
            "settings",
            "Saved Den operator settings.",
        );
        sync_status_from_state(&mut runtime);
    }
    emit_status(&app).await;
    run_refresh(app).await;
    Ok(next)
}

#[tauri::command]
pub async fn refresh_now(app: AppHandle) -> Result<(), String> {
    run_refresh(app).await;
    Ok(())
}

#[tauri::command]
pub async fn list_local_snapshots(
    state: State<'_, OperatorRuntime>,
) -> Result<LocalSnapshotList, String> {
    let runtime = state.inner.lock().await;
    let scopes = build_git_scopes(&runtime.projects, &runtime.workspaces);
    Ok(LocalSnapshotList {
        scopes,
        snapshots: runtime.local_snapshots.clone(),
    })
}

#[tauri::command]
pub async fn list_local_session_snapshots(
    state: State<'_, OperatorRuntime>,
) -> Result<LocalSessionSnapshotList, String> {
    let runtime = state.inner.lock().await;
    Ok(LocalSessionSnapshotList {
        snapshots: runtime.local_session_snapshots.clone(),
    })
}

#[tauri::command]
pub async fn get_latest_diff_snapshot(
    state: State<'_, OperatorRuntime>,
    request: LatestDiffSnapshotRequest,
) -> Result<DesktopDiffSnapshotLatestResult, String> {
    let (settings, den) = {
        let runtime = state.inner.lock().await;
        (runtime.settings.clone(), state.den.clone())
    };
    den.latest_diff_snapshot(&settings.den_base_url, &request)
        .await
}

pub async fn start_runtime(app: AppHandle) {
    let settings = load_settings(&app).normalized();
    {
        let state = app.state::<OperatorRuntime>();
        let mut runtime = state.inner.lock().await;
        runtime.settings = settings.clone();
        runtime.status = OperatorStatus::starting(&settings);
        runtime.status.phase = "running".to_string();
        push_diagnostic(
            &mut runtime,
            "info",
            "runtime",
            "Started Den operator runtime.",
        );
        sync_status_from_state(&mut runtime);
    }
    emit_status(&app).await;
    run_refresh(app.clone()).await;

    spawn(async move {
        loop {
            let interval = {
                let state = app.state::<OperatorRuntime>();
                let runtime = state.inner.lock().await;
                runtime.settings.poll_interval_seconds
            };
            sleep(Duration::from_secs(interval)).await;
            run_refresh(app.clone()).await;
        }
    });
}

async fn run_refresh(app: AppHandle) {
    let (settings, den, cached_projects, cached_workspaces) = {
        let state = app.state::<OperatorRuntime>();
        let den = state.den.clone();
        let mut runtime = state.inner.lock().await;
        runtime.status.phase = "running".to_string();
        runtime.status.observer_statuses = vec![
            ObserverStatus::running("git"),
            ObserverStatus::running("session"),
        ];
        sync_status_from_state(&mut runtime);
        (
            runtime.settings.clone(),
            den,
            runtime.projects.clone(),
            runtime.workspaces.clone(),
        )
    };
    emit_status(&app).await;

    if reqwest::Url::parse(&settings.den_base_url).is_err() {
        let state = app.state::<OperatorRuntime>();
        let mut runtime = state.inner.lock().await;
        let at = now_string();
        runtime.status.den_connection = DenConnectionStatus::misconfigured(
            &runtime.status.den_connection,
            format!("Invalid Den server URL: {}", settings.den_base_url),
            at,
        );
        runtime.status.observer_statuses = vec![
            ObserverStatus::stopped("git"),
            ObserverStatus::stopped("session"),
        ];
        push_diagnostic(
            &mut runtime,
            "warn",
            "den",
            "Den server URL is invalid; observers are waiting for valid settings.",
        );
        sync_status_from_state(&mut runtime);
        drop(runtime);
        emit_status(&app).await;
        return;
    }

    let now = now_string();
    let next_retry_at = seconds_from_now(settings.poll_interval_seconds);
    let mut den_connected = false;
    let mut projects = cached_projects;
    let mut workspaces = cached_workspaces;

    match den.health(&settings.den_base_url).await {
        Ok(health) => {
            den_connected = true;
            let state = app.state::<OperatorRuntime>();
            let mut runtime = state.inner.lock().await;
            runtime.status.den_connection = DenConnectionStatus::connected(
                &runtime.status.den_connection,
                format!("Connected to Den ({})", health.status),
                now.clone(),
            );
            runtime.status.last_sync_at = Some(now.clone());
            sync_status_from_state(&mut runtime);
        }
        Err(error) => {
            let state = app.state::<OperatorRuntime>();
            let mut runtime = state.inner.lock().await;
            runtime.status.den_connection = DenConnectionStatus::offline(
                &runtime.status.den_connection,
                error.clone(),
                now.clone(),
                next_retry_at.clone(),
            );
            push_diagnostic(&mut runtime, "warn", "den", error);
            sync_status_from_state(&mut runtime);
        }
    }

    if den_connected {
        match den.list_projects(&settings.den_base_url).await {
            Ok(fetched_projects) => projects = fetched_projects,
            Err(error) => {
                den_connected = false;
                let state = app.state::<OperatorRuntime>();
                let mut runtime = state.inner.lock().await;
                runtime.status.den_connection = DenConnectionStatus::degraded(
                    &runtime.status.den_connection,
                    error.clone(),
                    now_string(),
                    next_retry_at.clone(),
                );
                push_diagnostic(&mut runtime, "warn", "den", error);
                sync_status_from_state(&mut runtime);
            }
        }
    }

    if den_connected {
        match den.list_agent_workspaces(&settings.den_base_url).await {
            Ok(fetched_workspaces) => workspaces = fetched_workspaces,
            Err(error) => {
                den_connected = false;
                let state = app.state::<OperatorRuntime>();
                let mut runtime = state.inner.lock().await;
                runtime.status.den_connection = DenConnectionStatus::degraded(
                    &runtime.status.den_connection,
                    error.clone(),
                    now_string(),
                    next_retry_at.clone(),
                );
                push_diagnostic(&mut runtime, "warn", "den", error);
                sync_status_from_state(&mut runtime);
            }
        }
    }

    let scopes = build_git_scopes(&projects, &workspaces);
    let mut snapshots = Vec::new();
    let mut warning_count = 0;
    let mut publish_successes = 0;
    let mut diff_publish_successes = 0;
    let mut publish_errors = Vec::new();

    for scope in &scopes {
        let mut snapshot = inspect_scope(scope, &settings);
        warning_count += snapshot.request.warnings.len();
        if den_connected {
            match den
                .publish_git_snapshot(&settings.den_base_url, &scope.project_id, &snapshot.request)
                .await
            {
                Ok(()) => {
                    publish_successes += 1;
                    snapshot.last_publish_status = "published".to_string();
                    snapshot.last_published_at = Some(now_string());
                }
                Err(error) => {
                    snapshot.last_publish_status = "failed".to_string();
                    snapshot.last_publish_error = Some(error.clone());
                    publish_errors.push(error);
                }
            }
        } else {
            snapshot.last_publish_status = "queued".to_string();
            snapshot.last_publish_error =
                Some("Den is offline; latest local snapshot is retained in memory.".to_string());
        }
        snapshots.push(snapshot);
    }

    if den_connected {
        for snapshot in &snapshots {
            for diff_snapshot in inspect_diff_snapshots(snapshot) {
                match den
                    .publish_diff_snapshot(
                        &settings.den_base_url,
                        &snapshot.scope.project_id,
                        &diff_snapshot,
                    )
                    .await
                {
                    Ok(()) => diff_publish_successes += 1,
                    Err(error) => publish_errors.push(error),
                }
            }
        }
    }

    let mut session_result = scan_pi_session_snapshots(&settings, &projects);
    let mut session_publish_successes = 0;
    if den_connected {
        for session in &mut session_result.snapshots {
            match den
                .publish_session_snapshot(
                    &settings.den_base_url,
                    &session.project_id,
                    &session.request,
                )
                .await
            {
                Ok(()) => {
                    session_publish_successes += 1;
                    session.last_publish_status = "published".to_string();
                    session.last_published_at = Some(now_string());
                }
                Err(error) => {
                    session.last_publish_status = "failed".to_string();
                    session.last_publish_error = Some(error.clone());
                    publish_errors.push(error);
                }
            }
        }
    } else {
        for session in &mut session_result.snapshots {
            session.last_publish_status = "queued".to_string();
            session.last_publish_error = Some(
                "Den is offline; latest local session snapshot is retained in memory.".to_string(),
            );
        }
    }

    {
        let state = app.state::<OperatorRuntime>();
        let mut runtime = state.inner.lock().await;
        runtime.projects = projects;
        runtime.workspaces = workspaces;
        runtime.local_snapshots = snapshots.clone();
        runtime.local_session_snapshots = session_result.snapshots.clone();
        runtime.status.local_snapshot_count = snapshots.len();
        runtime.status.local_session_snapshot_count = session_result.snapshots.len();
        runtime.status.observer_statuses = vec![
            ObserverStatus::ready(
                "git",
                scopes.len(),
                warning_count,
                Some(seconds_from_now(settings.poll_interval_seconds)),
            ),
            ObserverStatus::ready(
                "session",
                session_result.snapshots.len(),
                session_result.warning_count,
                Some(seconds_from_now(settings.poll_interval_seconds)),
            ),
        ];
        if publish_successes > 0 || diff_publish_successes > 0 || session_publish_successes > 0 {
            runtime.status.last_publish_at = Some(now_string());
        }
        for error in publish_errors.into_iter().take(5) {
            runtime.status.den_connection = DenConnectionStatus::degraded(
                &runtime.status.den_connection,
                error.clone(),
                now_string(),
                next_retry_at.clone(),
            );
            push_diagnostic(&mut runtime, "warn", "publish", error);
        }
        sync_status_from_state(&mut runtime);
    }

    emit_status(&app).await;
    let _ = app.emit(GIT_SNAPSHOT_EVENT, snapshots);
    let _ = app.emit(SESSION_SNAPSHOT_EVENT, session_result.snapshots);
}

async fn emit_status(app: &AppHandle) {
    let status = {
        let state = app.state::<OperatorRuntime>();
        let runtime = state.inner.lock().await;
        runtime.status.clone()
    };
    let _ = app.emit(STATUS_EVENT, status);
}

fn sync_status_from_state(runtime: &mut RuntimeState) {
    runtime.status.source_instance_id = runtime.settings.source_instance_id.clone();
    runtime.status.den_base_url = runtime.settings.den_base_url.clone();
    runtime.status.project_count = runtime.projects.len();
    runtime.status.workspace_count = runtime.workspaces.len();
    runtime.status.local_snapshot_count = runtime.local_snapshots.len();
    runtime.status.local_session_snapshot_count = runtime.local_session_snapshots.len();
    runtime.status.diagnostics = runtime.diagnostics.iter().cloned().collect();
}

fn push_diagnostic(
    runtime: &mut RuntimeState,
    level: impl Into<String>,
    source: impl Into<String>,
    message: impl Into<String>,
) {
    runtime.diagnostics.push_back(DiagnosticEntry {
        level: level.into(),
        source: source.into(),
        message: message.into(),
        observed_at: now_string(),
    });
    while runtime.diagnostics.len() > MAX_DIAGNOSTICS {
        runtime.diagnostics.pop_front();
    }
}

fn now_string() -> String {
    Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
}

fn seconds_from_now(seconds: u64) -> String {
    (Utc::now() + chrono::Duration::seconds(seconds as i64))
        .to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
}
