// App-wide live activity: how many jobs are running, whether the work is GPU-accelerated
// (drives the sidebar throbbing indicator), which job the sidebar's encoding card shows and how
// far along it is, and rolling CPU/GPU usage histories for the Queue view's graph. Backed by a
// single SignalR connection started once at app boot, so the sidebar stays live regardless of
// which page is open.
import { api, type Job } from '../api'
import { pickLeadJob } from '../lead-job'
import { createJobsConnection, type JobProgress, type SystemMetrics } from '../realtime'

// ~90 s of history at the broadcaster's 1.5 s cadence.
const HISTORY = 60

function createActivity() {
  let activeJobs = $state(0)
  let hardwareActive = $state(false)
  let metrics = $state<SystemMetrics | null>(null)
  let cpuHistory = $state<number[]>([])
  let gpuHistory = $state<number[]>([])
  let leadJob = $state<Job | null>(null)
  let leadProgress = $state<JobProgress | null>(null)
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

  // The live list is re-read on every change event rather than on a timer: a job starting or
  // finishing is exactly the moment the card's subject changes.
  async function refreshLead() {
    try {
      const next = pickLeadJob(await api.liveJobs())
      if (next?.id !== leadJob?.id) leadProgress = null
      leadJob = next
    } catch {
      // Keep the last known card rather than blanking it on a missed read.
    }
  }

  function start() {
    if (started) return
    started = true
    const connection = createJobsConnection({
      onChanged: () => {
        void refreshStatus()
        void refreshLead()
      },
      // Progress arrives far more often than the list changes; fold it into the card's job so the
      // bar moves between reads instead of jumping when the next change event lands.
      onProgress: (progress) => {
        if (!leadJob || progress.jobId !== leadJob.id) return
        leadProgress = progress
        leadJob = { ...leadJob, progress: progress.progress }
      },
      onMetrics: (m) => {
        metrics = m
        cpuHistory = [...cpuHistory, m.cpuPercent].slice(-HISTORY)
        gpuHistory = [...gpuHistory, m.gpuSupported && m.gpuPercent != null ? m.gpuPercent : 0].slice(-HISTORY)
      },
    })
    connection
      .start()
      .then(() => Promise.all([refreshStatus(), refreshLead()]))
      .catch(() => {
        // Without the hub there are no change events, so read once so the card is not blank
        // forever on a server whose websocket is blocked by a proxy.
        void refreshStatus()
        void refreshLead()
      })
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
    get leadJob() {
      return leadJob
    },
    get leadProgress() {
      return leadProgress
    },
  }
}

export const activity = createActivity()
