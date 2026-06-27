// SignalR client for the jobs hub. The server pushes `jobsChanged` (something in
// the queue changed — re-fetch), `jobProgress` (live transcode telemetry for one
// job) and `systemMetrics` (live CPU/GPU usage while encoding). The consumer owns
// the connection lifecycle; callers should `stop()` the returned connection on teardown.
import { HubConnectionBuilder, type HubConnection } from '@microsoft/signalr'
import { getAdminToken } from './api'

export type JobProgress = {
  jobId: number
  progress: number
  fps: number | null
  speed: number | null
  etaSeconds: number | null
}

export type SystemMetrics = {
  cpuPercent: number
  gpuSupported: boolean
  gpuPercent: number | null
  gpuEngine: string | null
}

export type JobsHandlers = {
  onChanged: () => void
  onProgress: (progress: JobProgress) => void
  onMetrics?: (metrics: SystemMetrics) => void
}

export function createJobsConnection(handlers: JobsHandlers): HubConnection {
  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/jobs', {
      accessTokenFactory: () => getAdminToken() ?? '',
    })
    .withAutomaticReconnect()
    .build()

  connection.on('jobsChanged', handlers.onChanged)
  connection.on('jobProgress', handlers.onProgress)
  if (handlers.onMetrics) connection.on('systemMetrics', handlers.onMetrics)
  // After a dropped connection we may have missed events; reconcile on reconnect.
  connection.onreconnected(handlers.onChanged)

  return connection
}
