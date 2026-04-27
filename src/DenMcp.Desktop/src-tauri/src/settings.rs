use std::fs;
use std::io::Write;
use std::path::PathBuf;

use atomicwrites::{AtomicFile, OverwriteBehavior};
use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Manager};
use uuid::Uuid;

const SETTINGS_FILE_NAME: &str = "operator-settings.json";

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OperatorSettings {
    pub den_base_url: String,
    pub source_instance_id: String,
    pub source_display_name: Option<String>,
    pub poll_interval_seconds: u64,
    pub max_changed_files: usize,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveOperatorSettingsRequest {
    pub den_base_url: String,
    pub source_display_name: Option<String>,
    pub poll_interval_seconds: Option<u64>,
    pub max_changed_files: Option<usize>,
}

impl Default for OperatorSettings {
    fn default() -> Self {
        Self {
            den_base_url: "http://localhost:5199".to_string(),
            source_instance_id: format!("den-desktop-{}", Uuid::new_v4().simple()),
            source_display_name: Some("Den Desktop".to_string()),
            poll_interval_seconds: 30,
            max_changed_files: 200,
        }
    }
}

impl OperatorSettings {
    pub fn normalized(mut self) -> Self {
        self.den_base_url = self.den_base_url.trim().trim_end_matches('/').to_string();
        if self.den_base_url.is_empty() {
            self.den_base_url = "http://localhost:5199".to_string();
        }
        if self.source_instance_id.trim().is_empty() {
            self.source_instance_id = format!("den-desktop-{}", Uuid::new_v4().simple());
        }
        self.source_display_name = self
            .source_display_name
            .and_then(|value| trim_to_option(&value));
        self.poll_interval_seconds = self.poll_interval_seconds.clamp(5, 3600);
        self.max_changed_files = self.max_changed_files.clamp(25, 2000);
        self
    }

    pub fn from_save_request(current: &OperatorSettings, request: SaveOperatorSettingsRequest) -> Self {
        Self {
            den_base_url: request.den_base_url,
            source_instance_id: current.source_instance_id.clone(),
            source_display_name: request.source_display_name.and_then(|value| trim_to_option(&value)),
            poll_interval_seconds: request.poll_interval_seconds.unwrap_or(current.poll_interval_seconds),
            max_changed_files: request.max_changed_files.unwrap_or(current.max_changed_files),
        }
        .normalized()
    }
}

pub fn load_settings(app: &AppHandle) -> OperatorSettings {
    let path = match settings_path(app) {
        Ok(path) => path,
        Err(error) => {
            log::warn!("Unable to resolve Den operator settings path: {error}");
            return OperatorSettings::default().normalized();
        }
    };

    let contents = match fs::read_to_string(&path) {
        Ok(contents) => contents,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            let settings = OperatorSettings::default().normalized();
            if let Err(save_error) = save_settings(app, &settings) {
                log::warn!("Unable to write default Den operator settings: {save_error}");
            }
            return settings;
        }
        Err(error) => {
            log::warn!("Unable to read Den operator settings from {}: {error}", path.display());
            return OperatorSettings::default().normalized();
        }
    };

    match serde_json::from_str::<OperatorSettings>(&contents) {
        Ok(settings) => settings.normalized(),
        Err(error) => {
            log::warn!("Unable to parse Den operator settings from {}: {error}", path.display());
            OperatorSettings::default().normalized()
        }
    }
}

pub fn save_settings(app: &AppHandle, settings: &OperatorSettings) -> Result<(), String> {
    let settings_path = settings_path(app)?;
    if let Some(parent) = settings_path.parent() {
        fs::create_dir_all(parent).map_err(|error| {
            format!(
                "Unable to create Den operator settings directory {}: {error}",
                parent.display()
            )
        })?;
    }

    let payload = serde_json::to_vec_pretty(&settings.clone().normalized())
        .map_err(|error| format!("Unable to serialize Den operator settings: {error}"))?;
    let atomic_file = AtomicFile::new(&settings_path, OverwriteBehavior::AllowOverwrite);
    atomic_file
        .write(|file| {
            file.write_all(&payload)?;
            file.write_all(b"\n")?;
            file.sync_all()
        })
        .map_err(|error| format!("Unable to save Den operator settings: {error}"))?;

    Ok(())
}

pub fn settings_path(app: &AppHandle) -> Result<PathBuf, String> {
    let config_dir = app
        .path()
        .app_config_dir()
        .map_err(|error| format!("Unable to resolve Den operator settings directory: {error}"))?;
    Ok(config_dir.join(SETTINGS_FILE_NAME))
}

fn trim_to_option(value: &str) -> Option<String> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed.to_string())
    }
}
