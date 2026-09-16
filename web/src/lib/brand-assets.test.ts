import { strict as assert } from 'node:assert'
import { statSync } from 'node:fs'
import { test } from 'node:test'

// Keep a decorative indicator inside its download budget when the source scene is regenerated.
test('the icon assets stay within the lazy animation and immediate still budgets', () => {
  const bytes = (name: string) => statSync(new URL(`../../public/brand/${name}`, import.meta.url)).size
  assert.ok(bytes('active.webp') < 1_500_000, 'expanded animation must stay below 1.5 MB')
  assert.ok(bytes('active-small.webp') < 300_000, 'small animation must stay below 300 kB')
  assert.ok(bytes('steady.webp') < 30_000, 'idle mark must stay below 30 kB')
  assert.ok(bytes('excited.webp') < 40_000, 'reduced-motion mark must stay below 40 kB')
})
