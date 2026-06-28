// Claude API proxy "model" for GhostBot.
//
// GhostBot normally serves a local llama.cpp model. This module adds an
// alternative: forward inference to the Anthropic Messages API, so the same
// Playground agent loop can run against Opus/Sonnet/Haiku using the user's own
// API key. The key + chosen model are persisted to a small JSON file in the
// app data dir; the key never leaves the native side once set.
//
// Streaming mirrors the local path: we emit the same `ghost-token` events
// (`{streamId, delta}` / `{streamId, done}` / `{streamId, error}`) so the TS
// consumer is shared between local and cloud.

use futures_util::StreamExt;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
use tauri::{AppHandle, Emitter};

const ANTHROPIC_URL: &str = "https://api.anthropic.com/v1/messages";
const ANTHROPIC_VERSION: &str = "2023-06-01";

/// A chat message as it arrives from the Playground (same shape as GhostMsg).
#[derive(Debug, Clone, Deserialize)]
pub struct ChatMsg {
    pub role: String,
    pub content: String,
}

#[derive(Clone, Serialize, Deserialize)]
pub struct ClaudeConfig {
    #[serde(default = "default_model")]
    pub model: String,
    #[serde(default)]
    pub api_key: String,
}

fn default_model() -> String {
    "claude-sonnet-4-6".to_string()
}

impl Default for ClaudeConfig {
    fn default() -> Self {
        Self { model: default_model(), api_key: String::new() }
    }
}

#[derive(Serialize)]
pub struct ClaudeModelOption {
    pub id: &'static str,
    pub label: &'static str,
}

/// The Claude models offered in the config dropdown.
pub const CLAUDE_MODELS: &[ClaudeModelOption] = &[
    ClaudeModelOption { id: "claude-opus-4-8", label: "Claude Opus 4.8 — most capable" },
    ClaudeModelOption { id: "claude-sonnet-4-6", label: "Claude Sonnet 4.6 — fast & balanced" },
    ClaudeModelOption { id: "claude-haiku-4-5-20251001", label: "Claude Haiku 4.5 — fastest" },
];

/// What the UI needs to render the config screen (never returns the raw key).
#[derive(Serialize)]
pub struct ClaudeConfigView {
    pub model: String,
    pub has_key: bool,
    pub models: Vec<ClaudeModelOptionView>,
}

#[derive(Serialize)]
pub struct ClaudeModelOptionView {
    pub id: String,
    pub label: String,
}

pub struct ClaudeState {
    cfg: Mutex<ClaudeConfig>,
    path: PathBuf,
    abort: AtomicBool,
}

impl ClaudeState {
    pub fn new() -> Self {
        let path = dirs::data_local_dir()
            .unwrap_or_else(|| PathBuf::from("."))
            .join("ghostbot")
            .join("claude.json");
        let cfg = std::fs::read_to_string(&path)
            .ok()
            .and_then(|s| serde_json::from_str::<ClaudeConfig>(&s).ok())
            .unwrap_or_default();
        Self { cfg: Mutex::new(cfg), path, abort: AtomicBool::new(false) }
    }

    pub fn view(&self) -> ClaudeConfigView {
        let c = self.cfg.lock().unwrap();
        ClaudeConfigView {
            model: c.model.clone(),
            has_key: !c.api_key.trim().is_empty(),
            models: CLAUDE_MODELS
                .iter()
                .map(|m| ClaudeModelOptionView { id: m.id.to_string(), label: m.label.to_string() })
                .collect(),
        }
    }

    /// Update config. `model`/`api_key` are applied only when provided; an
    /// explicit empty `api_key` clears the stored key. Persists to disk.
    pub fn set(&self, model: Option<String>, api_key: Option<String>) {
        {
            let mut c = self.cfg.lock().unwrap();
            if let Some(m) = model {
                if !m.trim().is_empty() {
                    c.model = m;
                }
            }
            if let Some(k) = api_key {
                c.api_key = k.trim().to_string();
            }
            if let Some(parent) = self.path.parent() {
                let _ = std::fs::create_dir_all(parent);
            }
            if let Ok(json) = serde_json::to_string_pretty(&*c) {
                let _ = std::fs::write(&self.path, json);
            }
        }
    }

