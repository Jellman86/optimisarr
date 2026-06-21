// App-wide live activity: how many jobs are running, whether the work is GPU-accelerated
// (drives the sidebar throbbing indicator), and rolling CPU/GPU usage histories for the
// Queue view's graph. Backed by a single SignalR connection started once at app boot, so the
// sidebar stays live regardless of which page is open.
import { api } from '../api'
import { createJobsConnection, type SystemMetrics } from '../realtime'

// ~90 s of history at the broadcaster's 1.5 s cadence.
const HISTORY = 60

function createActivity() {
  let activeJobs = $state(0)
  let hardwareActive = $state(false)
  let metrics = $state<SystemMetrics | null>(null)
  let cpuHistory = $state<number[]>([])
  let gpuHistory = $state<number[]>([])
  let started = false

  async function refreshStatus() {
    try {
      const status = await api.queueStatus()
      activeJobs = status.runningJobs
      hardwareActive = status.hardwareAccelerated
      // Idle: clear the graph so a later run starts from a clean slate.
      if (status.runningJobs === 0) {
        metrics = null
        cpuHistory = []
        gpuHistory = []
      }
    } catch {
      // Status is best-effort; the next event will reconcile.
    }
  }

  function start() {
    if (started) return
    started = true
    const connection = createJobsConnection({
      onChanged: () => void refreshStatus(),
      onProgress: () => {},
      onMetrics: (m) => {
        metrics = m
        cpuHistory = [...cpuHistory, m.cpuPercent].slice(-HISTORY)
        gpuHistory = [...gpuHistory, m.gpuSupported && m.gpuPercent != null ? m.gpuPercent : 0].slice(-HISTORY)
      },
    })
    connection.start().then(refreshStatus).catch(() => {})
  }

  return {
    start,
    get activeJobs() {
      return activeJobs
    },
    get hardwareActive() {
      return hardwareActive
    },
    get metrics() {
      return metrics
    },
    get cpuHistory() {
      return cpuHistory
    },
    get gpuHistory() {
      return gpuHistory
    },
  }
}

export const activity = createActivity()
