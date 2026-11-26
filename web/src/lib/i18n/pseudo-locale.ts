// A development-only stand-in for a long translation: every English string accented and padded
// by at least 40%, the growth translators plan for, so a layout that clips, overflows or
// overlaps is found before a real locale ships it. It is not a language. It is never listed in
// the language selector, and a production build does not include it.

/** How much longer than English every pseudo string is, at least. */
export const PSEUDO_EXPANSION = 0.4

const ACCENTED: Record<string, string> = Object.fromEntries(
  [...'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ'].map((letter, index) => [
    letter,
    [...'áƀçðéƒĝĥíĵķļɱñóþǫŕšţúṽŵẋýžÁƁÇÐÉƑĜĤÍĴĶĻṀÑÓÞǪŔŠŢÚṼŴẊÝŽ'][index],
  ]),
)

// Short words, so the padding wraps the way a translated sentence does instead of forming one
// unbreakable run that no real language would produce.
const FILLER = ['ļóŕéɱ', 'íþšúɱ', 'ðóļóŕ', 'šíţ', 'áɱéţ', 'çóñšé', 'ţéţúŕ']

const PLACEHOLDER = /(\{\w+\})/

function accent(text: string): string {
  return [...text].map((character) => ACCENTED[character] ?? character).join('')
}

function padding(length: number): string {
  const words: string[] = []
  let used = 0
  for (let index = 0; used < length; index++) {
    const word = FILLER[index % FILLER.length]
    words.push(word)
    used += [...word].length + (words.length > 1 ? 1 : 0)
  }
  return words.join(' ')
}

/** One English string, accented and bracketed, at least 40% longer; `{tokens}` stay intact. */
export function pseudoString(source: string): string {
  if (!/[A-Za-z]/.test(source)) return source
  const [, leading, body, trailing] = /^(\s*)([\s\S]*?)(\s*)$/.exec(source)!
  const accented = body.split(PLACEHOLDER).map((part) => (PLACEHOLDER.test(part) ? part : accent(part))).join('')
  const target = Math.ceil([...source].length * (1 + PSEUDO_EXPANSION))
  // Brackets mark both ends, so a clipped string is obvious at a glance.
  const fixed = [...leading].length + [...trailing].length + [...accented].length + '[ ]'.length
  return `${leading}[${accented} ${padding(Math.max(1, target - fixed))}]${trailing}`
}

/** A whole message tree, same shape, every string run through `pseudoString`. */
export function pseudoLocalise<T>(messages: T): T {
  if (typeof messages === 'string') return pseudoString(messages) as T
  if (messages === null || typeof messages !== 'object') return messages
  return Object.fromEntries(
    Object.entries(messages).map(([key, value]) => [key, pseudoLocalise(value)]),
  ) as T
}
