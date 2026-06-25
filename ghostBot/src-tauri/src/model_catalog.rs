use serde::Serialize;
use std::path::Path;

#[derive(Clone, Serialize)]
pub struct CatalogModel {
    pub id: &'static str,
    pub label: &'static str,
    pub filename: &'static str,
    pub url: &'static str,
    /// Rough download size shown in the UI.
    pub size_label: &'static str,
    /// Minimum on-disk size (MB) before we warn the file may be incomplete.
    pub min_size_mb: u64,
    pub description: &'static str,
    pub recommended: bool,
}

pub const CATALOG: &[CatalogModel] = &[
    // ── Recommended: code-tuned, same footprint as the old 7B base ──────────
    CatalogModel {
        id: "qwen2.5-coder-7b-q4km",
        label: "Qwen2.5 Coder 7B Instruct",
        filename: "Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf",
        url: "https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf",
        size_label: "~4.7 GB",
        min_size_mb: 4000,
        description: "Recommended. Code-tuned — much better at Fade syntax + tools than the general 7B, same size.",
        recommended: true,
    },
    CatalogModel {
        id: "qwen2.5-coder-7b-q5km",
        label: "Qwen2.5 Coder 7B (Q5)",
        filename: "Qwen2.5-Coder-7B-Instruct-Q5_K_M.gguf",
        url: "https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-7B-Instruct-Q5_K_M.gguf",
        size_label: "~5.4 GB",
        min_size_mb: 4800,
        description: "Higher-fidelity quant of the Coder 7B — fewer malformed tokens. A bit more RAM.",
        recommended: false,
    },
    // ── Larger code models — much stronger, if you have the RAM ─────────────
    CatalogModel {
        id: "qwen2.5-coder-14b-q4km",
        label: "Qwen2.5 Coder 14B Instruct",
        filename: "Qwen2.5-Coder-14B-Instruct-Q4_K_M.gguf",
        url: "https://huggingface.co/bartowski/Qwen2.5-Coder-14B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-14B-Instruct-Q4_K_M.gguf",
        size_label: "~9.0 GB",
        min_size_mb: 8000,
        description: "Bigger code model — follows instructions/syntax noticeably better. Needs ~16 GB free RAM.",
        recommended: false,
    },
    CatalogModel {
        id: "qwen2.5-coder-32b-q4km",
        label: "Qwen2.5 Coder 32B Instruct",
        filename: "Qwen2.5-Coder-32B-Instruct-Q4_K_M.gguf",
        url: "https://huggingface.co/bartowski/Qwen2.5-Coder-32B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-32B-Instruct-Q4_K_M.gguf",
        size_label: "~20 GB",
        min_size_mb: 18000,
        description: "Near-frontier local code quality — mostly gets Fade right. Needs ~32 GB+ RAM.",
        recommended: false,
    },
    CatalogModel {
        id: "qwen3-coder-30b-a3b-q4km",
        label: "Qwen3 Coder 30B-A3B Instruct",
        filename: "Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf",
        url: "https://huggingface.co/unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF/resolve/main/Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf",
        size_label: "~18 GB",
        min_size_mb: 16000,
        description: "Mixture-of-experts (~3B active) — 32B-class quality, faster. Needs a recent runtime + ~24 GB RAM.",
        recommended: false,
    },
    CatalogModel {
        id: "gpt-oss-20b-mxfp4",
        label: "gpt-oss 20B (MXFP4)",
        filename: "gpt-oss-20b-mxfp4.gguf",
        url: "https://huggingface.co/ggml-org/gpt-oss-20b-GGUF/resolve/main/gpt-oss-20b-mxfp4.gguf",
        size_label: "~12 GB",
        min_size_mb: 10000,
        description: "OpenAI open-weight, strong reasoning + tools. Needs a recent llama.cpp build — may not load on older runtimes.",
        recommended: false,
    },
    // ── Lighter / legacy options ────────────────────────────────────────────
    CatalogModel {
        id: "qwen2.5-7b-q4km",
        label: "Qwen2.5 7B Instruct (general)",
        filename: "Qwen2.5-7B-Instruct-Q4_K_M.gguf",
        url: "https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q4_K_M.gguf",
        size_label: "~4.7 GB",
        min_size_mb: 4000,
        description: "General (non-code) 7B. Prefer the Coder 7B above for Fade work.",
        recommended: false,
    },
    CatalogModel {
        id: "qwen2.5-3b-q4km",
        label: "Qwen2.5 3B Instruct",
        filename: "Qwen2.5-3B-Instruct-Q4_K_M.gguf",
        url: "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf",
        size_label: "~2.0 GB",
        min_size_mb: 1700,
        description: "Faster downloads and lower VRAM. Good for lighter machines.",
        recommended: false,
    },
    CatalogModel {
        id: "qwen2.5-7b-q5km",
        label: "Qwen2.5 7B Instruct (Q5)",
        filename: "Qwen2.5-7B-Instruct-Q5_K_M.gguf",
        url: "https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q5_K_M.gguf",
        size_label: "~5.4 GB",
        min_size_mb: 4800,
        description: "Higher quality quant. Needs a bit more disk and VRAM.",
        recommended: false,
    },
    CatalogModel {
        id: "llama-3.2-3b-q4km",
        label: "Llama 3.2 3B Instruct",
        filename: "Llama-3.2-3B-Instruct-Q4_K_M.gguf",
        url: "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf",
        size_label: "~2.0 GB",
        min_size_mb: 1700,
        description: "Compact Meta instruct model — alternative to Qwen 3B.",
        recommended: false,
    },
    CatalogModel {
        id: "phi-3.5-mini-q4km",
        label: "Phi-3.5 Mini Instruct",
        filename: "Phi-3.5-mini-instruct-Q4_K_M.gguf",
        url: "https://huggingface.co/bartowski/Phi-3.5-mini-instruct-GGUF/resolve/main/Phi-3.5-mini-instruct-Q4_K_M.gguf",
        size_label: "~2.2 GB",
        min_size_mb: 1900,
        description: "Small Microsoft model — quick responses, modest VRAM.",
        recommended: false,
    },
];

pub fn find_catalog_model(model_id: &str) -> Option<&'static CatalogModel> {
    CATALOG.iter().find(|m| m.id == model_id)
}

pub fn find_by_filename(filename: &str) -> Option<&'static CatalogModel> {
    CATALOG.iter().find(|m| m.filename == filename)
}

pub fn file_status(path: &Path, entry: &CatalogModel) -> (bool, u64, bool) {
    if !path.exists() {
        return (false, 0, false);
    }
    let size_mb = path
        .metadata()
        .map(|m| m.len() / (1024 * 1024))
        .unwrap_or(0);
    let incomplete = size_mb < entry.min_size_mb;
    (true, size_mb, incomplete)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn catalog_has_unique_ids_and_filenames() {
        let mut ids = std::collections::HashSet::new();
        let mut names = std::collections::HashSet::new();
        for m in CATALOG {
            assert!(ids.insert(m.id), "duplicate id {}", m.id);
            assert!(names.insert(m.filename), "duplicate filename {}", m.filename);
        }
        assert!(CATALOG.iter().any(|m| m.recommended));
    }

    #[test]
    fn find_recommended_model() {
        let rec = find_catalog_model("qwen2.5-coder-7b-q4km").expect("recommended model");
        assert!(rec.recommended);
        assert!(rec.url.contains("Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf"));
    }

    #[test]
    fn exactly_one_recommended() {
        assert_eq!(CATALOG.iter().filter(|m| m.recommended).count(), 1);
    }
}
