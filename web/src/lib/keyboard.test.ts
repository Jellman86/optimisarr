import { test } from 'node:test'
import assert from 'node:assert/strict'
import { en } from './i18n/en.ts'
import {
  capturesKeys,
  createGoChord,
  filterJumpItems,
  GO_SHORTCUTS,
  hasOpenModalDialog,
  isApplePlatform,
  isEditableTarget,
  nextRowIndex,
  quickJumpItems,
  wrapIndex,
  type KeyTargetNode,
  type ShortcutKeyEvent,
} from './keyboard.ts'

function node(tagName: string, attributes: Record<string, string> = {}, parentElement: KeyTargetNode | null = null, isContentEditable = false): KeyTargetNode {
  return { tagName: tagName.toUpperCase(), isContentEditable, getAttribute: (name) => attributes[name] ?? null, parentElement }
}

function key(value: string, extra: Partial<ShortcutKeyEvent> = {}): ShortcutKeyEvent {
  return { key: value, ctrlKey: false, metaKey: false, altKey: false, isComposing: false, repeat: false, ...extra }
}

function fakeClock() {
  let now = 0
  let nextId = 1
  const timers = new Map<number, { at: number; run: () => void }>()
  return {
    setTimeout: (run: () => void, ms: number) => {
      const id = nextId++
      timers.set(id, { at: now + ms, run })
      return id
    },
    clearTimeout: (id: unknown) => void timers.delete(id as number),
    advance(ms: number) {
      now += ms
      for (const [id, timer] of [...timers]) {
        if (timer.at <= now) {
          timers.delete(id)
          timer.run()
        }
      }
    },
    get pending() {
      return timers.size
    },
  }
}

test('text fields, text areas, selects and editable regions are typing, not shortcuts', () => {
  assert.ok(isEditableTarget(node('input')))
  assert.ok(isEditableTarget(node('input', { type: 'search' })))
  assert.ok(isEditableTarget(node('input', { type: 'text' })))
  assert.ok(isEditableTarget(node('input', { type: 'number' })))
  assert.ok(isEditableTarget(node('textarea')))
  assert.ok(isEditableTarget(node('select')))
  assert.ok(isEditableTarget(node('div', {}, null, true)))
  assert.ok(isEditableTarget(node('div', { contenteditable: '' })))
  assert.ok(isEditableTarget(node('div', { contenteditable: 'plaintext-only' })))
  for (const role of ['textbox', 'searchbox', 'combobox', 'spinbutton']) assert.ok(isEditableTarget(node('div', { role })), role)
})

test('a checkbox, radio or button is not typing, so a shortcut pressed on it reaches the page', () => {
  for (const type of ['checkbox', 'radio', 'button', 'submit', 'reset']) assert.equal(isEditableTarget(node('input', { type })), false, type)
  assert.equal(isEditableTarget(node('button')), false)
  assert.equal(isEditableTarget(node('a', { href: '#/queue' })), false)
  assert.equal(isEditableTarget(node('div', { contenteditable: 'false' })), false)
  assert.equal(isEditableTarget(null), false)
})

test('anything inside an editable region counts as typing', () => {
  const editor = node('div', { contenteditable: 'true' })
  assert.ok(isEditableTarget(node('span', {}, node('p', {}, editor))))
  assert.equal(isEditableTarget(node('span', {}, node('p', {}, node('main')))), false)
})

test('menus and listboxes keep their own letter keys, while a plain button gives them up', () => {
  assert.ok(capturesKeys(node('button', { role: 'option' }, node('div', { role: 'listbox' }))))
  assert.ok(capturesKeys(node('button', { role: 'menuitem' }, node('div', { role: 'menu' }))))
  assert.ok(capturesKeys(node('input', { type: 'search' })))
  assert.equal(capturesKeys(node('button', {}, node('td', {}, node('tr')))), false)
})

test('an open dialog is detected from the document, and an absent one is not', () => {
  assert.ok(hasOpenModalDialog({ querySelector: (selector: string) => (selector === 'dialog[open]' ? ({} as Element) : null) } as Pick<Document, 'querySelector'>))
  assert.equal(hasOpenModalDialog({ querySelector: () => null } as Pick<Document, 'querySelector'>), false)
})

