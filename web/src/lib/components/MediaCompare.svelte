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
</script>

<div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
  {#each sides as side (side.label)}
    <div>
      <div class="mb-1 flex items-center justify-between text-xs font-medium text-slate-500 dark:text-slate-400">
        <span>{side.label}</span>
        <span>{side.sizeBytes != null ? formatSize(side.sizeBytes) : ''}</span>
      </div>
      {#if mediaKind === 'Image'}
        <img src={side.url} alt={side.label} class="max-h-72 w-full rounded bg-slate-100 object-contain dark:bg-slate-800" />
      {:else if mediaKind === 'Audio'}
        <audio src={side.url} controls preload="metadata" class="w-full"></audio>
      {:else}
        <video src={side.url} controls preload="metadata" class="max-h-72 w-full rounded bg-black"><track kind="captions" /></video>
      {/if}
    </div>
  {/each}
</div>
{#if mediaKind !== 'Image' && mediaKind !== 'Audio'}
  <p class="mt-2 text-xs text-slate-400">Some codecs (HEVC, AV1) may not play in every browser; the stats and verification below still apply.</p>
{/if}
