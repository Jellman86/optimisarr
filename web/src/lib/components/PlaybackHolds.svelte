<script lang="ts">
  import type { PlaybackHold } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { playbackTitle, playbackViewer, playbackPhrases } from '../playback'
  import Icon from './Icon.svelte'
  import Thumbnail from './Thumbnail.svelte'

  let { holds }: { holds: PlaybackHold[] } = $props()
  let phrases = $derived(playbackPhrases(i18n.m))
  const SHOWN = 8
  let shown = $derived(holds.slice(0, SHOWN))
</script>

{#snippet playback(hold: PlaybackHold)}
  <li class="playback-card">
    <Thumbnail src={hold.artworkUrl ?? null} size="md" shape={hold.kind === 'Track' ? 'square' : 'portrait'} />
    <div class="playback-copy">
      <!-- Hidden separators keep the card readable as one line to screen readers and copy. -->
      <span class="playback-title">{playbackTitle(hold, phrases)}</span>
      {#if playbackViewer(hold, phrases)}<span class="sr-only">{' · '}</span><span class="playback-viewer">{playbackViewer(hold, phrases)}</span>{/if}
      <span class="playback-meta">
        <span class="sr-only">{' · '}</span><span class="playback-chip">{hold.watcher}</span>
        <span class="sr-only">{' · '}</span><span class="playback-chip" class:playback-live={!hold.paused}>
          <Icon name={hold.paused ? 'pause' : 'play'} class="h-3 w-3 flex-none" />{hold.paused ? i18n.m.queue.playback_state_paused : i18n.m.queue.playback_state_playing}
        </span>
      </span>
    </div>
  </li>
{/snippet}

{#if holds.length > 0}
  <ul class="playback-holds" aria-label={i18n.m.queue.playback_heading}>
    {#each shown as hold, index (index)}{@render playback(hold)}{/each}
    {#if holds.length > SHOWN}
      <li class="playback-more">
        <details>
          <summary class="focus-ring cursor-pointer text-ink-3">{t(i18n.m.queue.playback_more, { count: holds.length - SHOWN })}</summary>
          <ul class="playback-holds">
            {#each holds.slice(SHOWN) as hold, index (index)}{@render playback(hold)}{/each}
          </ul>
        </details>
      </li>
    {/if}
  </ul>
{/if}

<style>
  .playback-holds { margin-top: 0.75rem; display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 18rem), 1fr)); gap: 0.5rem; }
  .playback-card { display: flex; align-items: center; gap: 0.875rem; min-width: 0; padding: 0.75rem; border: 1px solid var(--divide-soft); border-radius: 0.75rem; background: var(--raised); box-shadow: var(--lift-1), inset 0 1px 0 var(--edge); overflow-wrap: anywhere; }
  .playback-copy { min-width: 0; flex: 1; display: flex; flex-direction: column; gap: 0.25rem; }
  .playback-title { color: var(--ink); font-size: 0.875rem; font-weight: 600; line-height: 1.4; }
  .playback-viewer { color: var(--ink-3); font-size: 0.75rem; line-height: 1.5; }
  .playback-meta { display: flex; flex-wrap: wrap; gap: 0.375rem; margin-top: 0.25rem; }
  .playback-chip { display: inline-flex; align-items: center; gap: 0.25rem; padding: 0.125rem 0.5rem; border-radius: 999px; background: var(--panel); color: var(--ink-2); font-size: 0.6875rem; font-weight: 600; }
  .playback-live { color: var(--accent-strong); background: var(--accent-soft); }
  .playback-more { grid-column: 1 / -1; min-width: 0; }
  summary { padding: 0.25rem 0; font-size: 0.8125rem; }
  @media (pointer: coarse) { summary { min-height: 44px; line-height: 36px; } }
</style>
