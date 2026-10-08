<script lang="ts">
  import type { PlaybackHold } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { playbackLine, playbackPhrases } from '../playback'
  import Icon from './Icon.svelte'

  // What is playing, who is watching and where, beside a pause that playback caused. Renders
  // nothing when no playback is named, so the pause message stands alone as it did before.
  let { holds }: { holds: PlaybackHold[] } = $props()
  let phrases = $derived(playbackPhrases(i18n.m))
  // Keep a busy server compact while letting touch and keyboard users reach every stream.
  const SHOWN = 8
  let shown = $derived(holds.slice(0, SHOWN))
</script>

{#if holds.length > 0}
  <ul class="playback-holds" aria-label={i18n.m.queue.playback_heading}>
    {#each shown as hold, index (index)}
      <li>
        <Icon name={hold.paused ? 'pause' : 'play'} class="h-3.5 w-3.5 flex-none" />
        <span><span class="font-semibold">{hold.watcher}</span> · {playbackLine(hold, phrases)}</span>
      </li>
    {/each}
    {#if holds.length > SHOWN}
      <li>
        <details>
          <summary class="focus-ring cursor-pointer text-ink-3">{t(i18n.m.queue.playback_more, { count: holds.length - SHOWN })}</summary>
          <ul class="playback-holds">
            {#each holds.slice(SHOWN) as hold, index (index)}
              <li>
                <Icon name={hold.paused ? 'pause' : 'play'} class="h-3.5 w-3.5 flex-none" />
                <span><span class="font-semibold">{hold.watcher}</span> · {playbackLine(hold, phrases)}</span>
              </li>
            {/each}
          </ul>
        </details>
      </li>
    {/if}
  </ul>
{/if}

<style>
  .playback-holds {
    margin-top: 0.5rem;
    display: grid;
    gap: 0.25rem;
  }
  @media (pointer: coarse) {
    summary { min-height: 44px; line-height: 44px; }
  }
  .playback-holds li {
    display: flex;
    align-items: baseline;
    gap: 0.5rem;
    overflow-wrap: anywhere;
  }
</style>
