import { en, type Messages } from './en'
import { de } from './de'
import { es } from './es'

// Re-exported so components can import the contract type from the i18n entry point.
// `npm run check` (svelte-check + tsc, run in CI) is the translation-completeness gate:
// a locale that doesn't satisfy `Messages` fails the build.
export type { Messages }

// Registry of fully-translated locales. Add an entry only when its file is complete;
// the `Messages` type guarantees completeness at compile time.
const REGISTRY = { en, de, es } satisfies Record<string, Messages>

export type LocaleCode = keyof typeof REGISTRY

// Shown in the language selector, in each language's own name (endonym).
export const AVAILABLE_LOCALES: ReadonlyArray<{ code: LocaleCode; name: string }> = [
  { code: 'en', name: 'English' },
  { code: 'de', name: 'Deutsch' },
  { code: 'es', name: 'Español' },
]

const STORAGE_KEY = 'optimisarr:locale'

function isLocale(code: string): code is LocaleCode {
  return code in REGISTRY
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
  let locale = $state<LocaleCode>(detectInitial())
  return {
    get locale() {
      return locale
    },
    // The active locale's messages, fully typed. Reading this in markup makes the
    // component re-render when the language changes.
    get m(): Messages {
      return REGISTRY[locale]
    },
    set(code: string) {
      if (!isLocale(code)) return
      locale = code
      try {
        localStorage.setItem(STORAGE_KEY, code)
      } catch {
        // Persisting the choice is best-effort; the in-memory selection still applies.
      }
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
