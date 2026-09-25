// The figures the navigation and the status strip carry.
//
// Only the entries that can ask something of you get a number — the queue while it has work,
// quarantine while originals are waiting on a decision. A count on every row is seven figures
// competing for attention and five that never change, which tells a reader nothing about which
// one they were meant to look at.
//
// One poll answers the navigation and the strip across the top of every page: /api/stats and the
// queue status, every fifteen seconds, rather than one poll per page.
import { api, type QueueStatus, type Stats } from '../api'
import { i18n } from '../i18n/i18n.svelte'

const INTERVAL_MS = 15_000

function createCounts() {
  let stats = $state<Stats | null>(null)
  let queue = $state<QueueStatus | null>(null)
  // Bumped whenever this store changes the queue itself, so a page showing queue state can
  // reload at once instead of waiting for its own poll.
  let revision = $state(0)
  let pauseBusy = $state(false)
  let pauseError = $state<string | null>(null)
  let started = false
  let timer: ReturnType<typeof setInterval> | null = null

  async function refresh() {
    // Each half keeps its last known value on a missed poll. Flicking to zero would read as
    // "nothing there", which is a claim the app has no evidence for.
    const [nextStats, nextQueue] = await Promise.allSettled([api.stats(), api.queueStatus()])
    if (nextStats.status === 'fulfilled') stats = nextStats.value
    if (nextQueue.status === 'fulfilled') queue = nextQueue.value
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

  async function togglePause() {
    if (!queue || pauseBusy) return
    pauseBusy = true
    pauseError = null
    try {
      queue = queue.manuallyPaused ? await api.resumeQueue() : await api.pauseQueue()
      revision++
    } catch (err) {
      pauseError = err instanceof Error ? err.message : i18n.m.queue.error_pause
      await refresh()
    } finally {
      pauseBusy = false
    }
  }

  return {
    start,
    stop,
    refresh,
    togglePause,
    get stats() { return stats },
    get queue() { return queue },
    get revision() { return revision },
    get pauseBusy() { return pauseBusy },
    get pauseError() { return pauseError },
    get queued() { return stats?.queued ?? null },
    get quarantine() { return stats?.inQuarantine ?? null },
  }
}

export const counts = createCounts()
