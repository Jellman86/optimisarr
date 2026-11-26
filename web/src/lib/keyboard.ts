import type { Messages } from './i18n/en.ts'
import { SETTINGS_ROOM_PATHS, settingsRoomTitle, type SettingsRoomKey } from './route-meta.ts'

// The application's keyboard shortcuts. Every one of them moves, finds, focuses, selects or opens
// something to look at; none of them changes a file, a job or a setting. Anything that replaces,
// deletes, approves, starts or stops work keeps needing a deliberate press on its own button.
//
// The decisions are pure and testable without a browser. The few helpers that touch the page
// work through a deliberately small slice of the DOM so they stay predictable.

/** The minimum of an element the editable-target walk reads, so it can be tested without a DOM. */
export type KeyTargetNode = {
  tagName?: string
  isContentEditable?: boolean
  getAttribute?: (name: string) => string | null
  parentElement?: KeyTargetNode | null
}

// Input types that never take typed text; a shortcut pressed on one of them is meant for the page.
const NON_TEXT_INPUT_TYPES = new Set(['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color', 'file', 'image'])
const TEXT_ROLES = new Set(['textbox', 'searchbox', 'combobox', 'spinbutton'])
// Widgets that use letters and arrows themselves: a shortcut must not steal a key from them.
const KEY_OWNING_ROLES = new Set(['menu', 'menubar', 'listbox', 'slider', 'tablist', 'grid', 'tree'])

function role(node: KeyTargetNode): string | null {
  return node.getAttribute?.('role')?.trim().toLowerCase() ?? null
}

function isTypingNode(node: KeyTargetNode): boolean {
  const tag = node.tagName?.toLowerCase()
  if (tag === 'textarea' || tag === 'select') return true
  if (tag === 'input') {
    const type = (node.getAttribute?.('type') ?? 'text').trim().toLowerCase()
    return !NON_TEXT_INPUT_TYPES.has(type)
  }
  if (node.isContentEditable) return true
  const editable = node.getAttribute?.('contenteditable')
  if (editable != null && editable.toLowerCase() !== 'false') return true
  const name = role(node)
  return name !== null && TEXT_ROLES.has(name)
}

function closest(target: EventTarget | KeyTargetNode | null, test: (node: KeyTargetNode) => boolean): boolean {
  let node = target as KeyTargetNode | null
  for (let depth = 0; node && depth < 64; depth++) {
    if (test(node)) return true
    node = node.parentElement ?? null
  }
  return false
}

/** True when a key pressed on this target is somebody typing, not a shortcut. */
export function isEditableTarget(target: EventTarget | KeyTargetNode | null): boolean {
  return closest(target, isTypingNode)
}

/** True when the target is typing, or a widget (menu, listbox, slider…) that reads letters itself. */
export function capturesKeys(target: EventTarget | KeyTargetNode | null): boolean {
  return closest(target, (node) => {
    if (isTypingNode(node)) return true
    const name = role(node)
    return name !== null && KEY_OWNING_ROLES.has(name)
  })
}

/** True while any modal dialog is open: its own work must never be interrupted by a shortcut. */
export function hasOpenModalDialog(root: Pick<Document, 'querySelector'> = document): boolean {
  return root.querySelector('dialog[open]') !== null
}

/** The key that follows `g`, and the page it goes to. */
export const GO_SHORTCUTS = {
  d: '/',
  l: '/libraries',
  i: '/inventory',
  q: '/queue',
  r: '/quarantine',
  s: '/schedule',
  ',': '/settings',
} as const satisfies Record<string, string>

export type GoKey = keyof typeof GO_SHORTCUTS

export type ShortcutKeyEvent = Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'metaKey' | 'altKey'> & {
  isComposing?: boolean
  repeat?: boolean
}

export function hasCommandModifier(event: ShortcutKeyEvent): boolean {
  return event.ctrlKey || event.metaKey || event.altKey
}

/**
 * - `ignored`: the chord had nothing to do with this key; other shortcuts may use it.
 * - `armed`: `g` was pressed and the chord waits for the next key.
 * - `navigated`: the chord finished and went to a page.
 * - `cancelled`: the chord was waiting and this key ended it; nothing else should act on it.
 */
export type ChordResult = 'ignored' | 'armed' | 'navigated' | 'cancelled'

export type GoChordOptions = {
  timeoutMs?: number
  setTimeout?: (callback: () => void, ms: number) => unknown
  clearTimeout?: (handle: unknown) => void
}

