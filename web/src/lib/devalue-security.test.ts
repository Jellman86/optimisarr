import assert from 'node:assert/strict'
import { test } from 'node:test'
import { parse, stringify, unflatten } from 'devalue'

test('devalue rejects array keys instead of coercing them into object properties', () => {
  const malformed = '[{"data":1},["null",["__proto__"],2],{"isAdmin":3},true]'

  assert.throws(() => parse(malformed), /non-string key/)
  assert.throws(() => unflatten(JSON.parse(malformed)), /non-string key/)
})

test('devalue preserves legitimate keys on a null-prototype object', () => {
  const original = Object.assign(Object.create(null), {
    '': 'empty', '0': 'numeric', constructor: 'constructor', toString: 'toString',
  })
  const encoded = stringify(original)

  for (const restored of [parse(encoded), unflatten(JSON.parse(encoded))]) {
    assert.equal(Object.getPrototypeOf(restored), null)
    assert.deepEqual(restored, original)
  }
})
