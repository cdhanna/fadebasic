use crate::model_catalog::{self, CatalogModel};
use serde::Serialize;
use std::path::{Path, PathBuf};
use thiserror::Error;

#[derive(Debug, Error)]
pub enum ModelError {
    #[error("{0}")]
    Msg(String),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Http(#[from] reqwest::Error),
}

#[derive(Clone, Serialize)]
pub struct ModelEntry {
    pub id: String,
    pub label: String,
    pub path: String,
    pub size_mb: u64,
    pub incomplete: bool,
}

#[derive(Clone, Serialize)]
pub struct DownloadableModelInfo {
    pub id: String,
    pub label: String,
    pub filename: String,
    pub size_label: String,
    pub description: String,
    pub recommended: bool,
    pub downloaded: bool,
    pub size_mb: u64,
    pub incomplete: bool,
}

pub use model_catalog::find_catalog_model;

pub const RECOMMENDED_MODEL_ID: &str = "qwen2.5-coder-7b-q4km";

pub struct ModelStore {
    models_dir: PathBuf,
}

impl ModelStore {
    pub fn new() -> Self {
        let base = dirs::data_local_dir()
            .unwrap_or_else(|| PathBuf::from("."))
            .join("ghostbot")
            .join("models");
        let _ = std::fs::create_dir_all(&base);
        Self { models_dir: base }
    }

    pub fn models_dir(&self) -> &Path {
        &self.models_dir
    }

    pub fn catalog_path(&self, entry: &CatalogModel) -> PathBuf {
        self.models_dir.join(entry.filename)
    }

    pub fn list_downloadable(&self) -> Vec<DownloadableModelInfo> {
        model_catalog::CATALOG
            .iter()
            .map(|entry| {
                let path = self.catalog_path(entry);
                let (downloaded, size_mb, incomplete) = model_catalog::file_status(&path, entry);
                DownloadableModelInfo {
                    id: entry.id.to_string(),
                    label: entry.label.to_string(),
                    filename: entry.filename.to_string(),
                    size_label: entry.size_label.to_string(),
                    description: entry.description.to_string(),
                    recommended: entry.recommended,
                    downloaded,
                    size_mb,
                    incomplete,
                }
            })
            .collect()
    }

    pub fn list(&self) -> Result<Vec<ModelEntry>, ModelError> {
        let mut out = Vec::new();
        for entry in std::fs::read_dir(&self.models_dir)? {
            let entry = entry?;
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("gguf") {
                continue;
            }
            let name = path
                .file_name()
                .and_then(|n| n.to_str())
                .unwrap_or("model")
                .to_string();
            let id = slugify(&name);
            let size_mb = entry.metadata()?.len() / (1024 * 1024);
            let incomplete = model_catalog::find_by_filename(&name)
                .map(|cat| size_mb < cat.min_size_mb)
                .unwrap_or(false);
            out.push(ModelEntry {
                id,
                label: name.trim_end_matches(".gguf").to_string(),
                path: path.to_string_lossy().to_string(),
                size_mb,
                incomplete,
            });
        }
        out.sort_by(|a, b| a.label.cmp(&b.label));
        Ok(out)
    }

    pub fn resolve_path(&self, model_id: &str) -> Result<PathBuf, ModelError> {
        if let Some(cat) = find_catalog_model(model_id) {
            let path = self.catalog_path(cat);
            if path.exists() {
                return Ok(path);
            }
        }
        for m in self.list()? {
            if m.id == model_id {
                return Ok(PathBuf::from(m.path));
            }
        }
        Err(ModelError::Msg(format!("unknown model id: {model_id}")))
    }
}

pub(crate) fn slugify(name: &str) -> String {
    name.to_lowercase()
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() { c } else { '-' })
        .collect::<String>()
        .trim_matches('-')
        .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slugify_strips_gguf_and_special_chars() {
        assert_eq!(
            slugify("Qwen2.5-7B-Instruct-Q4_K_M.gguf"),
            "qwen2-5-7b-instruct-q4-k-m-gguf",
        );
    }

    #[test]
    fn recommended_id_matches_catalog() {
        assert!(find_catalog_model(RECOMMENDED_MODEL_ID).is_some());
    }
}
