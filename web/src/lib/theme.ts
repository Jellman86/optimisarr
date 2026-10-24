// Which scheme the page wears. The colours themselves live in app.css — this file only
// decides *which* scheme is in force, so the decision can be reasoned about and tested
// without a browser.

export type ThemeMode = 'system' | 'light' | 'dark'

export const THEME_STORAGE_KEY = 'optimisarr.theme'

// A stored value the app does not recognise is read as "follow the system": a stale or
// hand-edited preference must never leave the page in a scheme its owner did not ask for.
export function parseThemeMode(stored: string | null | undefined): ThemeMode {
  return stored === 'light' || stored === 'dark' ? stored : 'system'
}

// "System" is whatever the browser reports; a pinned scheme ignores the browser.
export function resolveDark(mode: ThemeMode, prefersDark: boolean): boolean {
  if (mode === 'dark') return true
  if (mode === 'light') return false
  return prefersDark
}

// The theme button walks all three settings. From "follow the system" it pins the scheme
// the system is *not* using, so the first click always changes what is on screen; the next
// click pins the other one; the third hands the page back to the system. Which of the two
// pinned schemes comes first follows the system, so the order never depends on where in
// the cycle the button was pressed.
export function nextThemeMode(mode: ThemeMode, prefersDark: boolean): ThemeMode {
  if (mode === 'system') return prefersDark ? 'light' : 'dark'
  if (mode === 'light') return prefersDark ? 'dark' : 'system'
  return prefersDark ? 'system' : 'light'
}

// The symbol the theme control carries: the scheme the page is in, or the mark for a page
// that follows the system. The control names the setting, so the three settings can be told
// apart at a glance.
export function themeIcon(mode: ThemeMode): 'auto' | 'sun' | 'moon' {
  return mode === 'system' ? 'auto' : mode === 'light' ? 'sun' : 'moon'
}
