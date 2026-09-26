// What the dashboard's status bar says, derived from the queue's own answer.
//
// The page this replaced drew the same screen whether the queue was racing or wedged: every
// figure on it was a count, and none of them was a state. "0 running, 1,418 queued" was what
// you got when the optimise window was shut, when media was streaming, when the operator had
// paused, and when nothing was eligible — four situations, one screen.
//
// Kept pure and separate from the component so the precedence between the gates is testable.
// It matters: a manual pause and a shut window are routinely true at the same time, and only
// one of them is worth naming.

export type DashboardStateInput = {
  canStart: boolean
  blockedReason: string | null
  manuallyPaused: boolean
  manualPauseMode: 'inactive' | 'suspended' | 'partial' | 'dispatchOnly'
  /** Set when dispatch is ready but every queued job's optimise window is shut. */
  waitingReason: string | null
  runningJobs: number
  queued: number
  /** Jobs a remote worker holds right now. They run while this server itself is held. */
  workerActive: number
  /** Queued jobs held back for a worker rather than started here. */
  workerWaiting: number
  /** The server's reason those jobs are still waiting for a worker. */
  workerReason: string | null
}

export type DashboardStateKind =
  | 'encoding'
  | 'workers'
  | 'paused'
  | 'blocked'
  | 'waiting'
  | 'idle'
  | 'unexplained'

/** Drives the status bar's colour. Semantic, and never the only cue — each state names itself too. */
export type DashboardSeverity = 'live' | 'held' | 'quiet' | 'attention'

export type DashboardState = {
  kind: DashboardStateKind
  severity: DashboardSeverity
  /** The server's own words for why, when it gave any. Never invented here. */
  detail: string | null
  running: number
  queued: number
  workerActive: number
  /** Set while workers are encoding and the operator's pause still holds this server. */
  localPaused: boolean
}

const SEVERITY: Record<DashboardStateKind, DashboardSeverity> = {
  encoding: 'live',
  workers: 'live',
  paused: 'held',
  blocked: 'held',
  waiting: 'held',
  idle: 'quiet',
  unexplained: 'attention',
}

function classify(input: DashboardStateInput): { kind: DashboardStateKind; detail: string | null } {
  // Work in progress outranks everything. Pausing does not stop an encode that has already
  // started, so a page claiming nothing is happening while a file is still being written
  // would be lying about the one thing it exists to report.
  if (input.runningJobs > 0) return { kind: 'encoding', detail: null }

  // The same holds for a worker. Playback or a pause holds this server's own encodes, not a
  // sidecar's, so naming the hold as the state hid an encode that was plainly happening. The
  // hold is still true, so it stays as the explanation.
  if (input.workerActive > 0) return { kind: 'workers', detail: input.blockedReason ?? input.waitingReason }

  // The operator's own pause comes next, because it is the gate they can undo. Its reason is
  // that they asked, which the label already says, so there is nothing to add.
  if (input.manuallyPaused) return { kind: 'paused', detail: null }

  if (input.blockedReason) return { kind: 'blocked', detail: input.blockedReason }

  if (input.waitingReason) return { kind: 'waiting', detail: input.waitingReason }

  // Nothing queued and nothing running is genuinely finished, not stuck.
  if (input.queued === 0) return { kind: 'idle', detail: null }

  // Jobs kept for a worker are not started here on purpose. That is a wait with a reason.
  if (input.workerWaiting > 0 && input.workerReason) return { kind: 'waiting', detail: input.workerReason }

  // Work is queued, every gate is quiet, and still nothing runs. Say that plainly rather than
  // draw the screen an idle server draws — this is the state that used to be invisible.
  return { kind: 'unexplained', detail: null }
}

export function dashboardState(input: DashboardStateInput): DashboardState {
  const { kind, detail } = classify(input)
  return {
    kind,
    severity: SEVERITY[kind],
    detail,
    running: input.runningJobs,
    queued: input.queued,
    workerActive: input.workerActive,
    localPaused: kind === 'workers' && input.manuallyPaused,
  }
}

type QueueStatusLike = Omit<DashboardStateInput, 'queued' | 'workerActive' | 'workerWaiting' | 'workerReason'> & {
  workloadLanes?: { lane: string; active: number; waiting: number; reason: string | null }[]
}

/** The state for a queue status as the API returns it, so every page reads the Workers lane the same way. */
export function dashboardStateFor(queue: QueueStatusLike, queued: number): DashboardState {
  const workers = queue.workloadLanes?.find((lane) => lane.lane === 'Workers')
  return dashboardState({
    canStart: queue.canStart,
    blockedReason: queue.blockedReason,
    manuallyPaused: queue.manuallyPaused,
    manualPauseMode: queue.manualPauseMode,
    waitingReason: queue.waitingReason,
    runningJobs: queue.runningJobs,
    queued,
    workerActive: workers?.active ?? 0,
    workerWaiting: workers?.waiting ?? 0,
    workerReason: workers?.reason ?? null,
  })
}
