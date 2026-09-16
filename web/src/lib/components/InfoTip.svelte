<script lang="ts">
  // A small information icon that reveals associated help on hover, keyboard focus, or tap, so
  // dense operational screens can keep their explanations without a wall of always-on text.
  // The real button supplies the accessible name, relationship, and touch target.
  import Icon from './Icon.svelte'
  import { i18n } from '../i18n/i18n.svelte'

  let {
    text,
    label,
    class: cls = '',
  }: { text: string; label?: string; class?: string } = $props()

  const tooltipId = $props.id()
  const accessibleLabel = $derived(label ?? i18n.m.common.more_information)
</script>

<!-- Negative margin preserves the compact visual rhythm while the real button supplies a
     44 × 44 px pointer target around the deliberately small information glyph. -->
<span class="group relative -m-[15px] inline-flex align-middle {cls}">
  <button
    type="button"
    class="inline-flex h-11 w-11 items-center justify-center text-ink-4 transition-colors hover:text-ink-2 focus-visible:text-accent focus-visible:outline-none"
    aria-label={accessibleLabel}
    aria-describedby={tooltipId}
    onclick={(e) => {
      // Never let the icon toggle a surrounding label/row or submit a form.
      e.preventDefault()
      e.stopPropagation()
      // Mobile Safari does not consistently focus a button after a tap. Explicit focus keeps the
      // same focus-within disclosure contract reliable for touch without a second open-state path.
      e.currentTarget.focus()
    }}
    onkeydown={(e) => {
      if (e.key === 'Escape') e.currentTarget.blur()
    }}
  >
    <Icon name="info" class="h-3.5 w-3.5" />
  </button>
  <span
    id={tooltipId}
    role="tooltip"
    class="tooltip pointer-events-none fixed inset-x-4 bottom-4 z-50 max-h-[calc(100dvh-2rem)] w-auto max-w-none overflow-y-auto overscroll-contain break-words opacity-0 transition-opacity duration-150 group-hover:pointer-events-auto group-hover:opacity-100 group-focus-within:pointer-events-auto group-focus-within:opacity-100 sm:inset-x-auto sm:right-4 sm:w-80"
  >
    {text}
  </span>
</span>
