<script lang="ts" module>
  // A small inline-SVG icon set drawn in a 24×24 viewbox with currentColor strokes,
  // so icons inherit text colour and size from their button or label.
  export const icons: Record<string, string> = {
    sun: 'M12 3v1m0 16v1m9-9h-1M4 12H3m15.4 6.4l-.7-.7M6.3 6.3l-.7-.7m12.7 0l-.7.7M6.3 17.7l-.7.7M16 12a4 4 0 11-8 0 4 4 0 018 0z',
    moon: 'M20.4 15.4A9 9 0 018.6 3.6 9 9 0 1020.4 15.4z',
    check: 'M5 13l4 4L19 7',
    'arrow-up-right': 'M7 17 17 7M7 7h10v10',
    'shield-check': 'M12 3 3 7v5c0 5 9 9 9 9s9-4 9-9V7zM8 12l3 3 5-6',
    tv: 'M4 7h16v13H4zM8 3l4 4 4-4',
    bell: 'M18 8a6 6 0 00-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4',
    server: 'M3 3h18v7H3zM3 14h18v7H3zM7 6.5h.01M7 17.5h.01M11 6.5h6M11 17.5h6',
    x: 'M6 6l12 12M18 6L6 18',
    retry: 'M4 4v6h6M20 20v-6h-6M5.5 9a7 7 0 0111.9-2.6L20 9M18.5 15a7 7 0 01-11.9 2.6L4 15',
    replace: 'M7 4l-3 3 3 3M4 7h11M17 20l3-3-3-3M20 17H9',
    warning: 'M12 9v4m0 4h.01M10.3 3.9 1.8 18a2 2 0 001.7 3h17a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z',
    play: 'M6 4l13 8-13 8z',
    pause: 'M8 5v14M16 5v14',
    search: 'M21 21l-4.3-4.3M11 18a7 7 0 100-14 7 7 0 000 14z',
    plus: 'M12 5v14M5 12h14',
    'arrow-right': 'M5 12h14m0 0l-6-6m6 6l-6 6',
    'arrow-left': 'M19 12H5m0 0l6-6m-6 6l6 6',
    minus: 'M5 12h14',
    download: 'M12 3v12m0 0l-4-4m4 4l4-4M4 21h16',
    upload: 'M12 21V9m0 0l-4 4m4-4l4 4M4 3h16',
    folder: 'M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v8a2 2 0 01-2 2H5a2 2 0 01-2-2z',
    clock: 'M12 7v5l3 2M21 12a9 9 0 11-18 0 9 9 0 0118 0z',
    film: 'M4 4h16v16H4zM4 8h16M4 16h16M8 4v16M16 4v16',
    rotate: 'M20 4v6h-6M18.5 9a7 7 0 00-11.9-2.6L4 9',
    trash: 'M4 7h16M9 7V5a1 1 0 011-1h4a1 1 0 011 1v2m-8 0v12a2 2 0 002 2h4a2 2 0 002-2V7',
    info: 'M12 8h.01M11 12h1v4h1M21 12a9 9 0 11-18 0 9 9 0 0118 0z',
    chevron: 'M6 9l6 6 6-6',
    sliders: 'M4 6h11M4 12h7M4 18h13M17 4v4M11 10v4M19 16v4',
    more: 'M12 6a1 1 0 100-2 1 1 0 000 2zM12 13a1 1 0 100-2 1 1 0 000 2zM12 20a1 1 0 100-2 1 1 0 000 2z',
    // A GPU/IC chip: outline, inner core, and pins on every side.
    gpu: 'M7 7h10v10H7z M10 10h4v4h-4z M9 4v3M15 4v3M9 17v3M15 17v3M4 9h3M4 15h3M17 9h3M17 15h3',
    // A snail: ground line, outer shell arc, inner spiral, and two antennae.
    snail: 'M3 17h9a5 5 0 10-5-5 3 3 0 103 3M14 9l2-3M17 8l2-2',
    // A no-entry sign: a circle struck through with a diagonal — "never do this".
    ban: 'M5.64 5.64l12.72 12.72M21 12a9 9 0 11-18 0 9 9 0 0118 0z',
  }
</script>

<script lang="ts">
  let { name, class: className = 'h-4 w-4' }: { name: keyof typeof icons | string; class?: string } = $props()
</script>

<svg class={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2" aria-hidden="true">
  <path stroke-linecap="round" stroke-linejoin="round" d={icons[name] ?? ''} />
</svg>
