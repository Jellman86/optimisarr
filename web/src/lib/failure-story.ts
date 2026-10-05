// Why a job failed, in the order a person would want to hear it. The server files a failure
// under the first gate it classifies (often "Size saving"), but when several gates fail the most
// fundamental one is the honest headline: a damaged source makes every other gate meaningless, and
// a broken output explains a bad quality score. Gate names are the server's stable check names.

export type FailureCause =
  | 'damaged_source'
  | 'broken_output'
  | 'looks_worse'
  | 'sounds_worse'
  | 'lost_content'
  | 'size_predicted'
  | 'too_big'

type Check = { name: string; outcome: string }

const SOURCE = ['Source video timeline']
const BROKEN = ['Decode health', 'Output readable', 'Video stream', 'Video structure', 'Timestamp integrity', 'Tail integrity', 'Duration']
const LOST = [
  'Audio tracks', 'Subtitle tracks', 'Audio languages', 'Subtitle languages', 'Audio fidelity', 'Audio codec',
  'Audio codecs unchanged', 'Audio metadata and artwork', 'Colour metadata', 'HDR signal', 'A/V sync', 'Dimensions',
  'Container unchanged', 'Picture retention', 'EXIF metadata', 'ICC colour profile', 'Image metadata (EXIF/ICC)',
]

function failed(checks: readonly Check[], names: readonly string[]): boolean {
  return checks.some((check) => check.outcome === 'Failed' && names.includes(check.name))
}

function failedMatching(checks: readonly Check[], pattern: RegExp): boolean {
  return checks.some((check) => check.outcome === 'Failed' && pattern.test(check.name))
}

/**
 * The single cause to lead with, or null when nothing more specific than the server's category
 * is known (the caller then falls back to the category's own description).
 */
export function primaryFailureCause(
  checks: readonly Check[] | null | undefined,
  errorMessage?: string | null,
): FailureCause | null {
  const list = checks ?? []
  const message = errorMessage ?? ''
  // A source rejected before encoding has no report, only the message naming the gate.
  if (failed(list, SOURCE) || /Source video timeline/.test(message)) return 'damaged_source'
  if (message.startsWith('Size saving prediction:')) return 'size_predicted'
  if (failed(list, BROKEN)) return 'broken_output'
  if (failedMatching(list, /^Perceptual quality \(VMAF\)|^Image quality/)) return 'looks_worse'
  if (failedMatching(list, /audio quality|^Audio clipping|^Audio loudness/i)) return 'sounds_worse'
  if (failed(list, LOST)) return 'lost_content'
  if (failed(list, ['Size saving'])) return 'too_big'
  return null
}

/** Names of every failed gate, for an "also failed" line under the headline. */
export function failedCheckNames(checks: readonly Check[] | null | undefined): string[] {
  return (checks ?? []).filter((check) => check.outcome === 'Failed').map((check) => check.name)
}
