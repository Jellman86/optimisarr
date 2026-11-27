<script lang="ts">
  // The top of every page: where you are (an optional trail back up), what the page is called,
  // one quiet sentence about it, and the page's own actions at the far edge. The title is the
  // page's one <h1>; it takes focus when the app moves to a new page, so it is focusable by
  // script but never a tab stop.
  import type { Snippet } from 'svelte'
  import type { HTMLAttributes } from 'svelte/elements'

  let {
    title,
    subtitle = '',
    headingEl = $bindable(),
    headingProps = {},
    class: className = '',
    actionsAlign = 'start',
    eyebrow,
    description,
    actions,
  }: {
    title: string
    /** One plain sentence under the title. Use `description` instead when it needs markup. */
    subtitle?: string
    /** The rendered <h1>, for a page that moves focus to its own heading. */
    headingEl?: HTMLHeadingElement
    /** Extra attributes for the <h1>, such as a page-specific data hook. */
    headingProps?: Omit<HTMLAttributes<HTMLHeadingElement>, 'class' | 'tabindex'>
    class?: string
    /** Where the actions sit against the title: level with it, or with the last line of text. */
    actionsAlign?: 'start' | 'end'
    /** A breadcrumb or back link above the title, for a page inside a section. */
    eyebrow?: Snippet
    /** Rich text under the title, rendered as given (bring your own `.page-subtitle`). */
    description?: Snippet
    /** The page's own actions, beside the title and wrapping beneath it on a narrow screen. */
    actions?: Snippet
  } = $props()
</script>

<header class="page-header {className}">
  {#if eyebrow}<div class="page-header-eyebrow">{@render eyebrow()}</div>{/if}
  <div class="page-header-row" class:page-header-row-end={actionsAlign === 'end'}>
    <div class="page-header-text">
      <h1 {...headingProps} class="page-title break-words outline-none" tabindex="-1" data-page-heading bind:this={headingEl}>{title}</h1>
      {#if subtitle}<p class="page-subtitle">{subtitle}</p>{/if}
      {@render description?.()}
    </div>
    {#if actions}<div class="page-header-actions">{@render actions()}</div>{/if}
  </div>
</header>
