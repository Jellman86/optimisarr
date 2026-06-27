import {
  api,
  AuthRequiredError,
  clearAdminToken,
  getAdminToken,
  setAdminToken,
  setAuthRequiredHandler,
} from '../api'

function createAuth() {
  let checked = $state(false)
  let required = $state(false)
  let token = $state<string | null>(getAdminToken())
  let error = $state<string | null>(null)
  let checking = $state(false)

  function forget() {
    clearAdminToken()
    token = null
  }

  function requireLogin() {
    required = true
    checked = true
    forget()
    error = 'Enter the Optimisarr admin token to continue.'
  }

  async function check() {
    checking = true
    try {
      const status = await api.authStatus()
      required = status.required
      token = getAdminToken()
      if (required && token) {
        await api.settings()
      }
      error = null
    } catch (err) {
      error = err instanceof AuthRequiredError
        ? 'Enter the Optimisarr admin token to continue.'
        : err instanceof Error ? err.message : 'Unable to check authentication'
    } finally {
      checked = true
      checking = false
    }
  }

  async function login(nextToken: string) {
    const trimmed = nextToken.trim()
    if (!trimmed) {
      error = 'Enter the admin token.'
      return
    }

    checking = true
    setAdminToken(trimmed)
    token = trimmed

    try {
      await api.settings()
      error = null
    } catch (err) {
      forget()
      error = err instanceof AuthRequiredError
        ? 'That admin token was not accepted.'
        : err instanceof Error ? err.message : 'Unable to sign in'
    } finally {
      checking = false
    }
  }

  setAuthRequiredHandler(requireLogin)

  return {
    check,
    login,
    forget,
    get checked() {
      return checked
    },
    get required() {
      return required
    },
    get token() {
      return token
    },
    get error() {
      return error
    },
    get checking() {
      return checking
    },
    get canUseApp() {
      return checked && (!required || token !== null)
    },
  }
}

export const auth = createAuth()