    fn snapshot(&self) -> ClaudeConfig {
        self.cfg.lock().unwrap().clone()
    }

    pub fn request_abort(&self) {
        self.abort.store(true, Ordering::SeqCst);
    }
    pub fn clear_abort(&self) {
        self.abort.store(false, Ordering::SeqCst);
    }
    pub fn should_abort(&self) -> bool {
        self.abort.load(Ordering::SeqCst)
    }
}

fn emit_token(app: &AppHandle, stream_id: u32, value: serde_json::Value) {
    let mut obj = serde_json::Map::new();
    obj.insert("streamId".into(), serde_json::json!(stream_id));
    if let serde_json::Value::Object(m) = value {
        for (k, v) in m {
            obj.insert(k, v);
        }
    }
    let _ = app.emit("ghost-token", serde_json::Value::Object(obj));
}

/// Map the Playground's flat message list to Anthropic's shape: a top-level
/// `system` string plus alternating user/assistant turns (consecutive same-role
/// messages are merged; `tool` is folded into `user`). Returns (system, messages).
fn build_anthropic_messages(msgs: &[ChatMsg]) -> (String, Vec<serde_json::Value>) {
    let mut system = String::new();
    let mut out: Vec<(String, String)> = Vec::new();
    for m in msgs {
        match m.role.as_str() {
            "system" => {
                if !system.is_empty() {
                    system.push_str("\n\n");
                }
                system.push_str(&m.content);
            }
            role => {
                let role = if role == "assistant" { "assistant" } else { "user" };
                if let Some(last) = out.last_mut() {
                    if last.0 == role {
                        last.1.push_str("\n\n");
                        last.1.push_str(&m.content);
                        continue;
                    }
                }
                out.push((role.to_string(), m.content.clone()));
            }
        }
    }
    let messages = out
        .into_iter()
        .map(|(role, content)| serde_json::json!({ "role": role, "content": content }))
        .collect();
    (system, messages)
}

