import { strict as assert } from 'node:assert'
import { test } from 'node:test'
import { parseBrandStyle, brandAsset } from './brand-style.ts'

test('stellar is the default while a saved precession choice is retained', () => {
  for (const value of [null, '', 'unknown', 'stellar']) assert.equal(parseBrandStyle(value), 'stellar')
  assert.equal(parseBrandStyle('precession'), 'precession')
})

test('both styles have distinct theme and activity assets without replacing the original', () => {
  assert.equal(brandAsset('precession', true, false, true), '/brand/favicon-dark-steady.png')
  assert.equal(brandAsset('stellar', false, true, true), '/brand/stellar/favicon-light-excited.png')
  assert.equal(brandAsset('stellar', true, false), '/brand/stellar/dark-steady.webp')
})
