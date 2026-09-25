import { strict as assert } from 'node:assert'
import { statSync } from 'node:fs'
import { test } from 'node:test'

// These are also the complete download path for reduced-motion sessions.
test('theme and activity stills stay within the immediate icon budget', () => {
  const bytes = (name: string) => statSync(new URL(`../../public/brand/${name}`, import.meta.url)).size
  for (const style of ['', 'stellar/']) for (const theme of ['dark', 'light']) for (const state of ['steady', 'excited']) {
    assert.ok(bytes(`${style}${theme}-${state}.webp`) < 30_000)
    assert.ok(bytes(`${style}favicon-${theme}-${state}.png`) < 15_000)
  }
})

