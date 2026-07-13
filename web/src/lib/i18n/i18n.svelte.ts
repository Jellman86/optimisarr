import { en, type Messages } from './en'

// Re-exported so components can import the contract type from the i18n entry point.
// `npm run check` (svelte-check + tsc, run in CI) is the translation-completeness gate:
// a locale that doesn't satisfy `Messages` fails the build.
export type { Messages }

// Load translations on demand so adding languages does not inflate the initial application chunk.
// Each imported locale is independently typed as Messages, and therefore still fails compilation
// if it is incomplete.
const LOADERS = {
  en: async () => en,
  de: async () => (await import('./de')).de,
  es: async () => (await import('./es')).es,
  fr: async () => (await import('./fr')).fr,
} satisfies Record<string, () => Promise<Messages>>

export type LocaleCode = keyof typeof LOADERS

// Shown in the language selector, in each language's own name (endonym).
export const AVAILABLE_LOCALES: ReadonlyArray<{ code: LocaleCode; name: string }> = [
  { code: 'en', name: 'English' },
  { code: 'de', name: 'Deutsch' },
  { code: 'es', name: 'Español' },
  { code: 'fr', name: 'Français' },
]

const STORAGE_KEY = 'optimisarr:locale'

function isLocale(code: string): code is LocaleCode {
  return code in LOADERS
}

function detectInitial(): LocaleCode {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    if (saved && isLocale(saved)) return saved
  } catch {
    // localStorage unavailable (private mode / SSR) — fall through to detection.
  }
  const nav = typeof navigator !== 'undefined' ? navigator.language.slice(0, 2).toLowerCase() : 'en'
  return isLocale(nav) ? nav : 'en'
}

function createI18n() {
  let locale = $state<LocaleCode>('en')
  let messages = $state<Messages>(en)
  let loadSequence = 0

  async function apply(code: LocaleCode, persist: boolean) {
    const sequence = ++loadSequence
    const loaded = await LOADERS[code]()
    if (sequence !== loadSequence) return
    locale = code
    messages = loaded
    if (!persist) return
    try {
      localStorage.setItem(STORAGE_KEY, code)
    } catch {
      // Persisting the choice is best-effort; the in-memory selection still applies.
    }
  }

  const initial = detectInitial()
  if (initial !== 'en') void apply(initial, false)

  return {
    get locale() {
      return locale
    },
    // The active locale's messages, fully typed. Reading this in markup makes the
    // component re-render when the language changes.
    get m(): Messages {
      return messages
    },
    set(code: string) {
      if (!isLocale(code)) return
      void apply(code, true)
    },
  }
}

export const i18n = createI18n()

// Fill `{token}` placeholders in a resolved message. Unknown tokens are left as-is
// so a missing value is visible rather than silently dropped.
export function t(template: string, params: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (_, key: string) =>
    key in params ? String(params[key]) : `{${key}}`,
  )
}

// Pick the singular/plural message for a count and interpolate `{count}`. English and
// German share the one/other split; locales with richer plural rules can be handled when
// they are added. Pass a preformatted `display` when the number needs locale grouping.
export function plural(
  count: number,
  one: string,
  other: string,
  display: string | number = count,
): string {
  return t(count === 1 ? one : other, { count: display })
}
