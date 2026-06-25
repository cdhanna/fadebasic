use llama_cpp_2::context::params::LlamaContextParams;
use llama_cpp_2::llama_backend::LlamaBackend;
use llama_cpp_2::llama_batch::LlamaBatch;
use llama_cpp_2::model::params::LlamaModelParams;
use llama_cpp_2::model::AddBos;
use llama_cpp_2::model::LlamaModel;
use llama_cpp_2::sampling::LlamaSampler;
use std::num::NonZeroU32;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
use thiserror::Error;

#[derive(Debug, Error)]
pub enum InferenceError {
    #[error("{0}")]
    Msg(String),
}

pub struct InferenceState {
    inner: Mutex<Option<LoadedModel>>,
    abort: AtomicBool,
}

struct LoadedModel {
    #[allow(dead_code)]
    backend: LlamaBackend,
    model: LlamaModel,
    name: String,
    path: String,
}

impl InferenceState {
    pub fn new() -> Self {
        Self {
            inner: Mutex::new(None),
            abort: AtomicBool::new(false),
        }
    }

    pub fn load(&self, path: &Path, display_name: &str) -> Result<(), InferenceError> {
        self.unload()?;
        let backend = LlamaBackend::init().map_err(|e| InferenceError::Msg(e.to_string()))?;
        let model_params = LlamaModelParams::default();
        let model = LlamaModel::load_from_file(&backend, path, &model_params)
            .map_err(|e| InferenceError::Msg(format!("load failed: {e}")))?;

        *self.inner.lock().unwrap() = Some(LoadedModel {
            backend,
            model,
            name: display_name.to_string(),
            path: path.to_string_lossy().to_string(),
        });
        Ok(())
    }

    /// Drop model + backend to release GPU/Metal VRAM.
    pub fn unload(&self) -> Result<(), InferenceError> {
        self.abort.store(true, Ordering::SeqCst);
        let mut guard = self.inner.lock().unwrap();
        *guard = None;
        Ok(())
    }

    pub fn loaded_info(&self) -> Option<(String, String)> {
        let guard = self.inner.lock().unwrap();
        guard.as_ref().map(|m| (m.name.clone(), m.path.clone()))
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

    pub fn stream<F>(
        &self,
        prompt: &str,
        max_tokens: u32,
        temperature: f32,
        mut on_token: F,
    ) -> Result<(), InferenceError>
    where
        F: FnMut(&str) -> bool,
    {
        self.clear_abort();
        let guard = self.inner.lock().unwrap();
        let loaded = guard
            .as_ref()
            .ok_or_else(|| InferenceError::Msg("no model loaded".into()))?;

        // n_batch caps how many tokens decode() can ingest in one call, so the
        // prompt must be fed in chunks of this size — the Playground's system
        // prompt now carries a lot of resident reference (tool defs + the full
        // command catalog + the core Fade syntax guide). n_ctx must hold all
        // that PLUS a multi-step agent conversation and the generated reply.
        // 24576 leaves comfortable room for history after ~12k of static
        // reference. Qwen2.5-7B supports 32k, so this is well within range;
        // the KV cache (~1.3 GB at 24k) is still modest next to the model.
        const N_BATCH: usize = 512;
        let ctx_params = LlamaContextParams::default()
            .with_n_ctx(NonZeroU32::new(24576))
            .with_n_batch(N_BATCH as u32);
        let mut ctx = loaded
            .model
            .new_context(&loaded.backend, ctx_params)
            .map_err(|e| InferenceError::Msg(format!("context: {e}")))?;

        let tokens = loaded
            .model
            .str_to_token(prompt, AddBos::Always)
            .map_err(|e| InferenceError::Msg(format!("tokenize: {e}")))?;

        let n_ctx = ctx.n_ctx() as usize;
        if tokens.len() >= n_ctx {
            return Err(InferenceError::Msg(format!(
                "prompt is {} tokens but the context window is {n_ctx}. \
                 Start a new chat or shorten the conversation.",
                tokens.len()
            )));
        }

        // Feed the prompt in n_batch-sized chunks. Only the very last token of
        // the whole prompt needs logits (that's what we sample the first
        // reply token from).
        let mut batch = LlamaBatch::new(N_BATCH, 1);
        let last_index = tokens.len().saturating_sub(1);
        let mut start = 0usize;
        while start < tokens.len() {
            let end = (start + N_BATCH).min(tokens.len());
            batch.clear();
            for j in start..end {
                batch
                    .add(tokens[j], j as i32, &[0], j == last_index)
                    .map_err(|e| InferenceError::Msg(format!("batch: {e}")))?;
            }
            ctx.decode(&mut batch)
                .map_err(|e| InferenceError::Msg(format!("decode prompt: {e}")))?;
            start = end;
        }

        let samplers: Vec<LlamaSampler> = vec![
            LlamaSampler::temp(temperature),
            LlamaSampler::top_p(0.9, 1),
            LlamaSampler::dist(0),
        ];
        let mut sampler = LlamaSampler::chain_simple(samplers);

        let mut decoder = encoding_rs::UTF_8.new_decoder();
        // Next position to write is just past the prompt. `batch` now holds
        // only the final prompt chunk, so we can't derive this from
        // batch.n_tokens() any more — use the full prompt length.
        let mut n_cur = tokens.len() as i32;
        for _ in 0..max_tokens {
            if self.should_abort() {
                break;
            }
            // NOTE: llama_sampler_sample() already calls llama_sampler_accept
            // internally, so we must NOT accept again. A double-accept advances
            // a grammar sampler's state twice per token → desync → empty stacks →
            // GGML_ASSERT(!stacks.empty()) abort. (Harmless for stateless
            // samplers, which is why this only surfaced once a grammar was added.)
            let token = sampler.sample(&ctx, batch.n_tokens() - 1);

            if loaded.model.is_eog_token(token) {
                break;
            }

            // A control/special token (e.g. Qwen's <|im_end|>) renders as
            // empty with special=false, which the crate surfaces as
            // UnknownTokenType. Some GGUFs don't flag every such token as
            // end-of-generation, so is_eog_token misses it. Treat an
            // undetokenizable token as end-of-turn instead of aborting the
            // whole stream with "detokenize: Unknown Token Type".
            let piece = match loaded.model.token_to_piece(token, &mut decoder, false, None) {
                Ok(p) => p,
                Err(e) => {
                    eprintln!("[inference] stopping at undetokenizable token: {e}");
                    break;
                }
            };

            if !piece.is_empty() && !on_token(&piece) {
                break;
            }

            batch.clear();
            batch
                .add(token, n_cur, &[0], true)
                .map_err(|e| InferenceError::Msg(format!("batch step: {e}")))?;
            n_cur += 1;
            ctx.decode(&mut batch)
                .map_err(|e| InferenceError::Msg(format!("decode step: {e}")))?;
        }
        Ok(())
    }
}