const MODIFIER_KEYS = new Set(['Shift', 'Control', 'Alt', 'Meta', 'AltGraph', 'CapsLock'])

/** `g` then a letter goes to a page. A second key that is not one of them simply lets it go. */
export function createGoChord(onNavigate: (path: string) => void, options: GoChordOptions = {}) {
  const timeoutMs = options.timeoutMs ?? 1000
  const schedule = options.setTimeout ?? ((callback, ms) => globalThis.setTimeout(callback, ms))
  const unschedule = options.clearTimeout ?? ((handle) => globalThis.clearTimeout(handle as ReturnType<typeof setTimeout>))
  let timer: unknown = null

  function disarm() {
    if (timer !== null) unschedule(timer)
    timer = null
  }

  return {
    get armed() {
      return timer !== null
    },
    handle(event: ShortcutKeyEvent): ChordResult {
      if (event.isComposing) return 'ignored'
      if (timer === null) {
        if (event.key !== 'g' || event.repeat || hasCommandModifier(event)) return 'ignored'
        timer = schedule(disarm, timeoutMs)
        return 'armed'
      }
      // Shift on its own is the start of the next key, not a different one.
      if (MODIFIER_KEYS.has(event.key)) return 'armed'
      if (event.repeat) return 'armed'
      disarm()
      if (hasCommandModifier(event) || !Object.hasOwn(GO_SHORTCUTS, event.key)) return 'cancelled'
      onNavigate(GO_SHORTCUTS[event.key as GoKey])
      return 'navigated'
    },
    cancel: disarm,
    dispose: disarm,
  }
}

export type GoChord = ReturnType<typeof createGoChord>

/** Where focus goes after `j` or `k`, clamped to the list. -1 means there is no row to go to. */
export function nextRowIndex(current: number, count: number, direction: 'next' | 'prev'): number {
  if (count <= 0) return -1
  if (current < 0 || current >= count) return direction === 'next' ? 0 : count - 1
  return direction === 'next' ? Math.min(current + 1, count - 1) : Math.max(current - 1, 0)
}

/** Whether the platform names its command key ⌘ rather than Ctrl. */
export function isApplePlatform(navigatorLike: { platform?: string; userAgent?: string; userAgentData?: { platform?: string } } | undefined): boolean {
  if (!navigatorLike) return false
  const platform = navigatorLike.userAgentData?.platform || navigatorLike.platform || navigatorLike.userAgent || ''
  return /mac|iphone|ipad|ipod/i.test(platform)
}

// ---- Lists ---------------------------------------------------------------------------------------
// An operational table opts in with `data-keyboard-list`, and marks the control that opens a row
// with `data-row-primary`. Without one, a row is reached through its selection checkbox or link,
// never through an arbitrary button: a row's other buttons may be the ones that change files.

const ROW_SELECTOR = 'tbody tr'
const ROW_CHECKBOX = '.selection-column input[type="checkbox"]:not([disabled])'
const ROW_FALLBACK = [ROW_CHECKBOX, 'a[href]']

function rowTarget(row: Element): HTMLElement | null {
  const primary = row.querySelector<HTMLElement>('[data-row-primary]')
  if (primary) return primary
  for (const selector of ROW_FALLBACK) {
    const found = row.querySelector<HTMLElement>(selector)
    if (found) return found
  }
  return null
}

function visibleRows(list: Element): HTMLElement[] {
  return [...list.querySelectorAll<HTMLElement>(ROW_SELECTOR)].filter((row) => row.getClientRects().length > 0 && rowTarget(row))
}

function focusedRow(list: Element): HTMLElement | null {
  const active = document.activeElement
  if (!active || !list.contains(active)) return null
  return active.closest<HTMLElement>(ROW_SELECTOR)
}

/** `j` / `k`: focus the next or previous row's opener. False when the list has no rows. */
export function moveTableRowFocus(list: Element | null, direction: 'next' | 'prev'): boolean {
  if (!list) return false
  const rows = visibleRows(list)
  const current = focusedRow(list)
  const index = nextRowIndex(current ? rows.indexOf(current) : -1, rows.length, direction)
  if (index < 0) return false
  const target = rowTarget(rows[index])
  if (!target) return false
  target.focus({ preventScroll: true })
  target.scrollIntoView({ block: 'nearest' })
  return true
}

