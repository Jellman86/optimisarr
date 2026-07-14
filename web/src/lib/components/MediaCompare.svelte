<script lang="ts">
  import { i18n } from '../i18n/i18n.svelte'
  // Side-by-side viewers for an original vs an encoded variant, chosen by media kind
  // (image ↔ image, video ↔ video, audio ↔ audio). Streamed from URLs the caller provides;
  // shared by the settings Preview and the Quarantine compare-to-approve panel.
  import { formatSize } from '../format'

  let { mediaKind, left, right }: {
    mediaKind: string
    left: { label: string; url: string; sizeBytes?: number | null }
    right: { label: string; url: string; sizeBytes?: number | null }
  } = $props()

  let sides = $derived([left, right])
  let playable = $derived(mediaKind !== 'Image')

  // Element refs for the two media viewers, so "Play both" can drive them together.
  let players = $state<(HTMLMediaElement | undefined)[]>([])
  // Reflect the actual element state (the user can also use the native controls),
  // so the toggle never lies about what is playing — §5 truthful UI.
  let anyPlaying = $state(false)

  function refreshPlaying() {
    anyPlaying = players.some((p) => p && !p.paused)
  }

  // Start playback in the middle of the file: the opening seconds are often
  // black frames/leaders, so the midpoint is a more representative frame to
  // compare original vs encoded at a glance.
  function seekToMiddle(event: Event) {
    const video = event.currentTarget as HTMLVideoElement
    if (Number.isFinite(video.duration) && video.duration > 0) {
      video.currentTime = video.duration / 2
    }
  }

  // Play or pause both viewers at once so the original and encoded output can be
  // compared frame-for-frame. A rejected play() promise (e.g. an unplayable codec
  // in this browser) is swallowed; the per-viewer controls and Download still work.
  function toggleBoth() {
    const shouldPlay = !anyPlaying
    for (const p of players) {
      if (!p) continue
      if (shouldPlay) void p.play().catch(() => {})
      else p.pause()
    }
  }
</script>

{#if playable}
  <div class="mb-2 flex justify-end">
    <button type="button" class="btn px-2 py-1 text-xs" onclick={toggleBoth}>
      {anyPlaying ? i18n.m.shared.pause_both : i18n.m.shared.play_both}
    </button>
  </div>
{/if}
<div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
  {#each sides as side, i (side.label)}
    <div>
      <div class="mb-1 flex items-center justify-between text-xs font-medium text-slate-500 dark:text-slate-400">
        <span>{side.label}</span>
        <span class="flex items-center gap-2">
          <span>{side.sizeBytes != null ? formatSize(side.sizeBytes) : ''}</span>
          <a class="btn px-2 py-1 text-xs" href={side.url} download>{i18n.m.shared.download}</a>
        </span>
      </div>
      {#if mediaKind === 'Image'}
        <img src={side.url} alt={side.label} class="max-h-72 w-full rounded bg-slate-100 object-contain dark:bg-slate-800" />
      {:else if mediaKind === 'Audio'}
        <audio bind:this={players[i]} src={side.url} controls preload="metadata" onplay={refreshPlaying} onpause={refreshPlaying} onended={refreshPlaying} class="w-full"></audio>
      {:else}
        <video bind:this={players[i]} src={side.url} controls preload="metadata" onloadedmetadata={seekToMiddle} onplay={refreshPlaying} onpause={refreshPlaying} onended={refreshPlaying} class="max-h-72 w-full rounded bg-black"><track kind="captions" /></video>
      {/if}
    </div>
  {/each}
</div>
{#if mediaKind !== 'Image' && mediaKind !== 'Audio'}
  <p class="mt-2 text-xs text-slate-400">{i18n.m.shared.playback_note}</p>
{/if}
