<script lang="ts">
  import { i18n } from '../i18n/i18n.svelte'
  import { api, type BrowseResponse } from '../api'
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

<!-- Backdrop -->
<div
  class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
  role="button"
  tabindex="0"
  onclick={onClose}
  onkeydown={(e) => e.key === 'Escape' && onClose()}
>
  <!-- Dialog (stop propagation so clicks inside don't close it) -->
  <div
    class="card flex max-h-[80vh] w-full max-w-lg flex-col p-0"
    role="dialog"
    tabindex="-1"
    onclick={(e) => e.stopPropagation()}
    onkeydown={(e) => e.stopPropagation()}
  >
    <div class="flex items-center justify-between border-b border-slate-200 p-4 dark:border-slate-700">
      <h2 class="font-semibold text-slate-800 dark:text-slate-100">{i18n.m.shared.choose_folder}</h2>
      <button class="btn btn-ghost min-h-11 min-w-11 px-2" onclick={onClose} aria-label={i18n.m.shared.close}>
        <Icon name="x" class="h-5 w-5" />
      </button>
    </div>

    <div class="border-b border-slate-200 p-3 dark:border-slate-700">
      <div class="flex items-center gap-2">
        <button
          class="btn min-h-11 px-3 text-xs"
          disabled={!listing?.parent}
          onclick={() => listing?.parent && navigate(listing.parent)}
          title={i18n.m.shared.up_one_level}
        >
          <Icon name="arrow-left" class="h-4 w-4 rotate-90" />
          {i18n.m.shared.up_one_level}
        </button>
        <code class="flex-1 truncate rounded bg-slate-100 px-2 py-1 text-xs text-slate-600 dark:bg-slate-800 dark:text-slate-300">
          {listing?.path ?? '…'}
        </code>
      </div>
    </div>

    <div class="flex-1 overflow-y-auto p-2">
      {#if loading}
        <p class="p-4 text-center text-sm text-slate-400">{i18n.m.common.loading_short}</p>
      {:else if error}
        <p class="p-4 text-center text-sm text-red-600">{error}</p>
      {:else if listing && listing.directories.length > 0}
        {#each listing.directories as dir}
          <button
            class="flex min-h-11 w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-sm text-slate-700 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
            onclick={() => navigate(dir.path)}
          >
            <svg class="h-4 w-4 flex-shrink-0 text-amber-500" fill="currentColor" viewBox="0 0 20 20">
              <path d="M2 6a2 2 0 012-2h4l2 2h6a2 2 0 012 2v6a2 2 0 01-2 2H4a2 2 0 01-2-2V6z" />
            </svg>
            <span class="truncate">{dir.name}</span>
          </button>
        {/each}
      {:else}
        <p class="p-4 text-center text-sm text-slate-400">{i18n.m.shared.no_subfolders}</p>
      {/if}
    </div>

    <div class="flex items-center justify-between gap-2 border-t border-slate-200 p-3 dark:border-slate-700">
      <span class="truncate text-xs text-slate-500 dark:text-slate-400">{i18n.m.shared.select_highlighted}</span>
      <div class="flex gap-2">
        <button class="btn min-h-11" onclick={onClose}>{i18n.m.common.cancel}</button>
        <button class="btn btn-primary min-h-11" disabled={!listing} onclick={() => listing && onSelect(listing.path)}>
          {i18n.m.shared.select_folder}
        </button>
      </div>
    </div>
  </div>
</div>