test('g then a letter goes to each page, and nothing else does', () => {
  assert.deepEqual(GO_SHORTCUTS, {
    d: '/', l: '/libraries', i: '/inventory', q: '/queue', r: '/quarantine', s: '/schedule', ',': '/settings',
  })
  for (const [second, path] of Object.entries(GO_SHORTCUTS)) {
    const visited: string[] = []
    const clock = fakeClock()
    const chord = createGoChord((to) => visited.push(to), clock)
    assert.equal(chord.handle(key('g')), 'armed')
    assert.equal(chord.handle(key(second)), 'navigated')
    assert.deepEqual(visited, [path])
    assert.equal(chord.armed, false)
    assert.equal(clock.pending, 0)
  }
})

test('the chord lets go after a second without navigating', () => {
  const visited: string[] = []
  const clock = fakeClock()
  const chord = createGoChord((to) => visited.push(to), { ...clock, timeoutMs: 1000 })
  chord.handle(key('g'))
  clock.advance(999)
  assert.equal(chord.armed, true)
  clock.advance(1)
  assert.equal(chord.armed, false)
  assert.equal(chord.handle(key('q')), 'ignored')
  assert.deepEqual(visited, [])
})

test('Escape, an unknown key or a modified key cancels the chord and is not reused', () => {
  for (const second of [key('Escape'), key('x'), key('q', { ctrlKey: true }), key('q', { metaKey: true }), key('q', { altKey: true })]) {
    const visited: string[] = []
    const clock = fakeClock()
    const chord = createGoChord((to) => visited.push(to), clock)
    chord.handle(key('g'))
    assert.equal(chord.handle(second), 'cancelled')
    assert.equal(chord.armed, false)
    assert.equal(clock.pending, 0)
    assert.deepEqual(visited, [])
  }
})

test('the chord only arms on a plain lowercase g', () => {
  const chord = createGoChord(() => assert.fail('must not navigate'), fakeClock())
  for (const event of [key('G'), key('g', { ctrlKey: true }), key('g', { metaKey: true }), key('g', { altKey: true }), key('g', { repeat: true }), key('q')]) {
    assert.equal(chord.handle(event), 'ignored')
    assert.equal(chord.armed, false)
  }
})

test('a key composed by an input method never arms or finishes the chord', () => {
  const visited: string[] = []
  const chord = createGoChord((to) => visited.push(to), fakeClock())
  assert.equal(chord.handle(key('g', { isComposing: true })), 'ignored')
  assert.equal(chord.armed, false)
  chord.handle(key('g'))
  assert.equal(chord.handle(key('q', { isComposing: true })), 'ignored')
  assert.equal(chord.armed, true)
  assert.deepEqual(visited, [])
})

test('Shift on its way to the next key, or a held g, leaves the chord waiting', () => {
  const visited: string[] = []
  const chord = createGoChord((to) => visited.push(to), fakeClock())
  chord.handle(key('g'))
  assert.equal(chord.handle(key('Shift')), 'armed')
  assert.equal(chord.handle(key('g', { repeat: true })), 'armed')
  assert.equal(chord.handle(key('i')), 'navigated')
  assert.deepEqual(visited, ['/inventory'])
})

test('disposing the chord clears its timer', () => {
  const clock = fakeClock()
  const chord = createGoChord(() => {}, clock)
  chord.handle(key('g'))
  assert.equal(clock.pending, 1)
  chord.dispose()
  assert.equal(clock.pending, 0)
  assert.equal(chord.armed, false)
})

test('j and k start at the ends of the list and stop at its edges', () => {
  assert.equal(nextRowIndex(-1, 4, 'next'), 0)
  assert.equal(nextRowIndex(-1, 4, 'prev'), 3)
  assert.equal(nextRowIndex(1, 4, 'next'), 2)
  assert.equal(nextRowIndex(1, 4, 'prev'), 0)
  assert.equal(nextRowIndex(3, 4, 'next'), 3)
  assert.equal(nextRowIndex(0, 4, 'prev'), 0)
  assert.equal(nextRowIndex(-1, 0, 'next'), -1)
})

