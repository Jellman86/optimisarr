// Small UI state holders: dark theme, sidebar collapse, and hash-based routing.

function createTheme() {
  const stored = localStorage.getItem('optimisarr.theme')
  const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches
  let dark = $state(stored ? stored === 'dark' : prefersDark)

  function apply() {
    document.documentElement.classList.toggle('dark', dark)
  }
  apply()

  return {
    get isDark() {
      return dark
    },
    toggle() {
      dark = !dark
      localStorage.setItem('optimisarr.theme', dark ? 'dark' : 'light')
      apply()
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
