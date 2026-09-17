type QueueActivity = { runningJobs: number; suspendedEncodeCount: number }
type JobActivity = { status: string; workerName: string | null }

/** A dispatch pause can leave encodes in Transcoding while their processes are suspended. */
export function hasBrandActivity(queue: QueueActivity, jobs: readonly JobActivity[]): boolean {
  if (queue.runningJobs > queue.suspendedEncodeCount) return true
  return jobs.some(job =>
    job.status === 'Probing' || job.status === 'Verifying' ||
    job.status === 'Leased' ||
    (job.status === 'Transcoding' && (Boolean(job.workerName) || queue.suspendedEncodeCount === 0)),
  )
}
