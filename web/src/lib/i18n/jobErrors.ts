import type { Messages } from './i18n.svelte'
import { failedCheckNames, primaryFailureCause } from '../failure-story'

// FailureCategory is persisted by the API and is therefore a stable translation key. Keep the
// backend description as a compatibility fallback for categories introduced by a newer server.
export function jobFailureDescription(
  category: string | null,
  messages: Messages,
  fallback?: string | null,
): string {
  switch (category?.toLowerCase()) {
    case 'sizesaving': return messages.queue.failure_size_saving
    case 'verification': return messages.queue.failure_verification
    case 'containerincompatibility': return messages.queue.failure_container_incompatibility
    case 'bitmapsubtitles': return messages.queue.failure_bitmap_subtitles
    case 'replacementcollision': return messages.queue.failure_replacement_collision
    case 'sourcemissing': return messages.queue.failure_source_missing
    case 'invalidconfiguration': return messages.queue.failure_invalid_configuration
    case 'other': return messages.queue.failure_other
    default: return fallback || messages.queue.job_failed
  }
}

export type FailureStory = {
  headline: string
  hint: string | null
  /** Every failed gate, so the headline never hides that others failed too. */
  failedChecks: string[]
}

/**
 * The failure told by its most fundamental cause, falling back to the server's category when
 * the report says nothing more specific. Used by every surface that explains a failed job.
 */
export function jobFailureStory(
  category: string | null,
  errorMessage: string | null | undefined,
  checks: readonly { name: string; outcome: string }[] | null | undefined,
  messages: Messages,
): FailureStory {
  const failedChecks = failedCheckNames(checks)
  const q = messages.queue
  switch (primaryFailureCause(checks, errorMessage)) {
    case 'damaged_source': return { headline: q.cause_damaged_source, hint: q.cause_damaged_source_hint, failedChecks }
    case 'broken_output': return { headline: q.cause_broken_output, hint: q.cause_broken_output_hint, failedChecks }
    case 'looks_worse': return { headline: q.cause_looks_worse, hint: q.cause_looks_worse_hint, failedChecks }
    case 'sounds_worse': return { headline: q.cause_sounds_worse, hint: q.cause_sounds_worse_hint, failedChecks }
    case 'lost_content': return { headline: q.cause_lost_content, hint: q.cause_lost_content_hint, failedChecks }
    case 'size_predicted': return { headline: q.failure_size_prediction, hint: null, failedChecks }
    case 'too_big': return { headline: q.cause_too_big, hint: q.cause_too_big_hint, failedChecks }
    default: return { headline: jobFailureDescription(category, messages), hint: null, failedChecks }
  }
}

/**
 * A few words per category for one-line summaries such as the Dashboard's "Needs you". Rarer
 * categories keep the server's own description, which still names the cause.
 */
export function jobFailureShortLabel(category: string | null, messages: Messages, fallback?: string | null): string {
  switch (category?.toLowerCase()) {
    case 'sizesaving': return messages.queue.failure_short_size_saving
    case 'verification': return messages.queue.failure_short_verification
    default: return fallback || messages.queue.failure_short_other
  }
}
