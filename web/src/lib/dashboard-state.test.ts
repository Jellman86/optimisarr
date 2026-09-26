import assert from 'node:assert/strict'
import test from 'node:test'
import { dashboardState, dashboardStateFor, type DashboardStateInput } from './dashboard-state.ts'

// A queue status with nothing wrong: work may start, nothing is paused, nothing is waiting.
function clear(overrides: Partial<DashboardStateInput> = {}): DashboardStateInput {
  return {
    canStart: true,
    blockedReason: null,
    manuallyPaused: false,
    manualPauseMode: 'inactive',
    waitingReason: null,
    runningJobs: 0,
    queued: 0,
    workerActive: 0,
    workerWaiting: 0,
    workerReason: null,
    ...overrides,
  }
}

test('running work reports Encoding, whatever else is true of the queue', () => {
  const state = dashboardState(clear({ runningJobs: 3, queued: 1418 }))

  assert.equal(state.kind, 'encoding')
  assert.equal(state.running, 3)
  assert.equal(state.detail, null)
})

test('an operator pause outranks every automatic gate', () => {
  // A manual pause and a shut window can be true at once. The operator did the one the
  // operator can undo, so that is the one the page names.
  const state = dashboardState(
    clear({
      canStart: false,
      manuallyPaused: true,
      manualPauseMode: 'suspended',
      waitingReason: '1605 job(s) waiting for the TV optimise window (00:00-05:00)',
      queued: 1605,
    }),
  )

  assert.equal(state.kind, 'paused')
  assert.equal(state.detail, null)
})

test('a blocked queue reports the reason the server gave, verbatim', () => {
  // Taken from a live server: the wording is the server's, and the page must not paraphrase it.
  const state = dashboardState(
    clear({ canStart: false, blockedReason: 'Paused while Riker Plex is active (1 stream).', queued: 14 }),
  )

  assert.equal(state.kind, 'blocked')
  assert.equal(state.detail, 'Paused while Riker Plex is active (1 stream).')
})

test('a queue that could start but has nothing eligible reports what it is waiting for', () => {
  const waiting = '1605 job(s) waiting for the TV optimise window (00:00-05:00)'
  const state = dashboardState(clear({ waitingReason: waiting, queued: 1605 }))

  assert.equal(state.kind, 'waiting')
  assert.equal(state.detail, waiting)
})

test('an empty queue with nothing running is idle, not blocked', () => {
  assert.equal(dashboardState(clear()).kind, 'idle')
})

test('a queue with work but no reason given is unexplained rather than idle', () => {
  // The honest answer when every gate is quiet and nothing runs anyway: say so, rather
  // than draw the same screen an idle server draws.
  const state = dashboardState(clear({ canStart: false, queued: 1418 }))

  assert.equal(state.kind, 'unexplained')
  assert.equal(state.detail, null)
})

test('jobs on workers read as work in progress, not as a queue that will not start', () => {
  // Regression: a worker was encoding while the server itself had nothing running, and the bar
  // said NOT STARTING. The work is happening; the bar must say where.
  const state = dashboardState(clear({ queued: 40, workerActive: 1 }))

  assert.equal(state.kind, 'workers')
  assert.equal(state.severity, 'live')
  assert.equal(state.workerActive, 1)
  assert.equal(state.detail, null)
})

test('worker activity outranks a hold on this server, and the hold stays visible as detail', () => {
  // Playback on the server holds local encodes only; a sidecar keeps going. Naming the hold
  // as the state hid the encode, so the hold becomes the explanation instead.
  const state = dashboardState(
    clear({ canStart: false, blockedReason: 'Paused while Riker Plex is active (1 stream).', queued: 40, workerActive: 1 }),
  )

  assert.equal(state.kind, 'workers')
  assert.equal(state.detail, 'Paused while Riker Plex is active (1 stream).')
  assert.equal(state.localPaused, false)
})

test('worker activity during an operator pause says the pause is still in force', () => {
  const state = dashboardState(
    clear({ canStart: false, manuallyPaused: true, manualPauseMode: 'dispatchOnly', queued: 40, workerActive: 2 }),
  )

  assert.equal(state.kind, 'workers')
  assert.equal(state.localPaused, true)
})

test('a local encode still reads as Encoding when workers are busy too', () => {
  const state = dashboardState(clear({ runningJobs: 1, workerActive: 1, queued: 40 }))

  assert.equal(state.kind, 'encoding')
  assert.equal(state.workerActive, 1)
})

test('jobs held back for a worker report the server’s reason instead of an unexplained stall', () => {
  // PreferWorker jobs are deliberately not started locally while a worker could take them.
  // That is a wait with a reason, not a mystery.
  const state = dashboardState(
    clear({ queued: 12, workerWaiting: 12, workerReason: 'No accepting worker is online.' }),
  )

  assert.equal(state.kind, 'waiting')
  assert.equal(state.detail, 'No accepting worker is online.')
})

test('a running job keeps the state Encoding even while a pause is draining', () => {
  // Pausing does not stop an encode that has already started, so the page must not claim
  // nothing is happening while a file is still being written.
  const state = dashboardState(
    clear({ canStart: false, manuallyPaused: true, manualPauseMode: 'dispatchOnly', runningJobs: 1 }),
  )

  assert.equal(state.kind, 'encoding')
  assert.equal(state.running, 1)
})

test('severity orders the states so the status bar can colour itself', () => {
  assert.equal(dashboardState(clear({ runningJobs: 1 })).severity, 'live')
  assert.equal(dashboardState(clear({ workerActive: 1, queued: 3 })).severity, 'live')
  assert.equal(dashboardState(clear({ canStart: false, manuallyPaused: true, manualPauseMode: 'suspended' })).severity, 'held')
  assert.equal(dashboardState(clear({ canStart: false, blockedReason: 'Plex is streaming' })).severity, 'held')
  assert.equal(dashboardState(clear({ waitingReason: 'window shut', queued: 5 })).severity, 'held')
  assert.equal(dashboardState(clear()).severity, 'quiet')
  assert.equal(dashboardState(clear({ canStart: false, queued: 9 })).severity, 'attention')
})

test('a queue status from the API reads its Workers lane', () => {
  // Shape taken from a live server: nothing running locally, playback holding it, a worker busy.
  const state = dashboardStateFor(
    {
      canStart: false,
      blockedReason: 'Paused while Riker Plex is active (1 stream).',
      manuallyPaused: false,
      manualPauseMode: 'inactive',
      waitingReason: null,
      runningJobs: 0,
      workloadLanes: [
        { lane: 'Video', active: 0, waiting: 0, reason: null },
        { lane: 'Workers', active: 1, waiting: 0, reason: null },
      ],
    },
    40,
  )

  assert.equal(state.kind, 'workers')
  assert.equal(state.workerActive, 1)
})
