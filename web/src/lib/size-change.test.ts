import assert from 'node:assert/strict'
import test from 'node:test'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'

// The size change of a file is read in the Recent results table, the idle in-flight panel, the
// Queue job detail and the Quarantine review list. They share one component so that the sign,
// the zero and the colour cannot drift apart between panels — a saving is a saving wherever it
// is shown, and a file that grew never reads as a saving. This test keeps them on it: a panel
// that writes out a percentage for itself is a panel that can disagree with the others.
const root = join(import.meta.dirname, '..')

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    return statSync(path).isDirectory() ? sources(path) : path.endsWith('.svelte') ? [path] : []
  })
}

// The shared component is where the figure is written, so it is the one place allowed to.
const owner = 'lib/components/SizeChange.svelte'

test('no panel writes out a size-change percentage for itself', () => {
  const offenders: string[] = []
  for (const file of sources(root)) {
    const name = relative(root, file)
    if (name === owner) continue
    if (/\bformatSizeChangePercent\s*\(/.test(readFileSync(file, 'utf8'))) {
      offenders.push(name)
    }
  }
  assert.deepEqual(offenders, [], `panels outside the shared component:\n${offenders.join('\n')}`)
})

test('no panel bolts a sign onto a percentage of its own', () => {
  // A fixed minus in front of a figure is what made a file that grew read as a saving. The
  // sign belongs to the shared formatter, which knows which way the file went.
  const offenders: string[] = []
  for (const file of sources(root)) {
    const name = relative(root, file)
    if (name === owner) continue
    if (/[−-]\s*\{[^}]*\}\s*%/.test(readFileSync(file, 'utf8'))) {
      offenders.push(name)
    }
  }
  assert.deepEqual(offenders, [], `panels with a sign of their own:\n${offenders.join('\n')}`)
})

test('the panels that show a size change all show it through the shared component', () => {
  const panels = [
    'lib/components/RecentResultsPanel.svelte',
    'lib/components/InFlightPanel.svelte',
    'lib/pages/Queue.svelte',
    'lib/pages/Quarantine.svelte',
  ]
  const missing = panels.filter((name) => !readFileSync(join(root, name), 'utf8').includes('<SizeChange'))
  assert.deepEqual(missing, [], `panels not using the shared component:\n${missing.join('\n')}`)
})