test('Apple platforms show the command key, everything else shows Ctrl', () => {
  assert.ok(isApplePlatform({ platform: 'MacIntel' }))
  assert.ok(isApplePlatform({ userAgentData: { platform: 'macOS' } }))
  assert.ok(isApplePlatform({ userAgent: 'Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X)' }))
  assert.equal(isApplePlatform({ platform: 'Win32' }), false)
  assert.equal(isApplePlatform({ platform: 'Linux x86_64' }), false)
  assert.equal(isApplePlatform(undefined), false)
})

const groups = { pages: en.shortcuts.jump_pages, settings: en.shortcuts.jump_settings, libraries: en.shortcuts.jump_libraries }
const libraries = [{ id: 1, name: 'Films' }, { id: 4, name: 'Family photos' }]

test('Quick Jump lists every page, every settings room and each library, and only navigation', () => {
  const items = quickJumpItems(en, libraries)
  assert.deepEqual(items.filter((item) => item.group === 'pages').map((item) => item.path), [
    '/', '/libraries', '/inventory', '/queue', '/queue/failures', '/quarantine', '/schedule', '/settings',
  ])
  assert.deepEqual(items.filter((item) => item.group === 'settings').map((item) => item.path), [
    '/settings/encoding', '/settings/super-resolution', '/settings/files', '/settings/media-servers',
    '/settings/download-managers', '/settings/notifications', '/settings/workers', '/settings/system',
  ])
  assert.deepEqual(items.filter((item) => item.group === 'libraries').map((item) => item.path), ['/libraries/1/configure', '/libraries/4/configure'])
  assert.equal(items.find((item) => item.path === '/queue')?.chord, 'q')
  assert.equal(items.find((item) => item.path === '/queue/failures')?.chord, undefined)
  assert.equal(new Set(items.map((item) => item.id)).size, items.length)
  assert.equal(items.find((item) => item.path === '/settings/workers')?.label, en.settings.room_workers)
})

test('Quick Jump works without libraries when they could not be loaded', () => {
  const items = quickJumpItems(en)
  assert.equal(items.filter((item) => item.group === 'libraries').length, 0)
  assert.equal(items.length, 16)
})

test('Quick Jump filters by every word typed, ignoring case and accents', () => {
  const items = quickJumpItems(en, [...libraries, { id: 9, name: 'Séries' }])
  assert.deepEqual(filterJumpItems(items, '', groups).length, items.length)
  assert.deepEqual(filterJumpItems(items, '  WORK ', groups).map((item) => item.path), ['/settings/workers'])
  assert.deepEqual(filterJumpItems(items, 'series', groups).map((item) => item.path), ['/libraries/9/configure'])
  assert.deepEqual(filterJumpItems(items, 'fam pho', groups).map((item) => item.path), ['/libraries/4/configure'])
  assert.deepEqual(filterJumpItems(items, 'nothing like this', groups), [])
})

test('Quick Jump keeps groups in order and puts names that start with the query first', () => {
  const items = quickJumpItems(en, [{ id: 2, name: 'Queued films' }])
  const found = filterJumpItems(items, 'queue', groups).map((item) => item.path)
  assert.deepEqual(found, ['/queue', '/libraries/2/configure'])
  const settings = filterJumpItems(items, 'settings', groups)
  assert.equal(settings[0].path, '/settings')
  assert.ok(settings.slice(1).every((item) => item.group === 'settings'))
})

test('arrow keys wrap around the options', () => {
  assert.equal(wrapIndex(-1, 3, 1), 0)
  assert.equal(wrapIndex(-1, 3, -1), 2)
  assert.equal(wrapIndex(2, 3, 1), 0)
  assert.equal(wrapIndex(0, 3, -1), 2)
  assert.equal(wrapIndex(0, 0, 1), -1)
})
