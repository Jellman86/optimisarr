import type { Messages } from './i18n.svelte'

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
