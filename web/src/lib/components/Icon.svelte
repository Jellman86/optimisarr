<script lang="ts" module>
  // A small inline-SVG icon set drawn in a 24×24 viewbox with currentColor strokes,
  // so icons inherit text colour and size from their button or label.
  export const icons: Record<string, string> = {
    check: 'M5 13l4 4L19 7',
    x: 'M6 6l12 12M18 6L6 18',
    retry: 'M4 4v6h6M20 20v-6h-6M5.5 9a7 7 0 0111.9-2.6L20 9M18.5 15a7 7 0 01-11.9 2.6L4 15',
    replace: 'M7 4l-3 3 3 3M4 7h11M17 20l3-3-3-3M20 17H9',
    warning: 'M12 9v4m0 4h.01M10.3 3.9 1.8 18a2 2 0 001.7 3h17a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z',
    play: 'M6 4l13 8-13 8z',
    pause: 'M8 5v14M16 5v14',
    search: 'M21 21l-4.3-4.3M11 18a7 7 0 100-14 7 7 0 000 14z',
    plus: 'M12 5v14M5 12h14',
    download: 'M12 3v12m0 0l-4-4m4 4l4-4M4 21h16',
    upload: 'M12 21V9m0 0l-4 4m4-4l4 4M4 3h16',
    folder: 'M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v8a2 2 0 01-2 2H5a2 2 0 01-2-2z',
    clock: 'M12 7v5l3 2M21 12a9 9 0 11-18 0 9 9 0 0118 0z',
    film: 'M4 4h16v16H4zM4 8h16M4 16h16M8 4v16M16 4v16',
    rotate: 'M4 4v6h6M5.5 9a7 7 0 0111.9-2.6L20 9',
    trash: 'M4 7h16M9 7V5a1 1 0 011-1h4a1 1 0 011 1v2m-8 0v12a2 2 0 002 2h4a2 2 0 002-2V7',
    info: 'M12 8h.01M11 12h1v4h1M21 12a9 9 0 11-18 0 9 9 0 0118 0z',
  }
</script>

<script lang="ts">
  let { name, class: className = 'h-4 w-4' }: { name: keyof typeof icons | string; class?: string } = $props()
</script>

<svg class={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2" aria-hidden="true">
  <path stroke-linecap="round" stroke-linejoin="round" d={icons[name] ?? ''} />
</svg>
