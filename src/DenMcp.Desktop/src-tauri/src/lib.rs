mod den_client;
mod git;
mod runtime;
mod settings;

use runtime::{
    get_operator_status, get_settings, list_local_snapshots, refresh_now, save_operator_settings,
    start_runtime, OperatorRuntime,
};
use tauri::Manager;

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_log::Builder::new().build())
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.show();
                let _ = window.set_focus();
            }
        }))
        .manage(OperatorRuntime::default())
        .invoke_handler(tauri::generate_handler![
            get_operator_status,
            get_settings,
            save_operator_settings,
            refresh_now,
            list_local_snapshots
        ])
        .setup(|app| {
            let app_handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                start_runtime(app_handle).await;
            });
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running Den operator desktop app");
}