/// Stream a completion from the Anthropic API, emitting `ghost-token` events.
/// Errors are surfaced as an `{error}` token rather than returned.
pub async fn stream(
    app: AppHandle,
    state: std::sync::Arc<ClaudeState>,
    stream_id: u32,
    messages: Vec<ChatMsg>,
    max_tokens: u32,
    temperature: f32,
) {
    state.clear_abort();
    let cfg = state.snapshot();
    if cfg.api_key.trim().is_empty() {
        emit_token(&app, stream_id, serde_json::json!({ "error": "No Anthropic API key set. Open GhostBot → Claude API and paste your key." }));
        return;
    }

    let (system, anth_messages) = build_anthropic_messages(&messages);
    if anth_messages.is_empty() {
        emit_token(&app, stream_id, serde_json::json!({ "error": "No user message to send." }));
        return;
    }

    let mut body = serde_json::json!({
        "model": cfg.model,
        "max_tokens": max_tokens.max(1),
        "temperature": temperature,
        "messages": anth_messages,
        "stream": true,
    });
    if !system.trim().is_empty() {
        body["system"] = serde_json::json!(system);
    }

    // Diagnostic (no secrets): model + key fingerprint so a 401/400 is debuggable
    // from the GhostBot console without ever printing the key.
    let key = cfg.api_key.trim();
    eprintln!(
        "[claude] request model={} key=len{}:{}…{} system={}b msgs={}",
        cfg.model,
        key.len(),
        &key[..key.len().min(7)],
        if key.len() > 4 { &key[key.len() - 4..] } else { "" },
        system.len(),
        anth_messages.len(),
    );

    let client = reqwest::Client::new();
    let resp = client
        .post(ANTHROPIC_URL)
        .header("x-api-key", key)
        .header("anthropic-version", ANTHROPIC_VERSION)
        .header("content-type", "application/json")
        .json(&body)
        .send()
        .await;

    let resp = match resp {
        Ok(r) => r,
        Err(e) => {
            emit_token(&app, stream_id, serde_json::json!({ "error": format!("Request failed: {e}") }));
            return;
        }
    };

    if !resp.status().is_success() {
        let status = resp.status();
        let text = resp.text().await.unwrap_or_default();
        eprintln!("[claude] HTTP {status}: {}", text.chars().take(300).collect::<String>());
        let detail = serde_json::from_str::<serde_json::Value>(&text)
            .ok()
            .and_then(|v| v.get("error").and_then(|e| e.get("message")).and_then(|m| m.as_str()).map(|s| s.to_string()))
            .unwrap_or_else(|| text.chars().take(200).collect());
        emit_token(&app, stream_id, serde_json::json!({ "error": format!("Anthropic API error ({status}): {detail}") }));
        return;
    }

    // Parse the SSE stream: lines like `data: {json}`. We only need text deltas
    // and the stop event; everything else (ping, message_start, …) is ignored.
    let mut byte_stream = resp.bytes_stream();
    let mut buf = String::new();
    let mut finished = false;
    while let Some(chunk) = byte_stream.next().await {
        if state.should_abort() {
            break;
        }
        let bytes = match chunk {
            Ok(b) => b,
            Err(e) => {
                emit_token(&app, stream_id, serde_json::json!({ "error": format!("Stream error: {e}") }));
                return;
            }
        };
        buf.push_str(&String::from_utf8_lossy(&bytes));

        while let Some(nl) = buf.find('\n') {
            let line: String = buf.drain(..=nl).collect();
            let line = line.trim();
            let Some(data) = line.strip_prefix("data:") else { continue };
            let data = data.trim();
            if data.is_empty() || data == "[DONE]" {
                continue;
            }
            let Ok(v) = serde_json::from_str::<serde_json::Value>(data) else { continue };
            match v.get("type").and_then(|t| t.as_str()) {
                Some("content_block_delta") => {
                    if let Some(text) = v.get("delta").and_then(|d| d.get("text")).and_then(|t| t.as_str()) {
                        if !text.is_empty() {
                            emit_token(&app, stream_id, serde_json::json!({ "delta": text }));
                        }
                    }
                }
                Some("message_stop") => {
                    finished = true;
                    break;
                }
                Some("error") => {
                    let msg = v
                        .get("error")
                        .and_then(|e| e.get("message"))
                        .and_then(|m| m.as_str())
                        .unwrap_or("unknown error");
                    emit_token(&app, stream_id, serde_json::json!({ "error": format!("Anthropic error: {msg}") }));
                    return;
                }
                _ => {}
            }
        }
        if finished {
            break;
        }
    }

    emit_token(&app, stream_id, serde_json::json!({ "done": true }));
}

#[cfg(test)]
mod tests {
    use super::*;

    fn msg(role: &str, content: &str) -> ChatMsg {
        ChatMsg { role: role.to_string(), content: content.to_string() }
    }

    #[test]
    fn extracts_system_and_keeps_user() {
        let (system, messages) = build_anthropic_messages(&[
            msg("system", "rules"),
            msg("user", "hi"),
        ]);
        assert_eq!(system, "rules");
        assert_eq!(messages.len(), 1);
        assert_eq!(messages[0]["role"], "user");
        assert_eq!(messages[0]["content"], "hi");
    }

    #[test]
    fn joins_multiple_system_messages() {
        let (system, _) = build_anthropic_messages(&[
            msg("system", "a"),
            msg("system", "b"),
            msg("user", "x"),
        ]);
        assert_eq!(system, "a\n\nb");
    }

    #[test]
    fn merges_consecutive_same_role_and_folds_tool_into_user() {
        let (_, messages) = build_anthropic_messages(&[
            msg("user", "one"),
            msg("tool", "result"),     // tool → user, merged with the previous user
            msg("assistant", "reply"),
            msg("user", "two"),
        ]);
        assert_eq!(messages.len(), 3);
        assert_eq!(messages[0]["role"], "user");
        assert_eq!(messages[0]["content"], "one\n\nresult");
        assert_eq!(messages[1]["role"], "assistant");
        assert_eq!(messages[2]["role"], "user");
        assert_eq!(messages[2]["content"], "two");
    }
}
