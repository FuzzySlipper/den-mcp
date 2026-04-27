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

    pub fn from_save_request(
        current: &OperatorSettings,
        request: SaveOperatorSettingsRequest,
    ) -> Self {
        Self {
            den_base_url: request.den_base_url,
            source_instance_id: current.source_instance_id.clone(),
            source_display_name: request
                .source_display_name
                .and_then(|value| trim_to_option(&value)),
            poll_interval_seconds: request
                .poll_interval_seconds
                .unwrap_or(current.poll_interval_seconds),
            max_changed_files: request
                .max_changed_files
                .unwrap_or(current.max_changed_files),
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

    load_settings_from_path(&path, || {
        let settings = OperatorSettings::default().normalized();
        if let Err(save_error) = save_settings_to_path(&path, &settings) {
            log::warn!("Unable to write default Den operator settings: {save_error}");
        }
        settings
    })
}

pub fn save_settings(app: &AppHandle, settings: &OperatorSettings) -> Result<(), String> {
    let settings_path = settings_path(app)?;
    save_settings_to_path(&settings_path, settings)
}

fn load_settings_from_path(
    path: &PathBuf,
    default_factory: impl FnOnce() -> OperatorSettings,
) -> OperatorSettings {
    let contents = match fs::read_to_string(path) {
        Ok(contents) => contents,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return default_factory(),
        Err(error) => {
            log::warn!(
                "Unable to read Den operator settings from {}: {error}",
                path.display()
            );
            return OperatorSettings::default().normalized();
        }
    };

    match serde_json::from_str::<OperatorSettings>(&contents) {
        Ok(settings) => settings.normalized(),
        Err(error) => {
            log::warn!(
                "Unable to parse Den operator settings from {}: {error}",
                path.display()
            );
            OperatorSettings::default().normalized()
        }
    }
}

fn save_settings_to_path(
    settings_path: &PathBuf,
    settings: &OperatorSettings,
) -> Result<(), String> {
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
    let atomic_file = AtomicFile::new(settings_path, OverwriteBehavior::AllowOverwrite);
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

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_settings_path(name: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "den-desktop-{name}-{}.json",
            Uuid::new_v4().simple()
        ))
    }

    #[test]
    fn save_and_load_settings_round_trip_preserves_source_instance_id() {
        let path = temp_settings_path("settings-round-trip");
        let settings = OperatorSettings {
            den_base_url: "http://localhost:5199/".to_string(),
            source_instance_id: "stable-source".to_string(),
            source_display_name: Some("  Desk  ".to_string()),
            poll_interval_seconds: 2,
            max_changed_files: 10_000,
        };

        save_settings_to_path(&path, &settings).expect("settings should save");
        let loaded = load_settings_from_path(&path, OperatorSettings::default);

        assert_eq!(loaded.den_base_url, "http://localhost:5199");
        assert_eq!(loaded.source_instance_id, "stable-source");
        assert_eq!(loaded.source_display_name.as_deref(), Some("Desk"));
        assert_eq!(loaded.poll_interval_seconds, 5);
        assert_eq!(loaded.max_changed_files, 2000);
        let _ = fs::remove_file(path);
    }

    #[test]
    fn save_request_keeps_existing_source_instance_id() {
        let current = OperatorSettings {
            den_base_url: "http://old".to_string(),
            source_instance_id: "stable-source".to_string(),
            source_display_name: Some("Old".to_string()),
            poll_interval_seconds: 30,
            max_changed_files: 200,
        };

        let next = OperatorSettings::from_save_request(
            &current,
            SaveOperatorSettingsRequest {
                den_base_url: " http://new/ ".to_string(),
                source_display_name: Some(" New Desk ".to_string()),
                poll_interval_seconds: Some(60),
                max_changed_files: Some(400),
            },
        );

        assert_eq!(next.source_instance_id, "stable-source");
        assert_eq!(next.den_base_url, "http://new");
        assert_eq!(next.source_display_name.as_deref(), Some("New Desk"));
        assert_eq!(next.poll_interval_seconds, 60);
        assert_eq!(next.max_changed_files, 400);
    }
}
