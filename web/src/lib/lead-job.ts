/** The fields of a job the sidebar needs in order to choose which one to show. */
export type LeadCandidate = {
  status: string
  remoteStage: string | null
  workerName: string | null
  progress: number
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
