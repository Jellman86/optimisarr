// What the Linux sidecar's own page says about the worker, kept free of Svelte so it is unit tested.

export type SidecarStage = 'FetchingSource' | 'Encoding' | 'Measuring' | 'Delivering'

export type WorkerTone = 'live' | 'ok' | 'warn' | 'bad' | 'idle'

export type WorkerStateView = { label: string; detail: string; tone: WorkerTone }

/** One line for the status strip: what the worker is doing, and what that means for the reader. */
export function workerState(state: string, jobCount: number): WorkerStateView {
  // A check-in can land between a claim and the next status write; running jobs are the truth.
  switch (state === 'Connected' && jobCount > 0 ? 'Working' : state) {
    case 'Working':
      return { label: 'Working', detail: jobCount === 1 ? 'Processing one job for the server.' : `Processing ${jobCount} jobs for the server.`, tone: 'live' }
    case 'Connected':
      return { label: 'Ready', detail: 'Connected and waiting. The server offers work when a library allows workers.', tone: 'ok' }
    case 'Draining':
      return { label: 'Draining', detail: 'Finishing what it holds and taking nothing new. Resume it on the server.', tone: 'warn' }
    case 'Unreachable':
      return { label: 'Unreachable', detail: 'The last check-in did not get through. It keeps retrying on its own.', tone: 'warn' }
    case 'Faulted':
      return { label: 'Needs attention', detail: 'Something on this machine failed. It keeps checking in; see the container log.', tone: 'bad' }
    case 'Stopped':
      return { label: 'Stopped', detail: 'The server refused this worker. Pair it again to bring it back.', tone: 'bad' }
    case 'Unpaired':
      return { label: 'Not paired', detail: 'Pair this worker with your Optimisarr server to start helping.', tone: 'idle' }
    default:
      return { label: 'Starting', detail: 'Proving which encoders and decoders really work on this machine.', tone: 'idle' }
  }
}

/** The four things a worker does with a job, in order; the server does everything either side. */
export const STAGES: { key: SidecarStage; label: string; active: string; doing: string }[] = [
  { key: 'FetchingSource', label: 'Receive', active: 'Receiving the source', doing: 'Receiving a copy of the source. The original stays on the server.' },
  { key: 'Encoding', label: 'Encode', active: 'Encoding here', doing: 'Encoding the copy.' },
  { key: 'Measuring', label: 'Measure', active: 'Measuring quality', doing: 'Comparing the encode with the source so the server can judge its quality.' },
  { key: 'Delivering', label: 'Return', active: 'Returning the candidate', doing: 'Sending the candidate back. The server verifies it before anything is replaced.' },
]

export function stageIndex(stage: string): number {
  const index = STAGES.findIndex((candidate) => candidate.key === stage)
  return index < 0 ? 0 : index
}

export function encodedPercent(encodedSeconds: number | null | undefined, durationSeconds: number | null | undefined): number | null {
  if (encodedSeconds == null || !durationSeconds || durationSeconds <= 0 || !Number.isFinite(encodedSeconds)) return null
  return Math.max(0, Math.min(100, (encodedSeconds / durationSeconds) * 100))
}

/** "1:02:05" / "4:07": a timecode, which is how people read a position in a film. */
export function timecode(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return '—'
  const total = Math.floor(seconds)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const rest = String(total % 60).padStart(2, '0')
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${rest}` : `${minutes}:${rest}`
}

/**
 * Encode speed from successive status polls. The worker reports only how far it has got, so the
 * rate is the change in media seconds over wall-clock seconds, smoothed so one slow poll does not
 * swing the estimate. A position that goes backwards (a new quality attempt) starts again.
 */
export function createSpeedTracker(smoothing = 0.35) {
  const samples = new Map<number, { at: number; position: number; speed: number | null }>()
  return {
    observe(jobId: number, position: number | null | undefined, at: number): number | null {
      if (position == null || !Number.isFinite(position)) return samples.get(jobId)?.speed ?? null
      const last = samples.get(jobId)
      if (!last || position < last.position) {
        samples.set(jobId, { at, position, speed: null })
        return null
      }
      const elapsed = (at - last.at) / 1000
      if (elapsed < 0.5 || position === last.position) return last.speed
      const instant = (position - last.position) / elapsed
      const speed = last.speed == null ? instant : last.speed + smoothing * (instant - last.speed)
      samples.set(jobId, { at, position, speed })
      return speed
    },
    forget(active: Iterable<number>) {
      const keep = new Set(active)
      for (const id of samples.keys()) if (!keep.has(id)) samples.delete(id)
    },
  }
}

export function remainingSeconds(position: number | null | undefined, duration: number | null | undefined, speed: number | null): number | null {
  if (position == null || !duration || speed == null || speed <= 0.01) return null
  return Math.max(0, (duration - position) / speed)
}

/** Groups a code the way the server shows it ("1234 5678") while it is typed. */
export function formatPairingCode(input: string): string {
  const digits = input.replace(/\D/g, '').slice(0, 8)
  return digits.length > 4 ? `${digits.slice(0, 4)} ${digits.slice(4)}` : digits
}

export function isCompletePairingCode(input: string): boolean {
  return input.replace(/\D/g, '').length === 8
}

/** The host a person recognises, not the whole URL. */
export function serverHost(address: string | null | undefined): string | null {
  if (!address) return null
  try { return new URL(address).host } catch { return null }
}

export function sinceLabel(from: string, now: number): string {
  const seconds = Math.max(0, Math.round((now - new Date(from).getTime()) / 1000))
  if (!Number.isFinite(seconds)) return ''
  if (seconds < 60) return `${seconds}s`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${String(seconds % 60).padStart(2, '0')}s`
  return `${Math.floor(seconds / 3600)}h ${String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')}m`
}
