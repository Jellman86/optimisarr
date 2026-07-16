import { api, type SetupState } from '../api'

function createSetup() {
  let state = $state<SetupState | null>(null)
  let checked = $state(false)
  let loading = $state(false)
  let error = $state<string | null>(null)

  async function run(action: () => Promise<SetupState>) {
    loading = true
    try {
      state = await action()
      error = null
      return state
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to update setup progress.'
      throw err
    } finally {
      checked = true
      loading = false
    }
  }

  return {
    load: () => run(() => api.setup()),
    advance: (completedStep: number) => run(() => api.advanceSetup(completedStep)),
    complete: () => run(() => api.completeSetup()),
    restart: () => run(() => api.restartSetup()),
    clearError: () => (error = null),
    get state() { return state },
    get checked() { return checked },
    get loading() { return loading },
    get error() { return error },
    get required() { return checked && state !== null && !state.completed },
  }
}

export const setup = createSetup()
