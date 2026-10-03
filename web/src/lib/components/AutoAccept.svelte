<script lang="ts">
  import { i18n } from '../i18n/i18n.svelte'
  import { modal } from '../modal'
  import Icon from './Icon.svelte'

  let { checked = $bindable(false), libraryName }: {
    checked?: boolean
    libraryName: string
  } = $props()
  const id = $props.id()
  let confirming = $state(false)
  let acknowledged = $state(false)

  function requestChange(event: Event) {
    const input = event.currentTarget as HTMLInputElement
    const requested = input.checked
    input.checked = checked
    if (requested && !checked) {
      // Safari does not always focus a clicked checkbox. Give the dialog an explicit
      // opener so cancelling returns keyboard users to this control on every browser.
      input.focus({ preventScroll: true })
      acknowledged = false
      confirming = true
    } else if (!requested) checked = false
  }

  function dismiss() {
    confirming = false
    acknowledged = false
  }

  function accept() {
    if (!acknowledged) return
    checked = true
    dismiss()
  }

  function focusCancel(button: HTMLButtonElement) {
    queueMicrotask(() => { if (button.isConnected) button.focus({ preventScroll: true }) })
  }
</script>

<section class="rounded-xl border border-line p-4" data-auto-accept-control>
  <label class="flex min-h-11 cursor-pointer items-center justify-between gap-4">
    <span class="min-w-0">
      <span class="block text-sm font-semibold text-ink">{i18n.m.libraries.auto_replace_label}</span>
      <span class="mt-1 block text-xs text-ink-3">{i18n.m.autoAccept.offByDefault}</span>
    </span>
    <span class="relative inline-flex h-6 w-11 shrink-0 items-center">
      <input type="checkbox" class="peer absolute inset-0 z-10 h-full w-full cursor-pointer opacity-0" aria-label={i18n.m.libraries.auto_replace_label} aria-describedby={`${id}-hint ${id}-warning`} {checked} onchange={requestChange} />
      <span class="switch-track"></span>
      <span class="switch-knob"></span>
    </span>
  </label>
  <p id={`${id}-hint`} class="mt-2 text-sm leading-relaxed text-ink-3">{i18n.m.libraries.auto_replace_hint}</p>
  <div class="mt-3 flex items-start gap-2 rounded-xl border border-bad-line bg-bad-soft p-3">
    <Icon name="warning" class="mt-0.5 h-5 w-5 shrink-0 text-bad" />
    <p id={`${id}-warning`} class="min-w-0 text-sm leading-relaxed text-ink-2">{i18n.m.autoAccept.warning}</p>
  </div>
</section>

{#if confirming}
  <dialog class="app-modal auto-accept-dialog" use:modal={dismiss} aria-labelledby={`${id}-title`} aria-describedby={`${id}-risk ${id}-quarantine`}>
    <div class="flex min-w-0 items-start gap-4 border-b border-line px-5 py-5 sm:px-6">
      <span class="warning-signal mt-0.5 flex h-11 w-11 shrink-0 items-center justify-center rounded-xl border border-bad-line bg-bad-soft text-bad" data-auto-accept-warning>
        <Icon name="warning" class="h-6 w-6" />
      </span>
      <div class="min-w-0">
        {#if libraryName.trim()}<p class="mb-1 break-words text-xs font-medium text-ink-3">{libraryName}</p>{/if}
        <h2 id={`${id}-title`} class="text-xl font-semibold leading-snug text-ink">{i18n.m.autoAccept.confirmTitle}</h2>
      </div>
    </div>
    <div class="min-h-0 overflow-y-auto px-5 py-5 sm:px-6">
      <p class="text-sm leading-relaxed text-ink-2">{i18n.m.autoAccept.confirmIntro}</p>
      <p id={`${id}-risk`} class="mt-4 rounded-xl border border-bad-line bg-bad-soft p-4 text-sm leading-relaxed text-ink-2">{i18n.m.autoAccept.warning}</p>
      <p id={`${id}-quarantine`} class="mt-4 text-sm leading-relaxed text-ink-3">{i18n.m.autoAccept.quarantine}</p>
      <label class="mt-4 flex min-h-11 cursor-pointer items-start gap-3 rounded-xl border border-line p-3 text-sm leading-relaxed text-ink">
        <input type="checkbox" class="checkbox mt-1 shrink-0" bind:checked={acknowledged} />
        <span>{i18n.m.autoAccept.acknowledge}</span>
      </label>
      <p class="mt-3 text-xs leading-relaxed text-ink-3">{i18n.m.autoAccept.saveNotice}</p>
    </div>
    <div class="flex flex-col gap-2 border-t border-line px-5 py-4 sm:flex-row sm:justify-end sm:px-6">
      <button type="button" class="btn min-h-11 justify-center" use:focusCancel onclick={dismiss}>{i18n.m.autoAccept.cancel}</button>
      <button type="button" class="btn btn-danger min-h-11 justify-center" disabled={!acknowledged} onclick={accept}>{i18n.m.autoAccept.enable}</button>
    </div>
  </dialog>
{/if}

<style>
  .auto-accept-dialog {
    width: 34rem;
    grid-template-rows: auto minmax(0, 1fr) auto;
  }
  .warning-signal { animation: caution-pulse 2.8s ease-in-out 3; }
  @keyframes caution-pulse {
    0%, 100% { box-shadow: 0 0 0 0 transparent; }
    50% { box-shadow: 0 0 0 0.4rem var(--bad-soft); border-color: var(--bad); }
  }
  @media (prefers-reduced-motion: reduce) {
    .warning-signal { animation: none; }
  }
</style>
