import { nextThemeMode, parseThemeMode, resolveDark, themeIcon, THEME_STORAGE_KEY, type ThemeMode } from '../theme'

// Small UI state holders: dark theme, sidebar collapse, and hash-based routing.

function createTheme() {
  const media = window.matchMedia('(prefers-color-scheme: dark)')
  let mode = $state<ThemeMode>(parseThemeMode(localStorage.getItem(THEME_STORAGE_KEY)))
  let dark = $state(resolveDark(mode, media.matches))

  // The browser chrome (iOS Safari's toolbar, Android's status bar) takes the page ground so
  // the app never sits in a differently coloured frame. Read from the stylesheet rather than
  // repeated here, so the ground has exactly one definition.
  function apply() {
    const root = document.documentElement
    root.classList.toggle('dark', dark)
    const ground = getComputedStyle(root).getPropertyValue('--ground').trim()
    if (ground) document.querySelector('meta[name="theme-color"]')?.setAttribute('content', ground)
  }

  // A page that follows the system changes with it, with no reload: the browser says the
  // system has switched, and the tokens, the favicon and the mark all follow the same state.
  media.addEventListener('change', (event) => {
    if (mode !== 'system') return
    dark = event.matches
    apply()
  })

  function setMode(next: ThemeMode) {
    mode = next
    dark = resolveDark(next, media.matches)
    try {
      localStorage.setItem(THEME_STORAGE_KEY, next)
    } catch {
      // A browser that refuses to store the choice still shows it; it just will not survive
      // a reload.
    }
    apply()
  }

  apply()

  return {
    // What the owner asked for: follow the system, or pin one scheme.
    get mode() {
      return mode
    },
    // What is on screen now, which is the system's answer while the page follows the system.
    get isDark() {
      return dark
    },
    // The symbol the theme control carries.
    get icon() {
      return themeIcon(mode)
    },
    toggle() {
      setMode(nextThemeMode(mode, media.matches))
    },
  }
}

function createLayout() {
  // `collapsed` is the desktop rail toggle (persisted). `mobileOpen` is the off-canvas
  // drawer on small screens — always starts closed and is never persisted.
  let collapsed = $state(localStorage.getItem('optimisarr.sidebar') === 'collapsed')
  let mobileOpen = $state(false)
  return {
    get collapsed() {
      return collapsed
    },
    toggle() {
      collapsed = !collapsed
      localStorage.setItem('optimisarr.sidebar', collapsed ? 'collapsed' : 'expanded')
    },
    get mobileOpen() {
      return mobileOpen
    },
    toggleMobile() {
      mobileOpen = !mobileOpen
    },
    closeMobile() {
      mobileOpen = false
    },
  }
}

function createRouter() {
  const current = () => (window.location.hash.replace(/^#/, '') || '/')
  let route = $state(current())

  // An optional guard a page registers while it has unsaved work. It returns true when it is
  // safe to leave (nothing unsaved, or the user confirmed discarding). All in-app navigation
  // funnels through the hash, so this one chokepoint covers sidebar links and back/forward alike.
  let leaveGuard: (() => boolean) | null = null
  let reverting = false

  window.addEventListener('hashchange', () => {
    if (reverting) {
      reverting = false
      return
    }
    const next = current()
    if (next === route) return
    if (leaveGuard && !leaveGuard()) {
      // Blocked: restore the previous hash without advancing the active route.
      reverting = true
      window.location.hash = route
      return
    }
    route = next
  })

  return {
    get path() {
      return route
    },
    go(path: string) {
      window.location.hash = path
    },
    // Replace the current history entry while still emitting hashchange so route state and keyed
    // page components stay in sync (used when a just-created resource gains its canonical URL).
    replace(path: string) {
      window.location.replace(`#${path}`)
    },
    // Register a guard; returns a disposer the page calls on unmount. Only one page is mounted
    // at a time, so a single slot is enough.
    guardLeave(fn: () => boolean) {
      leaveGuard = fn
      return () => {
        if (leaveGuard === fn) leaveGuard = null
      }
    },
  }
}

export const theme = createTheme()
export const layout = createLayout()
export const router = createRouter()
