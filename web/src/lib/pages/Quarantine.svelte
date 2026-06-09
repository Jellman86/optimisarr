<script lang="ts">
  import { api, type Replacement } from '../api'
  import { formatSize } from '../format'

  let replacements = $state<Replacement[]>([])
  let error = $state<string | null>(null)
  let loading = $state(true)
  let rollingBackId = $state<number | null>(null)

  $effect(() => {
    void load()
  })

  async function load() {
    try {
      replacements = await api.replacements()
      error = null
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load replacements'
    } finally {
      loading = false
    }
  }

  async function rollback(replacement: Replacement) {
    if (!confirm(`Restore the original and remove the replacement for:\n\n${replacement.originalPath}`)) {
      return
    }
    rollingBackId = replacement.id
    try {
      await api.rollbackReplacement(replacement.id)
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Rollback failed'
    } finally {
      rollingBackId = null
    }
  }

  function savingPercent(r: Replacement): number {
    if (r.originalSizeBytes <= 0) return 0
    return Math.round((1 - r.newSizeBytes / r.originalSizeBytes) * 100)
  }

  let activeCount = $derived(replacements.filter((r) => r.status === 'Replaced').length)
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Quarantine</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">
    Replaced originals are kept here, not deleted. Roll back to restore an original and remove its replacement.
    Once a quarantine retention window is set in Settings, originals past it are purged and can no longer be rolled back.
    {#if activeCount > 0}<span class="text-slate-400"> · {activeCount} in quarantine</span>{/if}
  </p>
</header>

{#if error}
  <div class="card mb-4 border-red-300 p-3 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{error}</div>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">Loading…</div>
{:else if replacements.length > 0}
  <div class="card overflow-x-auto">
    <table class="w-full text-sm">
      <thead class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400">
        <tr>
          <th class="px-4 py-3">Status</th>
          <th class="px-4 py-3">Replaced file</th>
          <th class="px-4 py-3">Saving</th>
          <th class="px-4 py-3">Replaced</th>
          <th class="px-4 py-3"></th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
        {#each replacements as r (r.id)}
          <tr class="text-slate-700 dark:text-slate-300">
            <td class="px-4 py-2">
              {#if r.status === 'Replaced'}
                <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">Replaced</span>
              {:else if r.status === 'Purged'}
                <span class="badge bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400" title="The retention window expired, so the quarantined original was deleted. This replacement can no longer be rolled back.">Purged</span>
              {:else}
                <span class="badge bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400">Rolled back</span>
              {/if}
              {#if r.crossFilesystem}
                <span class="badge ml-1 bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300" title="Different filesystem: a verified copy-plus-delete was used instead of an atomic move.">copied</span>
              {/if}
            </td>
            <td class="px-4 py-2">
              <div class="max-w-md truncate font-mono text-xs" title={r.finalPath}>{r.finalPath}</div>
              {#if r.status === 'Purged'}
                <div class="text-[11px] text-slate-400">↳ original purged after retention window</div>
              {:else if r.status === 'Replaced'}
                <div class="max-w-md truncate font-mono text-[11px] text-slate-400" title={r.quarantinePath}>↳ original in {r.quarantinePath}</div>
              {/if}
            </td>
            <td class="px-4 py-2 text-xs tabular-nums">
              {formatSize(r.originalSizeBytes)} → {formatSize(r.newSizeBytes)}
              <span class="text-emerald-600 dark:text-emerald-400"> (−{savingPercent(r)}%)</span>
            </td>
            <td class="px-4 py-2 text-xs text-slate-500">{new Date(r.replacedAt).toLocaleString()}</td>
            <td class="px-4 py-2 text-right">
              {#if r.status === 'Replaced'}
                <button class="btn px-3 py-1 text-xs" onclick={() => rollback(r)} disabled={rollingBackId === r.id}>
                  {rollingBackId === r.id ? 'Restoring' : 'Roll back'}
                </button>
              {/if}
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
  <p class="mt-2 text-xs text-slate-400">{replacements.length.toLocaleString()} replacements</p>
{:else}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    Nothing in quarantine. When you replace a verified job from the Queue, its original is kept here for rollback.
  </div>
{/if}
