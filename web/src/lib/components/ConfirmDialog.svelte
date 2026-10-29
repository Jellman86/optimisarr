<script lang="ts">
  // The confirmation a bulk action asks for before it runs. A batch touches many rows at once,
  // so the operator is told exactly what will happen — and how many rows it will happen to —
  // before anything is sent. The dialog is the app's own, not the browser's, so it can name the
  // count and explain a destructive step in plain words.
  import type { Snippet } from 'svelte'
  import { modal } from '../modal'
  import { i18n } from '../i18n/i18n.svelte'
  import Icon from './Icon.svelte'

  let {
    open = $bindable(false),
    title,
    body,
    confirmLabel,
    danger = false,
    busy = false,
    onconfirm,
    oncancel,
  }: {
    open?: boolean
    title: string
    /** What will happen, in the operator's terms. */
    body: Snippet
    confirmLabel: string
    /** Styles the confirm button as destructive. */
    danger?: boolean
    /** True while the action runs, so the dialog cannot be confirmed twice. */
    busy?: boolean
    onconfirm: () => void
    oncancel: () => void
  } = $props()

  const id = $props.id()
  let cancelButton: HTMLButtonElement | undefined = $state()

  // Dismissing always closes the dialog here, so a caller cannot leave it on screen by only
  // clearing its own state. Escape and a backdrop click land here too, through use:modal.
  function cancel() {
    if (busy) return
    open = false
    oncancel()
  }

  function confirm() {
    if (busy) return
    onconfirm()
  }

  // The safe control takes the focus: a bulk action is confirmed by a deliberate second press,
  // not by the Enter key left over from the click that opened the dialog.
  $effect(() => {
    if (open) queueMicrotask(() => cancelButton?.focus({ preventScroll: true }))
  })
</script>

{#if open}
  <dialog
    class="app-modal confirm-dialog"
    use:modal={cancel}
    aria-labelledby={`${id}-title`}
    aria-describedby={`${id}-body`}
    data-confirm-dialog
  >
    <div class="flex min-w-0 items-start gap-3 border-b border-line px-5 py-4 sm:px-6">
      {#if danger}
        <span class="confirm-signal mt-0.5 flex h-10 w-10 shrink-0 items-center justify-center rounded-xl border border-bad-line bg-bad-soft text-bad">
          <Icon name="warning" class="h-5 w-5" />
        </span>
      {/if}
      <h2 id={`${id}-title`} class="min-w-0 text-lg font-semibold leading-snug text-ink">{title}</h2>
    </div>
    <div id={`${id}-body`} class="min-h-0 overflow-y-auto px-5 py-4 text-sm leading-relaxed text-ink-2 sm:px-6">
      {@render body()}
    </div>
    <div class="flex flex-col gap-2 border-t border-line px-5 py-4 sm:flex-row sm:justify-end sm:px-6">
      <button type="button" class="btn min-h-11 justify-center" bind:this={cancelButton} onclick={cancel} disabled={busy}>
        {i18n.m.common.cancel}
      </button>
      <button
        type="button"
        class="btn {danger ? 'btn-danger' : 'btn-primary'} min-h-11 justify-center"
        onclick={confirm}
        disabled={busy}
        data-confirm-accept
      >
        {busy ? i18n.m.common.loading_short : confirmLabel}
      </button>
    </div>
  </dialog>
{/if}

<style>
  .confirm-dialog { width: 32rem; grid-template-rows: auto minmax(0, 1fr) auto; }
</style>
