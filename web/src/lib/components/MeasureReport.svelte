<script lang="ts">
  // The one shell every quality and search report wears. It is a section of whatever card or
  // dialog holds it, divided by a hairline rather than boxed in a card of its own, so proof never
  // nests surfaces three deep. A nested report (one track inside a soundtrack) drops the divider
  // and steps its heading down, because the track above it already separates it.
  import type { Snippet } from 'svelte'
  import InfoTip from './InfoTip.svelte'

  type Tone = 'ok' | 'bad' | 'muted'
  let { title, hint, hintLabel, badge, note, nested = false, children }: {
    title: string
    hint?: string
    hintLabel?: string
    badge?: { text: string; tone: Tone } | null
    note?: string
    nested?: boolean
    children: Snippet
  } = $props()
</script>

<section class="min-w-0 {nested ? 'mt-3' : 'hairline-t mt-5 pt-4 first:mt-0 first:pt-0 first:shadow-none'}" aria-label={title} data-measure-report>
  <div class="flex min-w-0 flex-wrap items-center justify-between gap-2">
    <div class="flex min-w-0 items-center gap-2">
      {#if nested}
        <h5 class="break-words text-label font-semibold text-ink-2">{title}</h5>
      {:else}
        <h3 class="break-words text-body font-semibold text-ink">{title}</h3>
      {/if}
      {#if hint}<InfoTip label={hintLabel} text={hint} />{/if}
    </div>
    {#if badge}<span class="badge tone-{badge.tone}">{badge.text}</span>{/if}
  </div>
  {@render children()}
  {#if note}<p class="hairline-t mt-3 pt-3 text-caption leading-relaxed text-ink-3">{note}</p>{/if}
</section>
