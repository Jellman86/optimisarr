import assert from 'node:assert/strict'
import test from 'node:test'
import { dashboardState, type DashboardStateInput } from './dashboard-state.ts'

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
  assert.equal(dashboardState(clear({ canStart: false, manuallyPaused: true, manualPauseMode: 'suspended' })).severity, 'held')
  assert.equal(dashboardState(clear({ canStart: false, blockedReason: 'Plex is streaming' })).severity, 'held')
  assert.equal(dashboardState(clear({ waitingReason: 'window shut', queued: 5 })).severity, 'held')
  assert.equal(dashboardState(clear()).severity, 'quiet')
  assert.equal(dashboardState(clear({ canStart: false, queued: 9 })).severity, 'attention')
})
