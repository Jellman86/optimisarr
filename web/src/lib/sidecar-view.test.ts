import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  createSpeedTracker, encodedPercent, formatPairingCode, isCompletePairingCode, remainingSeconds,
  serverHost, sinceLabel, stageIndex, timecode, workerState,
} from './sidecar-view.ts'

test('each worker state says what it means and how loud to be', () => {
  assert.equal(workerState('Working', 2).tone, 'live')
  assert.match(workerState('Working', 2).detail, /2 jobs/)
  assert.equal(workerState('Connected', 0).label, 'Ready')
  assert.equal(workerState('Connected', 1).label, 'Working')
  assert.equal(workerState('Draining', 0).tone, 'warn')
  assert.equal(workerState('Stopped', 0).tone, 'bad')
  assert.equal(workerState('Unpaired', 0).label, 'Not paired')
  assert.equal(workerState('SomethingNew', 0).label, 'Starting')
})

test('stages are ordered and an unknown stage reads as the first', () => {
  assert.equal(stageIndex('FetchingSource'), 0)
  assert.equal(stageIndex('Delivering'), 3)
  assert.equal(stageIndex('Claimed'), 0)
})

test('encode progress needs a known duration and stays within bounds', () => {
  assert.equal(encodedPercent(30, 120), 25)
  assert.equal(encodedPercent(500, 120), 100)
  assert.equal(encodedPercent(30, null), null)
  assert.equal(encodedPercent(null, 120), null)
})

test('timecodes read like a player', () => {
  assert.equal(timecode(247), '4:07')
  assert.equal(timecode(3725), '1:02:05')
  assert.equal(timecode(null), '—')
})

test('speed is media seconds per wall second, smoothed, and restarts when the position falls', () => {
  const speed = createSpeedTracker(0.5)
  assert.equal(speed.observe(1, 0, 0), null)
  assert.equal(speed.observe(1, 4, 2000), 2)
  assert.equal(speed.observe(1, 10, 4000), 2.5)
  assert.equal(speed.observe(1, 10, 4100), 2.5)
  assert.equal(speed.observe(1, 1, 6000), null)
  speed.forget([])
  assert.equal(speed.observe(1, 5, 8000), null)
})

test('time left follows from speed and never goes negative', () => {
  assert.equal(remainingSeconds(60, 120, 2), 30)
  assert.equal(remainingSeconds(130, 120, 2), 0)
  assert.equal(remainingSeconds(60, 120, null), null)
})

test('pairing codes are grouped as the server shows them and must be eight digits', () => {
  assert.equal(formatPairingCode('12345678'), '1234 5678')
  assert.equal(formatPairingCode('12a3-4 5678 9'), '1234 5678')
  assert.equal(formatPairingCode('123'), '123')
  assert.equal(isCompletePairingCode('1234 5678'), true)
  assert.equal(isCompletePairingCode('1234 567'), false)
})

test('server and elapsed labels are short', () => {
  assert.equal(serverHost('https://optimisarr.example/base/'), 'optimisarr.example')
  assert.equal(serverHost('not a url'), null)
  assert.equal(sinceLabel(new Date(0).toISOString(), 125_000), '2m 05s')
  assert.equal(sinceLabel(new Date(0).toISOString(), 3_720_000), '1h 02m')
})
