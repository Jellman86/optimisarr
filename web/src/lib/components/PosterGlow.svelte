<script lang="ts">
  // The title's own poster, blurred into the surface behind it, the way a "now playing" view
  // takes its colour from the artwork. It is ambience for recognition only: it never carries
  // state, it sits behind the content so text is always on the solid surface above it, and a
  // title with no artwork simply leaves the surface plain.
  let { mediaFileId }: { mediaFileId: number } = $props()
  let failed = $state(false)
  let loaded = $state(false)
  let shownMediaFileId: number | undefined

  // Reset only when the title really changes. A refresh hands down a new job object with the
  // same id, and resetting then would re-request artwork that is already known to be missing.
  $effect.pre(() => {
    if (mediaFileId === shownMediaFileId) return
    shownMediaFileId = mediaFileId
    failed = false
    loaded = false
  })
</script>

{#if !failed}
  <div class="poster-glow" aria-hidden="true">
    {#key mediaFileId}
      <img
        src="/api/media/{mediaFileId}/thumbnail"
        alt=""
        loading="lazy"
        class:loaded
        onload={() => (loaded = true)}
        onerror={() => (failed = true)}
      />
    {/key}
  </div>
{/if}

<style>
  .poster-glow {
    position: absolute;
    inset: 0;
    z-index: 0;
    overflow: hidden;
    border-radius: inherit;
    pointer-events: none;
  }
  img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    filter: blur(56px) saturate(1.5);
    transform: scale(1.35);
    opacity: 0;
    transition: opacity 0.4s ease;
  }
  img.loaded {
    opacity: 0.2;
  }
  :global(html.dark) img.loaded {
    opacity: 0.3;
  }
  @media (prefers-reduced-transparency: reduce) {
    .poster-glow {
      display: none;
    }
  }
</style>
