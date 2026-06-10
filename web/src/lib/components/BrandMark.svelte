<script lang="ts">
  // The application mark. A single <img> with a srcset across the packaged icon
  // sizes lets the browser pick the sharpest source for the rendered box and the
  // display's pixel density, so the logo stays crisp whether it's the 40px
  // collapsed badge or the 64px expanded header — and on hi-DPI screens.
  let {
    alt = 'Optimisarr',
    class: className = 'h-9 w-9',
    sizes = '64px',
  }: { alt?: string; class?: string; sizes?: string } = $props()

  const srcset = [
    '/favicon.png 32w',
    '/apple-touch-icon.png 180w',
    '/favicon-192.png 192w',
  ].join(', ')
</script>

<img
  src="/favicon-192.png"
  {srcset}
  {sizes}
  {alt}
  decoding="async"
  class="object-contain {className}"
/>
