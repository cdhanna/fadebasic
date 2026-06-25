//! Compile-time adjacent checks for bundle icons — same failure Tauri hits at startup.

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    fn icon_path() -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("icons/icon.png")
    }

    #[test]
    fn bundle_icon_png_decodes_to_full_rgba_buffer() {
        let path = icon_path();
        assert!(path.is_file(), "missing {}", path.display());

        let img = image::open(&path).expect("icon.png must decode");
        let (w, h) = (img.width(), img.height());
        assert!(w >= 32 && h >= 32, "icon too small: {w}x{h}");
        assert_eq!(w, h, "icon must be square");

        let rgba = img.to_rgba8();
        let expected = (w as usize) * (h as usize) * 4;
        assert_eq!(
            rgba.as_raw().len(),
            expected,
            "truncated/corrupt PNG pixel data"
        );
    }
}
