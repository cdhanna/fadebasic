// Thin re-export of the shared theme manager in @fadebasic/components, so the
// docs picker, the homepage editor picker, and the boot-time apply all use one
// implementation + one localStorage key.

import { applyFadeTheme, getFadeTheme, FADE_THEME_PRESETS } from '@fadebasic/components';

export const THEMES = FADE_THEME_PRESETS;
export const getTheme = getFadeTheme;
export const applyTheme = applyFadeTheme;
