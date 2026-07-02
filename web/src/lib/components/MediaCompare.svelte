<script lang="ts">
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

  // Start playback in the middle of the file: the opening seconds are often
  // black frames/leaders, so the midpoint is a more representative frame to
  // compare original vs encoded at a glance.
  function seekToMiddle(event: Event) {
    const video = event.currentTarget as HTMLVideoElement
    if (Number.isFinite(video.duration) && video.duration > 0) {
      video.currentTime = video.duration / 2
    }
  }
</script>

<div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
  {#each sides as side (side.label)}
    <div>
      <div class="mb-1 flex items-center justify-between text-xs font-medium text-slate-500 dark:text-slate-400">
        <span>{side.label}</span>
        <span class="flex items-center gap-2">
          <span>{side.sizeBytes != null ? formatSize(side.sizeBytes) : ''}</span>
          <a class="btn px-2 py-1 text-xs" href={side.url} download>Download</a>
        </span>
      </div>
      {#if mediaKind === 'Image'}
        <img src={side.url} alt={side.label} class="max-h-72 w-full rounded bg-slate-100 object-contain dark:bg-slate-800" />
      {:else if mediaKind === 'Audio'}
        <audio src={side.url} controls preload="metadata" class="w-full"></audio>
      {:else}
        <video src={side.url} controls preload="metadata" onloadedmetadata={seekToMiddle} class="max-h-72 w-full rounded bg-black"><track kind="captions" /></video>
      {/if}
    </div>
  {/each}
</div>
{#if mediaKind !== 'Image' && mediaKind !== 'Audio'}
  <p class="mt-2 text-xs text-slate-400">Playback uses the original streams. Some browsers cannot play MKV, HEVC, AV1, or E-AC-3; use Download to inspect either exact file locally. The stats and verification below still apply.</p>
{/if}
