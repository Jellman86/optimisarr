<script lang="ts">
  import { i18n } from '../i18n/i18n.svelte'
  import { api, ApiError, type BrowseResponse } from '../api'
  import { modal } from '../modal'
  import Icon from './Icon.svelte'

  let { initialPath = '', onSelect, onClose }: {
    initialPath?: string
    onSelect: (path: string) => void
    onClose: () => void
  } = $props()

  let listing = $state<BrowseResponse | null>(null)
  let error = $state<string | null>(null)
  let notice = $state<string | null>(null)
  let loading = $state(true)

  $effect(() => {
    void openInitial(initialPath)
  })

  async function openInitial(path: string) {
    const trimmed = path.trim()
    if (!trimmed) {
      await navigate(undefined)
      return
    }
    loading = true
    error = null
    notice = null
    try {
      listing = await api.browse(trimmed)
    } catch (err) {
      const reason = err instanceof Error ? err.message : i18n.m.shared.browse_failed
      // A saved library or target folder may have been moved, unmounted or removed. Only then
      // open the default folder so a replacement can be picked; any other failure (the server
      // unreachable, an internal error) is shown as it is rather than hidden behind a fallback.
      if (!isUnusablePath(err)) {
        listing = null
        error = reason
        return
      }
      try {
        listing = await api.browse(undefined)
        notice = `${reason} ${i18n.m.shared.browse_showing_default}`
      } catch (fallbackErr) {
        listing = null
        error = `${reason} · ${fallbackErr instanceof Error ? fallbackErr.message : i18n.m.shared.browse_failed}`
      }
    } finally {
      loading = false
    }
  }

  function isUnusablePath(err: unknown): boolean {
    return err instanceof ApiError && (err.code === 'filesystem.notDirectory' || err.code === 'filesystem.accessDenied')
  }

  async function navigate(path?: string) {
    loading = true
    error = null
    notice = null
    try {
      listing = await api.browse(path)
    } catch (err) {
      const reason = err instanceof Error ? err.message : i18n.m.shared.browse_failed
      if (listing) {
        notice = reason
      } else {
        error = reason
      }
    } finally {
      loading = false
    }
  }
</script>

<dialog class="app-modal folder-dialog" use:modal={onClose} aria-label={i18n.m.shared.choose_folder}>
  <div class="hairline-b flex items-center justify-between p-4">
    <h2 class="font-semibold text-ink">{i18n.m.shared.choose_folder}</h2>
    <button class="btn btn-ghost btn-icon" onclick={onClose} aria-label={i18n.m.shared.close}>
      <Icon name="x" class="h-5 w-5" />
    </button>
  </div>

  <div class="hairline-b p-3">
    <div class="flex items-center gap-2">
      <button
        class="btn btn-sm"
        disabled={loading || !listing?.parent}
        onclick={() => listing?.parent && navigate(listing.parent)}
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
    {#if notice}
      <p role="alert" class="callout tone-bad mb-2">{notice}</p>
    {/if}
    {#if loading}
      <p class="p-4 text-center text-sm text-ink-3">{i18n.m.common.loading_short}</p>
    {:else if error}
      <p role="alert" class="p-4 text-center text-sm text-bad">{error}</p>
    {:else if listing && listing.directories.length > 0}
      {#each listing.directories as dir}
        <button
          class="hit-target focus-ring-inset flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-sm text-ink-2 hover:bg-raised"
          onclick={() => navigate(dir.path)}
        >
          <Icon name="folder" class="h-4 w-4 flex-shrink-0 text-ink-3" />
          <span class="truncate">{dir.name}</span>
        </button>
      {/each}
    {:else}
      <p class="p-4 text-center text-sm text-ink-3">{i18n.m.shared.no_subfolders}</p>
    {/if}
  </div>

  <div class="hairline-t flex flex-wrap items-center justify-between gap-2 p-3">
    <span class="truncate text-xs text-ink-3">{i18n.m.shared.select_highlighted}</span>
    <div class="flex gap-2">
      <button class="btn btn-lg" onclick={onClose}>{i18n.m.common.cancel}</button>
      <button class="btn btn-lg btn-primary" disabled={loading || !!error || !listing} onclick={() => listing && onSelect(listing.path)}>
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
