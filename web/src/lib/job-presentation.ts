type JobState = { status: string; remoteStage?: string | null; progress?: number }

export function isWorkingJob(job: JobState): boolean {
  return ['Probing', 'Transcoding', 'Verifying', 'Leased', 'AwaitingVerification'].includes(job.status)
}

export function isJobSuspended(job: JobState, queue: { runningEncodesSuspended: boolean; manualPauseMode: string } | null): boolean {
  // A partial pause has no per-job outcome in the API. Its aggregate reason is shown instead.
  return job.status === 'Transcoding' && queue?.runningEncodesSuspended === true && queue.manualPauseMode === 'suspended'
}

export function jobPercent(job: JobState): number | null {
  const value = job.progress
  if (value == null || !Number.isFinite(value)) return null
  const encoding = job.status === 'Transcoding' || (job.status === 'Leased' && job.remoteStage === 'Encoding')
  if (encoding) return Math.floor(Math.max(0, Math.min(value, .999)) * 100)
  if (['Probing', 'Verifying'].includes(job.status) && value > 0) return Math.round(Math.min(value, 1) * 100)
  return null
}

export function jobStep(job: JobState): number | null {
  switch (job.status) {
    case 'Probing': return 0
    case 'Transcoding': return 1
    case 'Leased': return job.remoteStage === 'Encoding' ? 1 : job.remoteStage === 'Delivering' ? 2 : 0
    case 'AwaitingVerification':
    case 'Verifying': return 2
    case 'ReadyToReplace': return 3
    case 'Completed': return 4
    default: return null
  }
}
