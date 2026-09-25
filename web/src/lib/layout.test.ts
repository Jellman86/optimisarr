import assert from 'node:assert/strict'
import test from 'node:test'
import { pageWidth } from './layout.ts'

test('pages that are lists, grids or dashboards use the whole screen', () => {
  for (const path of ['/', '/queue', '/inventory', '/candidates', '/libraries', '/quarantine', '/quarantine/12', '/schedule', '/libraries/4/quality-check']) {
    assert.equal(pageWidth(path), 'fluid', path)
  }
})

test('forms keep a readable measure however wide the screen is', () => {
  for (const path of ['/settings', '/settings/encoding', '/tools', '/libraries/new', '/libraries/4/configure', '/libraries/4/configure/encode']) {
    assert.equal(pageWidth(path), 'reading', path)
  }
})
