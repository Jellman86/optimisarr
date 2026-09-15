import assert from 'node:assert/strict'
import test from 'node:test'
import { dashboardState, remainingWork, type DashboardStateInput } from './dashboard-state.ts'

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
      waitingReason: '1605 job(s) waiting for the TV optimise window (00:00–05:00)',
      queued: 1605,
    }),
  )

  assert.equal(state.kind, 'paused')
  assert.equal(state.detail, null)
})

test('a blocked queue reports the reason the server gave, verbatim', () => {
  const state = dashboardState(
    clear({ canStart: false, blockedReason: 'Plex has 2 active streams', queued: 1418 }),
  )

  assert.equal(state.kind, 'blocked')
  assert.equal(state.detail, 'Plex has 2 active streams')
})

test('a queue that could start but has nothing eligible reports what it is waiting for', () => {
  const waiting = '1605 job(s) waiting for the TV optimise window (00:00–05:00)'
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

test('a running job keeps the state Encoding even while a pause is draining', () => {
  // Pausing does not stop an encode that has already started; the page must not claim
  // nothing is happening while a file is still being written.
  const state = dashboardState(
    clear({ canStart: false, manuallyPaused: true, manualPauseMode: 'dispatchOnly', runningJobs: 1 }),
  )

  assert.equal(state.kind, 'encoding')
  assert.equal(state.running, 1)
})

test('severity orders the states so the status bar can colour itself', () => {
  assert.equal(dashboardState(clear({ runningJobs: 1 })).severity, 'live')
  assert.equal(dashboardState(clear({ canStart: false, manuallyPaused: true, manualPauseMode: 'suspended' })).severity, 'held')
  assert.equal(dashboardState(clear({ canStart: false, blockedReason: 'Plex is streaming' })).severity, 'held')
  assert.equal(dashboardState(clear({ waitingReason: 'window shut', queued: 5 })).severity, 'held')
  assert.equal(dashboardState(clear()).severity, 'quiet')
  assert.equal(dashboardState(clear({ canStart: false, queued: 9 })).severity, 'attention')
})

test('remaining work is a count of files, never a projected size', () => {
  // A projection of bytes-still-to-save from the average saving per optimised file is
  // unsound: the files left are not a sample of the ones already done, and the arithmetic
  // cheerfully produced a figure larger than the whole library. A count is true.
  const remaining = remainingWork({ discoveredFiles: 11_240, filesOptimised: 2_184, queued: 1_418 })

  assert.equal(remaining.files, 9_056)
  assert.equal(remaining.queued, 1_418)
})

test('remaining work never goes negative when the inventory shrinks under a finished tally', () => {
  // Files can be deleted from disk after they were optimised, so discovered can fall below
  // the lifetime optimised count. That is not "minus six files left to do".
  assert.equal(remainingWork({ discoveredFiles: 10, filesOptimised: 16, queued: 0 }).files, 0)
})

test('a library with nothing discovered yet has nothing remaining', () => {
  assert.equal(remainingWork({ discoveredFiles: 0, filesOptimised: 0, queued: 0 }).files, 0)
})
