/** The fields of a job the sidebar needs in order to choose which one to show. */
export type LeadCandidate = {
  status: string
  remoteStage: string | null
  workerName: string | null
  progress: number
  finalizing?: boolean
}

/**
 * The one job the sidebar shows while work is running.
 *
 * A server may carry several live jobs — one encoding here, two on remote workers, one probing —
 * and a card the size of a playing-now strip has room for one. The encode furthest along on this
 * server is the one a reader is waiting on, so it wins; failing that, whichever encode is furthest
 * along anywhere; failing that, the first job the server listed.
 */
export function pickLeadJob<T extends LeadCandidate>(jobs: readonly T[]): T | null {
  if (jobs.length === 0) return null
  const encoding = jobs.filter((job) => job.status === 'Transcoding' || job.remoteStage === 'Encoding')
  const local = encoding.filter((job) => !job.workerName)
  const pool = local.length > 0 ? local : encoding.length > 0 ? encoding : jobs
  return pool.reduce((best, job) => (job.progress > best.progress ? job : best))
}

export type LeadPhase =
  | 'encoding'
  | 'sending'
  | 'returning'
  | 'starting'
  | 'probing'
  | 'verifying'
  | 'waiting'
  | 'finalizing'

/** What the lead job is doing, where, and which worker it came from or is on. */
export type LeadActivity = {
  phase: LeadPhase
  place: 'server' | 'worker' | 'transfer'
  /** The worker named on the job, which it keeps after handing its encode back. */
  from: string | null
}

/**
 * The sidebar's account of the lead job. A job keeps its worker's name after the worker returns
 * the candidate, so the name alone said the Mac was encoding while the Mac sat idle and this
 * server checked its result. Only a leased job is on a worker; everything else is here.
 */
export function leadActivity(job: LeadCandidate): LeadActivity {
  const from = job.workerName
  if (job.status === 'Leased') {
    switch (job.remoteStage) {
      case 'Encoding':
        return { phase: 'encoding', place: 'worker', from }
      case 'FetchingSource':
        return { phase: 'sending', place: 'transfer', from }
      case 'Delivering':
        return { phase: 'returning', place: 'transfer', from }
      default:
        return { phase: 'starting', place: 'worker', from }
    }
  }
  const phase: LeadPhase = job.finalizing
    ? 'finalizing'
    : job.status === 'Transcoding'
      ? 'encoding'
      : job.status === 'Probing'
        ? 'probing'
        : job.status === 'AwaitingVerification'
          ? 'waiting'
          : 'verifying'
  return { phase, place: 'server', from }
}