/** `x`: select or clear the focused row, keeping focus on the row so `j` `x` `j` `x` keeps going. */
export function toggleFocusedRowSelection(list: Element | null): boolean {
  if (!list) return false
  const row = focusedRow(list)
  const box = row?.querySelector<HTMLInputElement>(ROW_CHECKBOX)
  if (!row || !box) return false
  box.click()
  const primary = row.querySelector<HTMLElement>('[data-row-primary]')
  if (primary && primary.isConnected) primary.focus({ preventScroll: true })
  return true
}

/**
 * Enter on a row's selection checkbox opens the row. Enter on a button or link already presses
 * it natively, so this declines rather than pressing it twice.
 */
export function openFocusedRow(list: Element | null, target: EventTarget | null): boolean {
  if (!list || !(target instanceof HTMLInputElement) || target.type !== 'checkbox') return false
  if (!list.contains(target)) return false
  const primary = target.closest(ROW_SELECTOR)?.querySelector<HTMLElement>('[data-row-primary]')
  if (!primary) return false
  primary.click()
  return true
}

// ---- Quick jump ----------------------------------------------------------------------------------

export type JumpGroup = 'pages' | 'settings' | 'libraries'

export type JumpItem = {
  id: string
  group: JumpGroup
  label: string
  path: string
  /** The `g` chord that also reaches it, shown beside the page. */
  chord?: GoKey
}

const PAGES: { path: string; label: (m: Messages) => string; chord?: GoKey }[] = [
  { path: '/', label: (m) => m.nav.dashboard, chord: 'd' },
  { path: '/libraries', label: (m) => m.nav.libraries, chord: 'l' },
  { path: '/inventory', label: (m) => m.nav.inventory, chord: 'i' },
  { path: '/queue', label: (m) => m.nav.queue, chord: 'q' },
  { path: '/queue/failures', label: (m) => m.queue.tab_failures },
  { path: '/quarantine', label: (m) => m.nav.quarantine, chord: 'r' },
  { path: '/schedule', label: (m) => m.nav.schedule, chord: 's' },
  { path: '/settings', label: (m) => m.nav.settings, chord: ',' },
]

/** Everything Quick Jump can go to, in the order it lists them. Navigation only, by construction. */
export function quickJumpItems(m: Messages, libraries: readonly { id: number; name: string }[] = []): JumpItem[] {
  const pages: JumpItem[] = PAGES.map((page) => ({ id: `page:${page.path}`, group: 'pages', label: page.label(m), path: page.path, chord: page.chord }))
  const rooms: JumpItem[] = (Object.entries(SETTINGS_ROOM_PATHS) as [SettingsRoomKey, string][]).map(([room, slug]) => ({
    id: `settings:${slug}`,
    group: 'settings',
    label: settingsRoomTitle(room, m),
    path: `/settings/${slug}`,
  }))
  const libs: JumpItem[] = libraries.map((library) => ({
    id: `library:${library.id}`,
    group: 'libraries',
    label: library.name,
    path: `/libraries/${library.id}/configure`,
  }))
  return [...pages, ...rooms, ...libs]
}

function fold(text: string): string {
  return text.normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase()
}

/**
 * The items whose name (or group's name) holds every word typed, in any order. Within a group, a
 * name that starts with what was typed comes before one that only contains it.
 */
export function filterJumpItems(items: readonly JumpItem[], query: string, groupLabels: Record<JumpGroup, string>): JumpItem[] {
  const terms = fold(query).split(/\s+/).filter(Boolean)
  if (terms.length === 0) return [...items]
  const matches = items.filter((item) => {
    const haystack = fold(`${item.label} ${groupLabels[item.group]}`)
    return terms.every((term) => haystack.includes(term))
  })
  const order: JumpGroup[] = ['pages', 'settings', 'libraries']
  const startsWith = (item: JumpItem) => (fold(item.label).startsWith(terms[0]) ? 0 : 1)
  return matches
    .map((item, index) => ({ item, index }))
    .sort((a, b) => order.indexOf(a.item.group) - order.indexOf(b.item.group) || startsWith(a.item) - startsWith(b.item) || a.index - b.index)
    .map(({ item }) => item)
}

/** The active option after an arrow key, wrapping at either end. */
export function wrapIndex(current: number, count: number, step: 1 | -1): number {
  if (count <= 0) return -1
  if (current < 0 || current >= count) return step === 1 ? 0 : count - 1
  return (current + step + count) % count
}
