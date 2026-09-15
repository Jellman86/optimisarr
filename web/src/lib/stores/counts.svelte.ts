// The two figures the navigation carries.
//
// Only the entries that can ask something of you get a number — the queue while it has work,
// quarantine while originals are waiting on a decision. A count on every row is seven figures
// competing for attention and five that never change, which tells a reader nothing about which
// one they were meant to look at.
//
// One poll of /api/stats answers both, and it is a call the dashboard already makes, so the
// navigation costs one request every fifteen seconds rather than one per page.
import { api } from '../api'

const INTERVAL_MS = 15_000

function createCounts() {
  let queued = $state<number | null>(null)
  let quarantine = $state<number | null>(null)
  let started = false
  let timer: ReturnType<typeof setInterval> | null = null

  async function refresh() {
    try {
      const stats = await api.stats()
      queued = stats.queued
      quarantine = stats.inQuarantine
    } catch {
      // A missed poll is not worth reporting in the navigation. The counts keep their last known
      // values rather than flicking to zero, which would read as "nothing there".
    }
  }

  function start() {
    if (started) return
    started = true
    void refresh()
    timer = setInterval(() => void refresh(), INTERVAL_MS)
  }

  /** Stops the poll. Every other timer in this app is cleaned up; this one can be too. */
  function stop() {
    if (timer !== null) clearInterval(timer)
    timer = null
    started = false
  }

  return {
    start,
    stop,
    refresh,
    get queued() { return queued },
    get quarantine() { return quarantine },
  }
}

export const counts = createCounts()
