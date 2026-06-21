<script lang="ts">
  // A small "?"-style info icon that reveals help on hover or keyboard focus, so dense
  // operational screens can keep their explanations without a wall of always-on text.
  // Backed by a real button for keyboard/screen-reader access; the bubble is presentational.
  import Icon from './Icon.svelte'

  let {
    text,
    label = 'More information',
    class: cls = '',
  }: { text: string; label?: string; class?: string } = $props()
</script>

<span class="group relative inline-flex align-middle {cls}">
  <button
    type="button"
    class="inline-flex text-slate-400 transition-colors hover:text-slate-600 focus-visible:text-cyan-600 focus-visible:outline-none dark:text-slate-500 dark:hover:text-slate-300"
    aria-label={label}
    onclick={(e) => {
      // Never let the icon toggle a surrounding label/row or submit a form.
      e.preventDefault()
      e.stopPropagation()
    }}
  >
    <Icon name="info" class="h-3.5 w-3.5" />
  </button>
  <span
    role="tooltip"
    class="pointer-events-none absolute bottom-full left-1/2 z-50 mb-2 w-72 max-w-[80vw] -translate-x-1/2 rounded-md bg-slate-800 px-3 py-2 text-left text-xs font-normal normal-case leading-relaxed tracking-normal text-slate-100 opacity-0 shadow-lg transition-opacity duration-150 group-hover:opacity-100 group-focus-within:opacity-100 dark:bg-slate-700"
  >
    {text}
  </span>
</span>
