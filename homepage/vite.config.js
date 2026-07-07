import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'
import { fileURLToPath } from 'node:url'

// Consume the @fadebasic/* component library from source (the packages aren't
// published yet). When they are, drop these aliases and add them as npm deps.
const fade = (p) => fileURLToPath(new URL(`../../Fade.Playground/packages/${p}/src/index.ts`, import.meta.url))

// https://vite.dev/config/
export default defineConfig({
  base: 'https://fadebasic.com',
  resolve: {
    alias: {
      '@fadebasic/components': fade('components'),
      '@fadebasic/editor': fade('editor'),
      '@fadebasic/runtime': fade('runtime'),
    },
  },
  plugins: [svelte()],
})
