<script lang="ts">
  // The dialog that inspects one item: a header that says which item it is, a body that scrolls
  // on its own, and the item's actions pinned beneath it. It is a native modal dialog, so focus
  // stays inside it, Escape and a click on the backdrop close it, and focus returns to whatever
  // opened it. Closing is always the caller's decision: `onclose` is asked, and the caller
  // removes the dialog.
  import type { Snippet } from 'svelte'
  import { modal } from '../modal'
  import Icon from './Icon.svelte'

  let {
    id = undefined,
    title,
    titleId,
    eyebrow = '',
    eyebrowId = undefined,
    focusableTitle = false,
    closeLabel,
    size = 'default',
    onclose,
    backdrop,
    leading,
    meta,
    actions,
    footer,
    children,
  }: {
    id?: string
    title: string
    /** The title's id; the dialog is named by it (and by the eyebrow, when that has an id too). */
    titleId: string
    /** A short line above the title: where the item lives, or what kind of record it is. */
    eyebrow?: string
    /** Give the eyebrow an id to make it part of the dialog's accessible name. */
    eyebrowId?: string
    /** Let the caller move focus to the title, so a screen reader starts at the item's name. */
    focusableTitle?: boolean
    /** The close button's accessible name. */
    closeLabel: string
    size?: 'default' | 'wide'
    onclose: () => void
    /** Artwork laid behind the header. It is ambience: the header's text never depends on it. */
    backdrop?: Snippet
    /** A poster or icon beside the title. */
    leading?: Snippet
    /** Facts and badges under the title. */
    meta?: Snippet
    /** The item's buttons, in a row pinned to the bottom of the dialog. */
    actions?: Snippet
    /** Notes under the actions, such as why one of them is unavailable. */
    footer?: Snippet
    children: Snippet
  } = $props()

  let labelledby = $derived([eyebrow ? eyebrowId : undefined, titleId].filter(Boolean).join(' '))
</script>

<dialog {id} class="app-modal detail-dialog" class:detail-dialog-wide={size === 'wide'} use:modal={onclose} aria-labelledby={labelledby}>
  <header class="detail-dialog-header">
    {#if backdrop}<div class="detail-dialog-backdrop" aria-hidden="true">{@render backdrop()}</div>{/if}
    {#if leading}<div class="detail-dialog-leading">{@render leading()}</div>{/if}
    <div class="detail-dialog-identity">
      {#if eyebrow}<p id={eyebrowId} class="mb-2 text-caption font-semibold text-ink-2">{eyebrow}</p>{/if}
      {#if focusableTitle}
        <h2 id={titleId} class="detail-dialog-title outline-none" tabindex="-1">{title}</h2>
      {:else}
        <h2 id={titleId} class="detail-dialog-title">{title}</h2>
      {/if}
      {#if meta}<div class="mt-3 flex flex-wrap items-center gap-x-3 gap-y-1.5 text-caption text-ink-2 [overflow-wrap:anywhere]">{@render meta()}</div>{/if}
    </div>
    <button type="button" class="btn btn-lg btn-ghost btn-icon detail-dialog-close" onclick={onclose} aria-label={closeLabel}><Icon name="x" /></button>
  </header>

  <div class="detail-dialog-body">
    {@render children()}
  </div>

  {#if actions || footer}
    <footer class="detail-dialog-footer">
      {#if actions}<div class="detail-actions">{@render actions()}</div>{/if}
      {@render footer?.()}
    </footer>
  {/if}
</dialog>

<style>
  .detail-dialog { width: 48rem; grid-template-rows: auto minmax(0, 1fr) auto; }
  .detail-dialog-wide { width: 52rem; }
  .detail-dialog-header { position: relative; isolation: isolate; overflow: hidden; display: flex; align-items: center; gap: 1.5rem; padding: 1.5rem 2rem; background: var(--panel); box-shadow: inset 0 -1px 0 var(--divide-soft); }
  .detail-dialog-backdrop { position: absolute; inset: 0; z-index: -1; overflow: hidden; pointer-events: none; }
  .detail-dialog-leading { flex-shrink: 0; overflow: hidden; border-radius: .625rem; box-shadow: var(--lift-3); }
  .detail-dialog-leading :global([data-thumbnail]) { width: 5rem; height: 7.5rem; }
  .detail-dialog-leading :global([data-thumbnail][data-shape='square']) { height: 5rem; }
  .detail-dialog-identity { min-width: 0; padding-right: 1.75rem; }
  .detail-dialog-title { font-size: clamp(1.125rem, 3vw, 1.5rem); line-height: 1.3; font-weight: 650; letter-spacing: -.025em; color: var(--ink); overflow-wrap: anywhere; display: -webkit-box; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
  .detail-dialog-close { position: absolute; right: .75rem; top: .75rem; background: var(--panel); }
  .detail-dialog-close:hover { background: linear-gradient(var(--lit), var(--lit)), var(--panel); }
  .detail-dialog-body { min-height: 0; overflow-y: auto; overscroll-behavior: contain; scrollbar-gutter: stable; padding: 1.5rem 2rem; }
  .detail-dialog-footer { padding: 1rem 2rem; background: var(--raised); box-shadow: inset 0 1px 0 var(--divide-soft); }
  .detail-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: .5rem; }
  .detail-actions :global(.btn) { min-height: var(--control-lg); }
  @media (max-width: 639px) {
    .detail-dialog-header { gap: 1rem; padding: 1rem; }
    .detail-dialog-identity { padding-right: 2.75rem; }
    .detail-dialog-leading :global([data-thumbnail]) { width: 3.5rem; height: 5.25rem; }
    .detail-dialog-leading :global([data-thumbnail][data-shape='square']) { height: 3.5rem; }
    .detail-dialog-close { right: .375rem; top: .375rem; }
    .detail-dialog-body, .detail-dialog-footer { padding: 1rem; }
    .detail-actions { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); }
    .detail-actions :global(.btn) { white-space: normal; }
    /* An odd last action takes the whole row rather than half of it. */
    .detail-actions > :global(:last-child:nth-child(odd)) { grid-column: 1 / -1; }
  }
  @media (max-height: 540px) {
    .detail-dialog-header { gap: 1rem; padding: .75rem 1rem; }
    .detail-dialog-identity { padding-right: 2.75rem; }
    .detail-dialog-close { right: .375rem; top: .375rem; }
    .detail-dialog-leading :global([data-thumbnail]) { width: 2.5rem; height: 3.75rem; }
    .detail-dialog-leading :global([data-thumbnail][data-shape='square']) { height: 2.5rem; }
    .detail-dialog-title { -webkit-line-clamp: 1; line-clamp: 1; font-size: 1.125rem; }
    .detail-dialog-body { padding: 1rem; }
    .detail-dialog-footer { padding: .5rem 1rem; }
  }
</style>
