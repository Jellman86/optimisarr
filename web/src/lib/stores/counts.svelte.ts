// The figures beside each sidebar entry.
//
// The nav used to be seven words with no numbers on them, so "is there anything in quarantine?"
// meant opening quarantine to find out. One poll of /api/stats answers all of it, and it is the
// same call the dashboard already makes, so the sidebar costs one request every fifteen seconds
// rather than one per page.
import { api } from '../api'

const INTERVAL_MS = 15_000

function createCounts() {
  let libraries = $state<number | null>(null)
  let files = $state<number | null>(null)
  let queued = $state<number | null>(null)
  let running = $state<number | null>(null)
  let quarantine = $state<number | null>(null)
  let started = false

  async function refresh() {
    try {
      const stats = await api.stats()
      libraries = stats.libraries
      files = stats.discoveredFiles
      queued = stats.queued
      running = stats.running
      quarantine = stats.inQuarantine
    } catch {
      // A missed poll is not worth reporting in the navigation. The counts keep their last
      // known values rather than flicking to zero, which would read as "nothing there".
    }
  }

  function start() {
    if (started) return
    started = true
    void refresh()
    setInterval(() => void refresh(), INTERVAL_MS)
  }

  return {
    start,
    refresh,
    get libraries() {
      return libraries
    },
    get files() {
      return files
    },
    get queued() {
      return queued
    },
    get running() {
      return running
    },
    get quarantine() {
      return quarantine
    },
  }
}

export const counts = createCounts()
