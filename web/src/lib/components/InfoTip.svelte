<script lang="ts">
  // A small "?"-style info icon that reveals help on hover or keyboard focus, so dense
  // operational screens can keep their explanations without a wall of always-on text.
  // Backed by a real button for keyboard/screen-reader access; the bubble is presentational.
  import Icon from './Icon.svelte'
  import { i18n } from '../i18n/i18n.svelte'

  let {
    text,
    label,
    class: cls = '',
  }: { text: string; label?: string; class?: string } = $props()

  const accessibleLabel = $derived(label ?? i18n.m.common.more_information)
</script>

<!-- Negative margin preserves the compact visual rhythm while the real button supplies a
     44 × 44 px pointer target around the deliberately small information glyph. -->
<span class="group relative -m-[15px] inline-flex align-middle {cls}">
  <button
    type="button"
    class="inline-flex h-11 w-11 items-center justify-center text-slate-400 transition-colors hover:text-slate-600 focus-visible:text-cyan-600 focus-visible:outline-none dark:text-slate-500 dark:hover:text-slate-300"
    aria-label={accessibleLabel}
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
    class="pointer-events-none fixed inset-x-4 bottom-4 z-50 w-auto max-w-none rounded-md bg-slate-800 px-3 py-2 text-left text-xs font-normal normal-case leading-relaxed tracking-normal text-slate-100 opacity-0 shadow-lg transition-opacity duration-150 group-hover:opacity-100 group-focus-within:opacity-100 dark:bg-slate-700 sm:inset-x-auto sm:right-4 sm:w-72"
  >
    {text}
  </span>
</span>
