<script lang="ts">
  // A small information icon that reveals associated help on hover, keyboard focus, or tap, so
  // dense operational screens can keep their explanations without a wall of always-on text.
  // The real button supplies the accessible name, relationship, and touch target.
  import Icon from './Icon.svelte'
  import { i18n } from '../i18n/i18n.svelte'

  let {
    text,
    label,
    id,
    class: cls = '',
  }: {
    text: string
    label?: string
    /** Lets the control this explains point `aria-describedby` at the same words. */
    id?: string
    class?: string
  } = $props()

  const generatedId = $props.id()
  const tooltipId = $derived(id ?? generatedId)
  const accessibleLabel = $derived(label ?? i18n.m.common.more_information)

  let wrapper: HTMLSpanElement
  let tip: HTMLSpanElement

  // Below this width the help is a sheet along the bottom of the screen (the classes below); at
  // and above it, the help hangs beside its own button.
  const besideButton = '(min-width: 640px)'
  const viewportMargin = 16
  const glyphGap = 8
  const glyphRadius = 7

  // The tip is fixed-positioned so no scrolling panel or dialog can clip it, which means its
  // place is measured from the button rather than inherited from the layout.
  function place() {
    const style = tip.style
    style.removeProperty('top')
    style.removeProperty('left')
    style.removeProperty('right')
    style.removeProperty('bottom')
    style.removeProperty('max-height')
    if (!window.matchMedia(besideButton).matches) return

    const button = wrapper.querySelector('button')!.getBoundingClientRect()
    const centreX = button.left + button.width / 2
    const centreY = button.top + button.height / 2
    const viewportWidth = document.documentElement.clientWidth
    const viewportHeight = window.innerHeight
    const width = tip.offsetWidth
    const height = tip.scrollHeight
    const below = centreY + glyphRadius + glyphGap
    const above = centreY - glyphRadius - glyphGap
    const roomBelow = viewportHeight - viewportMargin - below
    const roomAbove = above - viewportMargin
    const fitsBelow = height <= roomBelow || roomBelow >= roomAbove
    const room = Math.max(0, fitsBelow ? roomBelow : roomAbove)
    // Start at the glyph and read rightwards; near the right edge, end at the glyph instead.
    const rightwards = centreX - glyphRadius * 2
    const leftwards = centreX + glyphRadius * 2 - width
    const fitsRightwards = rightwards + width <= viewportWidth - viewportMargin
    const left = Math.max(viewportMargin, fitsRightwards ? rightwards : leftwards)
    const top = fitsBelow ? below : above - Math.min(height, room)

    style.right = 'auto'
    style.bottom = 'auto'
    style.maxHeight = `${room}px`
    style.left = `${left}px`
    style.top = `${top}px`
    // A transformed or filtered ancestor moves where "fixed" starts counting from; correct for it.
    const drawn = tip.getBoundingClientRect()
    style.left = `${left - (drawn.left - left)}px`
    style.top = `${top - (drawn.top - top)}px`
  }

  function placeWhileOpen() {
    if (wrapper.matches(':hover, :focus-within')) place()
  }

  $effect(() => {
    const options = { capture: true, passive: true }
    window.addEventListener('scroll', placeWhileOpen, options)
    window.addEventListener('resize', placeWhileOpen, options)
    return () => {
      window.removeEventListener('scroll', placeWhileOpen, options)
      window.removeEventListener('resize', placeWhileOpen, options)
    }
  })
</script>

<!-- Negative margin preserves the compact visual rhythm while the real button supplies a
     44 × 44 px pointer target around the deliberately small information glyph. -->
<span bind:this={wrapper} class="group relative -m-[15px] inline-flex align-middle {cls}">
  <button
    type="button"
    class="inline-flex h-11 w-11 items-center justify-center rounded-full text-ink-4 transition-colors hover:text-ink-2 focus-visible:text-accent focus-ring-inset"
    aria-label={accessibleLabel}
    aria-describedby={tooltipId}
    onpointerenter={place}
    onfocus={place}
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
    bind:this={tip}
    id={tooltipId}
    role="tooltip"
    class="tooltip pointer-events-none fixed inset-x-4 bottom-safe-4 z-50 max-h-[calc(100dvh_-_2rem_-_env(safe-area-inset-bottom,0px))] w-auto max-w-none overflow-y-auto overscroll-contain break-words opacity-0 transition-opacity group-hover:pointer-events-auto group-hover:opacity-100 group-focus-within:pointer-events-auto group-focus-within:opacity-100 sm:inset-x-auto sm:right-4 sm:w-max sm:max-w-80"
  >
    {text}
  </span>
</span>
