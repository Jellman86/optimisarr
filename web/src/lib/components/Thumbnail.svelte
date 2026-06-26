<script lang="ts">
  // A small media thumbnail proxied by the backend, chosen by kind: a poster (Radarr/Sonarr, then a
  // media server) for film/TV, embedded cover art for music, and a down-scaled still for an image.
  // A fixed box so it never shifts layout, with a clean placeholder when nothing resolves — artwork
  // is a recognition aid here, never a state signal, so a missing image is silent.
  let { mediaFileId, alt = '', size = 'sm' }: { mediaFileId: number; alt?: string; size?: 'sm' | 'md' } =
    $props()

  let loaded = $state(false)
  let failed = $state(false)

  const box = $derived(size === 'md' ? 'h-16 w-11' : 'h-12 w-8')
</script>

<div
  class="relative shrink-0 overflow-hidden rounded bg-slate-100 ring-1 ring-slate-200 dark:bg-slate-800 dark:ring-slate-700 {box}"
>
  {#if !failed}
    <img
      src="/api/media/{mediaFileId}/thumbnail"
      {alt}
      loading="lazy"
      class="h-full w-full object-cover transition-opacity duration-200"
      class:opacity-0={!loaded}
      class:opacity-100={loaded}
      onload={() => (loaded = true)}
      onerror={() => (failed = true)}
    />
  {/if}
  {#if failed || !loaded}
    <div class="absolute inset-0 grid place-items-center text-slate-300 dark:text-slate-600">
      <svg class="h-4 w-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true">
        <rect x="3" y="4" width="18" height="16" rx="2" />
        <path d="M3 9h18M8 4v16" />
      </svg>
    </div>
  {/if}
</div>
