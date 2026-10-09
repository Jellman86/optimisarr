<script lang="ts">
  // A small media thumbnail proxied by the backend, chosen by kind: a poster (Radarr/Sonarr, then a
  // media server) for film/TV, embedded cover art for music, and a down-scaled still for an image.
  // A fixed box so it never shifts layout, with a clean placeholder when nothing resolves — artwork
  // is a recognition aid here, never a state signal, so a missing image is silent.
  let { mediaFileId, src, alt = '', size = 'sm', shape = 'portrait' }: { mediaFileId?: number; src?: string | null; alt?: string; size?: 'sm' | 'md' | 'lg' | 'poster'; shape?: 'portrait' | 'square' } =
    $props()

  let loaded = $state(false)
  let failed = $state(false)
  let displayedSource: string | null | undefined
  let source = $derived(src !== undefined ? src : mediaFileId != null ? `/api/media/${mediaFileId}/thumbnail` : null)

  // Reset before the keyed image mounts: a cached image can load before a
  // post-render effect and otherwise have its successful load state erased.
  // Parent object replacement can invalidate the prop even for the same ID;
  // that must not hide a loaded image or retry one that already failed.
  $effect.pre(() => {
    if (source === displayedSource) return
    displayedSource = source
    loaded = false
    failed = false
  })

  // Artwork corners scale with the artwork, as album and poster art does in the Apple apps.
  const corner = $derived(size === 'poster' || size === 'lg' ? 'rounded-[10px]' : 'rounded-[5px]')
  const box = $derived(size === 'poster' ? (shape === 'square' ? 'h-32 w-32' : 'h-48 w-32') : size === 'lg' ? (shape === 'square' ? 'h-24 w-24' : 'h-36 w-24') : size === 'md' ? (shape === 'square' ? 'h-16 w-16' : 'h-16 w-11') : (shape === 'square' ? 'h-8 w-8' : 'h-12 w-8'))
</script>

{#key source}
<div
  data-thumbnail data-shape={shape}
  class="relative shrink-0 overflow-hidden bg-raised shadow-[0_1px_3px_rgba(0,0,0,0.18)] ring-1 ring-line {corner} {box}"
>
  {#if source && !failed}
    <img
      src={source}
      {alt}
      loading="lazy"
      class="h-full w-full object-cover transition-opacity duration-200"
      class:opacity-0={!loaded}
      class:opacity-100={loaded}
      onload={() => (loaded = true)}
      onerror={() => (failed = true)}
    />
  {/if}
  {#if !source || failed || !loaded}
    <div class="absolute inset-0 grid place-items-center text-ink-5">
      <svg class="h-4 w-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true">
        <rect x="3" y="4" width="18" height="16" rx="2" />
        <path d="M3 9h18M8 4v16" />
      </svg>
    </div>
  {/if}
</div>

{/key}
