<script lang="ts">
  import { i18n } from '../i18n/i18n.svelte'
  // Side-by-side viewers for an original vs an encoded variant, chosen by media kind
  // (image ↔ image, video ↔ video, audio ↔ audio). Streamed from URLs the caller provides;
  // shared by the settings Preview and the Quarantine compare-to-approve panel.
  import { formatSize } from '../format'

  let { mediaKind, left, right, comparisonDurationSeconds = null }: {
    mediaKind: string
    left: { label: string; url: string; sizeBytes?: number | null; startSeconds?: number | null }
    right: { label: string; url: string; sizeBytes?: number | null; startSeconds?: number | null }
    comparisonDurationSeconds?: number | null
  } = $props()

  let sides = $derived([left, right])
  let playable = $derived(mediaKind !== 'Image')

  // Element refs for the two media viewers, so "Play both" can drive them together.
  let players = $state<(HTMLMediaElement | undefined)[]>([])
  let metadataReady = $state([false, false])
  let initialPositionSet = false
  let leader = 0
  const programmaticPlay = new Set<number>()
  const programmaticPause = new Set<number>()
  const programmaticSeek = new Set<number>()
  const programmaticRate = new Set<number>()
  const driftToleranceSeconds = 0.08
  // Reflect the actual element state (the user can also use the native controls),
  // so the toggle never lies about what is playing — §5 truthful UI.
  let anyPlaying = $state(false)

  function refreshPlaying() {
    anyPlaying = players.some((p) => p && !p.paused)
  }

  function startSeconds(index: number) {
    return sides[index]?.startSeconds ?? 0
  }

  function comparisonDuration() {
    if (comparisonDurationSeconds != null && comparisonDurationSeconds > 0) {
      return comparisonDurationSeconds
    }

    const available = players
      .map((player, index) =>
        player && Number.isFinite(player.duration)
          ? Math.max(0, player.duration - startSeconds(index))
          : 0,
      )
      .filter((duration) => duration > 0)
    return available.length > 0 ? Math.min(...available) : 0
  }

  function relativeTime(index: number) {
    return Math.max(0, (players[index]?.currentTime ?? startSeconds(index)) - startSeconds(index))
  }

  function seek(index: number, relativeSeconds: number) {
    const player = players[index]
    if (!player) return
    const duration = comparisonDuration()
    const relative = duration > 0
      ? Math.min(Math.max(0, relativeSeconds), duration)
      : Math.max(0, relativeSeconds)
    const absolute = startSeconds(index) + relative
    if (Math.abs(player.currentTime - absolute) <= 0.01) return
    programmaticSeek.add(index)
    player.currentTime = absolute
  }

  function synchronizeTime(sourceIndex: number) {
    const relative = relativeTime(sourceIndex)
    for (let index = 0; index < players.length; index++) {
      if (index !== sourceIndex) seek(index, relative)
    }
  }

  // Both streams represent the same source-relative window, even though the original URL is the
  // full file and the encoded URL starts at zero. Position them at the same representative frame.
  function mediaReady(index: number) {
    metadataReady[index] = true
    if (initialPositionSet || !metadataReady.every(Boolean)) return
    const duration = comparisonDuration()
    if (duration <= 0) return
    initialPositionSet = true
    const midpoint = duration / 2
    seek(0, midpoint)
    seek(1, midpoint)
  }

  function playPeer(index: number) {
    const player = players[index]
    if (!player || !player.paused) return
    programmaticPlay.add(index)
    void player.play()
      .then(() => queueMicrotask(() => programmaticPlay.delete(index)))
      .catch(() => programmaticPlay.delete(index))
  }

  function pausePeer(index: number) {
    const player = players[index]
    if (!player || player.paused) return
    programmaticPause.add(index)
    player.pause()
    queueMicrotask(() => programmaticPause.delete(index))
  }

  function handlePlay(index: number) {
    refreshPlaying()
    if (programmaticPlay.delete(index)) return
    leader = index
    synchronizeTime(index)
    for (let peer = 0; peer < players.length; peer++) {
      if (peer !== index) playPeer(peer)
    }
  }

  function handlePause(index: number) {
    refreshPlaying()
    if (programmaticPause.delete(index)) return
    leader = index
    for (let peer = 0; peer < players.length; peer++) {
      if (peer !== index) pausePeer(peer)
    }
  }

  function handleSeeking(index: number) {
    if (programmaticSeek.delete(index)) return
    leader = index
    synchronizeTime(index)
  }

  function handleRateChange(index: number) {
    if (programmaticRate.delete(index)) return
    const rate = players[index]?.playbackRate
    if (rate == null) return
    leader = index
    for (let peer = 0; peer < players.length; peer++) {
      const player = players[peer]
      if (peer === index || !player || player.playbackRate === rate) continue
      programmaticRate.add(peer)
      player.playbackRate = rate
    }
  }

  function correctDrift(index: number) {
    if (index !== leader || players[index]?.paused) return
    const sourceRelative = relativeTime(index)
    for (let peer = 0; peer < players.length; peer++) {
      if (peer !== index && Math.abs(relativeTime(peer) - sourceRelative) > driftToleranceSeconds) {
        seek(peer, sourceRelative)
      }
    }
  }

  // Play or pause both viewers at once so the original and encoded output can be
  // compared frame-for-frame. A rejected play() promise (e.g. an unplayable codec
  // in this browser) is swallowed; the per-viewer controls and Download still work.
  function toggleBoth() {
    const shouldPlay = !anyPlaying
    leader = 0
    synchronizeTime(leader)
    for (let index = 0; index < players.length; index++) {
      if (shouldPlay) playPeer(index)
      else pausePeer(index)
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
        <audio
          bind:this={players[i]}
          src={side.url}
          controls
          preload="metadata"
          onloadedmetadata={() => mediaReady(i)}
          onplay={() => handlePlay(i)}
          onpause={() => handlePause(i)}
          onended={refreshPlaying}
          onseeking={() => handleSeeking(i)}
          onratechange={() => handleRateChange(i)}
          ontimeupdate={() => correctDrift(i)}
          class="w-full"
        ></audio>
      {:else}
        <video
          bind:this={players[i]}
          src={side.url}
          controls
          preload="metadata"
          onloadedmetadata={() => mediaReady(i)}
          onplay={() => handlePlay(i)}
          onpause={() => handlePause(i)}
          onended={refreshPlaying}
          onseeking={() => handleSeeking(i)}
          onratechange={() => handleRateChange(i)}
          ontimeupdate={() => correctDrift(i)}
          class="max-h-72 w-full rounded bg-black"
        ><track kind="captions" /></video>
      {/if}
    </div>
  {/each}
</div>
{#if mediaKind !== 'Image' && mediaKind !== 'Audio'}
  <p class="mt-2 text-xs text-slate-400">{i18n.m.shared.playback_note}</p>
{/if}
