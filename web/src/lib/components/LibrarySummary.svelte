<script lang="ts">
  // The short first view of a library: what it does, in sentences worked out from its settings.
  // It is a summary, not a short form. A short form leaves settings out; this shows the effect of
  // all of them, and a warning tag on a line cannot be collapsed away.
  import { i18n, t } from '../i18n/i18n.svelte'
  import type { SummaryLine, SummaryTopic } from '../library-summary'
  import Icon from './Icon.svelte'

  let { lines, onedit = undefined, unsaved = false, headingId = 'library-summary-heading', intro = undefined }: {
    lines: SummaryLine[]
    /** Said under the heading instead of the editor's own sentence, where there is no Edit to point at. */
    intro?: string
    /** Opens the page holding every control behind a line. Leave out where nothing can be edited. */
    onedit?: (topic: SummaryTopic) => void
    unsaved?: boolean
    headingId?: string
  } = $props()

  const icons: Record<SummaryTopic, string> = {
    files: 'folder', video: 'film', audio: 'waveform', images: 'copies', checks: 'shield-check', schedule: 'clock',
  }
</script>

<section class="card overflow-hidden" aria-labelledby={headingId} data-library-summary data-config-section="">
  <header class="px-4 py-4 hairline-b sm:px-6">
    <h2 id={headingId} class="text-base font-semibold text-ink">{i18n.m.librarySummary.title}</h2>
    <p class="mt-0.5 text-sm leading-relaxed text-ink-3">
      {intro ?? i18n.m.librarySummary.intro}
      {#if unsaved}<span class="text-warn" data-summary-unsaved>{i18n.m.librarySummary.unsaved_note}</span>{/if}
    </p>
  </header>
  <ul class="divide-y divide-line-soft px-4 sm:px-6">
    {#each lines as line (line.topic)}
      <li class="flex items-start gap-3 py-4 sm:gap-4" data-summary-line={line.topic}>
        <span class="mt-0.5 hidden h-9 w-9 flex-none items-center justify-center rounded-xl bg-raised text-accent sm:flex">
          <Icon name={icons[line.topic]} class="h-4 w-4" />
        </span>
        <div class="min-w-0 flex-1">
          <h3 class="text-xs font-semibold text-ink-3">{line.title}</h3>
          <p class="mt-1 break-words text-sm leading-relaxed text-ink">{line.text}</p>
          {#if line.detail}<p class="mt-1 text-xs text-ink-3" data-summary-detail>{line.detail}</p>{/if}
          {#if line.tags.length > 0}
            <div class="mt-2 flex flex-wrap gap-1.5">
              {#each line.tags as tag (tag.text)}
                <span class="badge {tag.tone === 'warn' ? 'tone-warn' : 'tone-neutral'}" data-summary-tag={tag.tone}>{tag.text}</span>
              {/each}
            </div>
          {/if}
        </div>
        {#if onedit}
          <button type="button" class="btn min-h-11 flex-none" aria-label={t(i18n.m.librarySummary.edit_topic, { topic: line.title })} onclick={() => onedit(line.topic)}>
            {i18n.m.librarySummary.edit}
          </button>
        {/if}
      </li>
    {/each}
  </ul>
</section>
