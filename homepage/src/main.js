import { mount } from 'svelte'
import './app.css'
import './themes.css'
import { applyTheme, getTheme } from './theme.js'
import { normalizeBeta } from './beta.js'
import App from './App.svelte'

// Apply the persisted theme before mount so there's no dark→theme flash.
applyTheme(getTheme())
// Hoist a hash-borne `?beta` into the query string before routing reads the URL.
normalizeBeta()

const app = mount(App, {
  target: document.getElementById('app'),
})

export default app
