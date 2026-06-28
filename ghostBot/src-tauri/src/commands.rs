use crate::inference::{InferenceError, InferenceState};
use crate::model_catalog::CatalogModel;
use crate::model_manager::{find_catalog_model, ModelStore, RECOMMENDED_MODEL_ID};
use futures_util::StreamExt;
use serde::Serialize;
use std::sync::Arc;
use std::time::Instant;
use tauri::{AppHandle, Emitter, State};

#[derive(Serialize)]
pub struct LoadedModelInfo {
    pub name: String,
    pub path: String,
}

#[derive(Serialize)]
pub struct SetupState {
    pub models_dir: String,
    pub model_count: usize,
    pub loaded_model: Option<String>,
    pub recommended_id: String,
    pub downloadable: Vec<crate::model_manager::DownloadableModelInfo>,
}

#[tauri::command]
pub fn get_setup_state(
    store: State<'_, ModelStore>,
    inference: State<'_, Arc<InferenceState>>,
) -> Result<SetupState, String> {
    let models = store.list().map_err(|e| e.to_string())?;
    Ok(SetupState {
        models_dir: store.models_dir().to_string_lossy().to_string(),
        model_count: models.len(),
        loaded_model: inference.loaded_info().map(|(name, _)| name),
        recommended_id: RECOMMENDED_MODEL_ID.to_string(),
        downloadable: store.list_downloadable(),
    })
}

#[tauri::command]
pub fn list_downloadable_models(
    store: State<'_, ModelStore>,
) -> Result<Vec<crate::model_manager::DownloadableModelInfo>, String> {
    Ok(store.list_downloadable())
}

#[tauri::command]
pub fn list_models(store: State<'_, ModelStore>) -> Result<Vec<crate::model_manager::ModelEntry>, String> {
    store.list().map_err(|e| e.to_string())
}

#[tauri::command]
pub fn open_models_folder(store: State<'_, ModelStore>) -> Result<String, String> {
    let dir = store.models_dir().to_path_buf();
    open::that(&dir).map_err(|e| e.to_string())?;
    Ok(dir.to_string_lossy().to_string())
}

#[tauri::command]
pub fn load_model(
    model_id: String,
    store: State<'_, ModelStore>,
    inference: State<'_, Arc<InferenceState>>,
) -> Result<LoadedModelInfo, String> {
    let path = store.resolve_path(&model_id).map_err(|e| e.to_string())?;
    if let Some(name) = path.file_name().and_then(|s| s.to_str()) {
        if let Some(cat) = crate::model_catalog::find_by_filename(name) {
            let (_, size_mb, incomplete) = crate::model_catalog::file_status(&path, cat);
            if incomplete {
                return Err(format!(
                    "Model file looks incomplete ({size_mb} MB, expected at least {} MB). \
                     Open the models folder, delete \"{name}\", and download again.",
                    cat.min_size_mb
                ));
            }
        }
    }
    let label = path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or(&model_id)
        .to_string();
    inference.load(&path, &label).map_err(|e| {
        let msg = e.to_string();
        if msg.contains("corrupted") || msg.contains("incomplete") || msg.contains("file bounds") {
            format!(
                "{msg}\n\nThe GGUF file may be truncated. Open the models folder, delete the \
                 partial download, and try downloading again."
            )
        } else {
            msg
        }
    })?;
    Ok(LoadedModelInfo {
        name: label,
        path: path.to_string_lossy().to_string(),
    })
}

#[tauri::command]
pub fn unload_model(inference: State<'_, Arc<InferenceState>>) -> Result<(), String> {
    inference.unload().map_err(|e| e.to_string())
}

fn emit_download_progress(
    app: &AppHandle,
    pct: f64,
    downloaded: u64,
    total: u64,
    speed_bps: f64,
    phase: &str,
    label: &str,
) {
    let downloaded_mb = downloaded as f64 / 1_048_576.0;
    let total_mb = if total > 0 { total as f64 / 1_048_576.0 } else { 0.0 };
    let speed_mbps = speed_bps / 1_048_576.0;
    let eta_sec = if speed_bps > 0.0 && total > downloaded {
        ((total - downloaded) as f64 / speed_bps) as u64
    } else {
        0
    };
    let _ = app.emit(
        "download-progress",
        serde_json::json!({
            "pct": pct,
            "downloadedMb": downloaded_mb,
            "totalMb": total_mb,
            "speedMbps": speed_mbps,
            "etaSec": eta_sec,
            "phase": phase,
            "label": label,
            "text": if total > 0 {
                format!("{downloaded_mb:.1} / {total_mb:.1} MB")
            } else {
                format!("{downloaded_mb:.1} MB downloaded")
            },
        }),
    );
}

