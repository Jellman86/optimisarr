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
  window.addEventListener('hashchange', () => {
    route = current()
  })
  return {
    get path() {
      return route
    },
    go(path: string) {
      window.location.hash = path
    },
  }
}

export const theme = createTheme()
export const layout = createLayout()
export const router = createRouter()
