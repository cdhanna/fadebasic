import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'
import { fileURLToPath } from 'node:url'

// Consume the @fadebasic/* component library from source (the packages aren't
// published yet). When they are, drop these aliases and add them as npm deps.
const fade = (p) => fileURLToPath(new URL(`../../Fade.Playground/packages/${p}/src/index.ts`, import.meta.url))

// https://vite.dev/config/
export default defineConfig({
  resolve: {
    alias: {
      '@fadebasic/components': fade('components'),
      '@fadebasic/editor': fade('editor'),
      '@fadebasic/runtime': fade('runtime'),
    },
  },
  server: {
    // monaco-editor is resolved from the sibling Fade.Playground checkout
    // (via the aliases above); allow the dev server to serve its assets
    // (e.g. codicon.ttf) from outside the homepage root. Dev-only — a prod
    // build bundles them.
    fs: { allow: ['.', fileURLToPath(new URL('../../Fade.Playground', import.meta.url))] },
  },
  plugins: [svelte()],
})
