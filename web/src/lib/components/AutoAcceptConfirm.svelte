<script lang="ts">
  // Replacing automatically removes the one moment a person looks at a result before an
  // original moves, so it is never switched on by a single click: the risk is read and ticked.
  import { i18n } from '../i18n/i18n.svelte'
  import { modal } from '../modal'
  import Icon from './Icon.svelte'

  let { libraryName, onaccept, oncancel }: {
    libraryName: string
    onaccept: () => void
    oncancel: () => void
  } = $props()
  const id = $props.id()
  let acknowledged = $state(false)

  function focusCancel(button: HTMLButtonElement) {
    queueMicrotask(() => { if (button.isConnected) button.focus({ preventScroll: true }) })
  }
</script>

<dialog class="app-modal auto-accept-dialog" use:modal={oncancel} aria-labelledby={`${id}-title`} aria-describedby={`${id}-risk ${id}-quarantine`}>
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
    <button type="button" class="btn min-h-11 justify-center" use:focusCancel onclick={oncancel}>{i18n.m.autoAccept.cancel}</button>
    <button type="button" class="btn btn-danger min-h-11 justify-center" disabled={!acknowledged} onclick={onaccept}>{i18n.m.autoAccept.enable}</button>
  </div>
</dialog>

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