async fn download_gguf(
    app: AppHandle,
    store: &ModelStore,
    entry: &CatalogModel,
    force: bool,
) -> Result<(), String> {
    let dest = store.catalog_path(entry);
    if dest.exists() && !force {
        let (_, _, incomplete) = crate::model_catalog::file_status(&dest, entry);
        if !incomplete {
            let size = dest.metadata().map(|m| m.len()).unwrap_or(0);
            emit_download_progress(&app, 1.0, size, size, 0.0, "complete", entry.label);
            return Ok(());
        }
        std::fs::remove_file(&dest).map_err(|e| e.to_string())?;
    }

    emit_download_progress(&app, 0.0, 0, 0, 0.0, "starting", entry.label);

    let client = reqwest::Client::builder()
        .user_agent("GhostBot/0.1")
        .build()
        .map_err(|e| e.to_string())?;

    let resp = client
        .get(entry.url)
        .send()
        .await
        .map_err(|e| e.to_string())?;

    if !resp.status().is_success() {
        return Err(format!("download failed: HTTP {}", resp.status()));
    }

    let total = resp.content_length().unwrap_or(0);
    emit_download_progress(&app, 0.0, 0, total, 0.0, "downloading", entry.label);

    let mut stream = resp.bytes_stream();
    let mut file = tokio::fs::File::create(&dest)
        .await
        .map_err(|e| e.to_string())?;

    let mut downloaded: u64 = 0;
    let started = Instant::now();
    let mut last_emit = Instant::now();

    use tokio::io::AsyncWriteExt;
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|e| e.to_string())?;
        file.write_all(&chunk).await.map_err(|e| e.to_string())?;
        downloaded += chunk.len() as u64;

        let elapsed = started.elapsed().as_secs_f64().max(0.001);
        let speed = downloaded as f64 / elapsed;

        if last_emit.elapsed().as_millis() >= 200 || downloaded == total {
            let pct = if total > 0 {
                downloaded as f64 / total as f64
            } else {
                0.0
            };
            emit_download_progress(&app, pct, downloaded, total, speed, "downloading", entry.label);
            last_emit = Instant::now();
        }
    }

    file.flush().await.map_err(|e| e.to_string())?;
    let final_size = dest.metadata().map(|m| m.len()).unwrap_or(downloaded);
    emit_download_progress(
        &app,
        1.0,
        final_size,
        final_size.max(total),
        0.0,
        "complete",
        entry.label,
    );
    Ok(())
}

#[tauri::command]
pub async fn download_model(
    model_id: String,
    force: Option<bool>,
    app: AppHandle,
    store: State<'_, ModelStore>,
) -> Result<(), String> {
    let entry = find_catalog_model(&model_id)
        .ok_or_else(|| format!("unknown downloadable model: {model_id}"))?;
    download_gguf(app, &store, entry, force.unwrap_or(false)).await
}

#[tauri::command]
pub async fn download_recommended_model(
    app: AppHandle,
    store: State<'_, ModelStore>,
) -> Result<(), String> {
    let entry = find_catalog_model(RECOMMENDED_MODEL_ID)
        .ok_or_else(|| "recommended model missing from catalog".to_string())?;
    download_gguf(app, &store, entry, false).await
}

#[tauri::command]
pub async fn start_stream(
    app: AppHandle,
    stream_id: u32,
    prompt: String,
    max_tokens: u32,
    temperature: f32,
    inference: State<'_, Arc<InferenceState>>,
) -> Result<(), String> {
    let inference = Arc::clone(&*inference);
    let app2 = app.clone();
    tokio::task::spawn_blocking(move || {
        let result = inference.stream(&prompt, max_tokens, temperature, |piece| {
            let _ = app2.emit(
                "ghost-token",
                serde_json::json!({ "streamId": stream_id, "delta": piece }),
            );
            !inference.should_abort()
        });
        match result {
            Ok(()) => {
                let _ = app2.emit(
                    "ghost-token",
                    serde_json::json!({ "streamId": stream_id, "done": true }),
                );
            }
            Err(InferenceError::Msg(m)) => {
                let _ = app2.emit(
                    "ghost-token",
                    serde_json::json!({ "streamId": stream_id, "error": m }),
                );
            }
        }
    });
    Ok(())
}

#[tauri::command]
pub fn abort_stream(
    inference: State<'_, Arc<InferenceState>>,
    claude: State<'_, Arc<crate::claude::ClaudeState>>,
) -> Result<(), String> {
    // Abort whichever provider is mid-stream — cheap to signal both.
    inference.request_abort();
    claude.request_abort();
    Ok(())
}

// ── Claude API proxy ─────────────────────────────────────────────────────────

#[tauri::command]
pub fn get_claude_config(
    claude: State<'_, Arc<crate::claude::ClaudeState>>,
) -> Result<crate::claude::ClaudeConfigView, String> {
    Ok(claude.view())
}

#[tauri::command]
pub fn set_claude_config(
    model: Option<String>,
    api_key: Option<String>,
    claude: State<'_, Arc<crate::claude::ClaudeState>>,
) -> Result<crate::claude::ClaudeConfigView, String> {
    claude.set(model, api_key);
    Ok(claude.view())
}

#[tauri::command]
pub async fn start_claude_stream(
    app: AppHandle,
    stream_id: u32,
    messages: Vec<crate::claude::ChatMsg>,
    max_tokens: u32,
    temperature: f32,
    claude: State<'_, Arc<crate::claude::ClaudeState>>,
) -> Result<(), String> {
    let state = Arc::clone(&*claude);
    tokio::spawn(async move {
        crate::claude::stream(app, state, stream_id, messages, max_tokens, temperature).await;
    });
    Ok(())
}
