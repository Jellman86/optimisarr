import assert from 'node:assert/strict'
import test from 'node:test'
import { firstUnavailableLibrary, libraryPathsReady } from './setup-library-readiness.ts'

test('libraryPathsReady requires at least one library and confirmed access for every path', () => {
  assert.equal(libraryPathsReady([], {}), false)
  assert.equal(libraryPathsReady([{ id: 1 }, { id: 2 }], { 1: { ok: true }, 2: { ok: true } }), true)
  assert.equal(libraryPathsReady([{ id: 1 }, { id: 2 }], { 1: { ok: true }, 2: null }), false)
})

test('firstUnavailableLibrary identifies the first path that needs attention', () => {
  const libraries = [{ id: 1, name: 'Films' }, { id: 2, name: 'Television' }]

  assert.equal(firstUnavailableLibrary(libraries, { 1: { ok: true }, 2: { ok: false } }), libraries[1])
  assert.equal(firstUnavailableLibrary(libraries, { 1: { ok: true }, 2: { ok: true } }), undefined)
})
