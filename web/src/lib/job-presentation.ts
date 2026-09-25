type JobState = { status: string; remoteStage?: string | null; progress?: number; sidecarVerification?: boolean; finalizing?: boolean }

export function localWorkloadCapacity(queue: { maxConcurrentJobs: number; workloadLanes?: { lane: string; capacity: number }[] }): number {
  if (!queue.workloadLanes?.length) return queue.maxConcurrentJobs
  return queue.workloadLanes
    .filter(lane => ['Video', 'NonVideo', 'Evidence'].includes(lane.lane))
    .reduce((total, lane) => total + lane.capacity, 0)
}

export function jobLocation(job: JobState): 'container' | 'worker' | 'transfer' {
  if (job.status !== 'Leased') return 'container'
  return job.remoteStage === 'FetchingSource' || job.remoteStage === 'Delivering' ? 'transfer' : 'worker'
}

export function verificationPhase(job: JobState): 'waiting' | 'evidence' | 'media' | null {
  if (job.status === 'AwaitingVerification') return 'waiting'
  if (job.status !== 'Verifying') return null
  return job.sidecarVerification ? 'evidence' : 'media'
}

export function isWorkingJob(job: JobState): boolean {
  return job.finalizing === true || ['Probing', 'Transcoding', 'Verifying', 'Leased', 'AwaitingVerification'].includes(job.status)
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
