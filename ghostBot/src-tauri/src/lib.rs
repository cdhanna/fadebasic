mod claude;
mod commands;
mod icon_validation;
mod inference;
mod local_network;
mod model_catalog;
mod model_manager;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    // Must come from the app process — WKWebView's WebRTC never triggers
    // the macOS Local Network prompt itself (see local_network.rs).
    local_network::trigger_local_network_prompt();

    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .manage(std::sync::Arc::new(inference::InferenceState::new()))
        .manage(std::sync::Arc::new(claude::ClaudeState::new()))
        .manage(model_manager::ModelStore::new())
        .invoke_handler(tauri::generate_handler![
            commands::get_setup_state,
            commands::list_downloadable_models,
            commands::list_models,
            commands::open_models_folder,
            commands::load_model,
            commands::unload_model,
            commands::download_model,
            commands::download_recommended_model,
            commands::start_stream,
            commands::abort_stream,
            commands::get_claude_config,
            commands::set_claude_config,
            commands::start_claude_stream,
        ])
        .run(tauri::generate_context!())
        .expect("error while running GhostBot");
}
