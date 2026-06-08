// SignalR client for the jobs hub. The server pushes `jobsChanged` (something in
// the queue changed — re-fetch) and `jobProgress` (live transcode telemetry for
// one job). The consumer owns the connection lifecycle; callers should `stop()`
// the returned connection on teardown.
import { HubConnectionBuilder, type HubConnection } from '@microsoft/signalr'

export type JobProgress = {
  jobId: number
  progress: number
  fps: number | null
  speed: number | null
  etaSeconds: number | null
}

export type JobsHandlers = {
  onChanged: () => void
  onProgress: (progress: JobProgress) => void
}

export function createJobsConnection(handlers: JobsHandlers): HubConnection {
  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/jobs')
    .withAutomaticReconnect()
    .build()

  connection.on('jobsChanged', handlers.onChanged)
  connection.on('jobProgress', handlers.onProgress)
  // After a dropped connection we may have missed events; reconcile on reconnect.
  connection.onreconnected(handlers.onChanged)

  return connection
}
