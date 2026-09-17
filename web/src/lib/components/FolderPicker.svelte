<script lang="ts">
  import { i18n } from '../i18n/i18n.svelte'
  import { api, type BrowseResponse } from '../api'
  import { modal } from '../modal'
  import Icon from './Icon.svelte'

  let { initialPath = '', onSelect, onClose }: {
    initialPath?: string
    onSelect: (path: string) => void
    onClose: () => void
  } = $props()

  let listing = $state<BrowseResponse | null>(null)
  let error = $state<string | null>(null)
  let loading = $state(true)

  $effect(() => {
    void navigate(initialPath || undefined)
  })

  async function navigate(path?: string) {
    loading = true
    error = null
    try {
      listing = await api.browse(path)
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.shared.browse_failed
    } finally {
      loading = false
    }
  }
</script>

<dialog class="app-modal folder-dialog" use:modal={onClose} aria-label={i18n.m.shared.choose_folder}>
  <div class="flex items-center justify-between border-b border-line p-4">
    <h2 class="font-semibold text-ink">{i18n.m.shared.choose_folder}</h2>
    <button class="btn btn-ghost min-h-11 min-w-11 px-2" onclick={onClose} aria-label={i18n.m.shared.close}>
      <Icon name="x" class="h-5 w-5" />
    </button>
  </div>

  <div class="border-b border-line p-3">
    <div class="flex items-center gap-2">
      <button
        class="btn min-h-11 px-3 text-xs"
        disabled={loading || !listing?.parent}
        onclick={() => listing?.parent && navigate(listing.parent)}
        title={i18n.m.shared.up_one_level}
      >
        <Icon name="arrow-left" class="h-4 w-4 rotate-90" />
        {i18n.m.shared.up_one_level}
      </button>
      <code class="flex-1 truncate rounded bg-raised px-2 py-1 text-xs text-ink-2">
        {listing?.path ?? '…'}
      </code>
    </div>
  </div>

  <div class="min-h-0 overflow-y-auto p-2">
    {#if loading}
      <p class="p-4 text-center text-sm text-ink-3">{i18n.m.common.loading_short}</p>
    {:else if error}
      <p role="alert" class="p-4 text-center text-sm text-bad">{error}</p>
    {:else if listing && listing.directories.length > 0}
      {#each listing.directories as dir}
        <button
          class="flex min-h-11 w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-sm text-ink-2 hover:bg-raised"
          onclick={() => navigate(dir.path)}
        >
          <svg class="h-4 w-4 flex-shrink-0 text-warn" fill="currentColor" viewBox="0 0 20 20">
            <path d="M2 6a2 2 0 012-2h4l2 2h6a2 2 0 012 2v6a2 2 0 01-2 2H4a2 2 0 01-2-2V6z" />
          </svg>
          <span class="truncate">{dir.name}</span>
        </button>
      {/each}
    {:else}
      <p class="p-4 text-center text-sm text-ink-3">{i18n.m.shared.no_subfolders}</p>
    {/if}
  </div>

  <div class="flex flex-wrap items-center justify-between gap-2 border-t border-line p-3">
    <span class="truncate text-xs text-ink-3">{i18n.m.shared.select_highlighted}</span>
    <div class="flex gap-2">
      <button class="btn min-h-11" onclick={onClose}>{i18n.m.common.cancel}</button>
      <button class="btn btn-primary min-h-11" disabled={loading || !!error || !listing} onclick={() => listing && onSelect(listing.path)}>
        {i18n.m.shared.select_folder}
      </button>
    </div>
  </div>
</dialog>

<style>
  .folder-dialog {
    width: 32rem;
    max-height: min(80dvh, calc(100dvh - 2rem));
    grid-template-rows: auto auto minmax(0, 1fr) auto;
  }
</style>
