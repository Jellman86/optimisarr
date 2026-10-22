<script lang="ts">
  import { onDestroy, tick } from 'svelte'
  import { api, type ExactDuplicateGroup } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { formatSize } from '../format'
  import Icon from './Icon.svelte'
  import Thumbnail from './Thumbnail.svelte'
  import Banner from './Banner.svelte'

  let { group, libraryId, disabled = false, onrefresh }: {
    group: ExactDuplicateGroup; libraryId: number; disabled?: boolean; onrefresh: (libraryId: number) => Promise<void>
  } = $props()
  let keepIds = $state<number[]>([])
  let saving = $state(false)
  let error = $state<string | null>(null)
  let observed = ''
  let preset = $state('manual')
  let presetId = $derived(`keeper-${group.reviewKey}`)
  let keeperLibraries = $derived(Array.from(new Map(group.copies.flatMap(c => [
    { id:c.libraryId, name:c.libraryName }, ...(c.aliases ?? []).map(a => ({ id:a.libraryId, name:a.libraryName }))
  ]).filter(l => l.id && l.name).map(l => [l.id, l])).values()))
  let disposed = false
  let summaryElement: HTMLElement
  onDestroy(() => { disposed = true })
  let savedChoice = $derived(group.decision?.choice)
  // Name the group after the copy in the library being reviewed; other libraries hold the extras.
  let displayName = $derived((group.copies.find(c => c.libraryId === libraryId) ?? group.copies[0]).relativePath.split(/[\\/]/).at(-1))
  let canSave = $derived(group.canDecide === true && !disabled && !saving)
  $effect.pre(() => {
    const key = `${group.reviewKey}:${group.decision?.savedAt}:${group.decision?.choice}`
    if (key === observed) return
    observed = key; keepIds = [...(group.decision?.keepIds ?? [])]; preset = 'manual'; error = null
  })
  function toggle(id: number, checked: boolean) {
    preset = 'manual'
    keepIds = checked ? [...keepIds, id] : keepIds.filter(value => value !== id)
  }
  function applyPreset(value: string) {
    preset = value
    if (value === 'all') keepIds = group.copies.map(c => c.id)
    else if (value.startsWith('library:')) {
      const id = Number(value.slice(8))
      keepIds = group.copies.filter(c => c.libraryId === id || c.aliases?.some(a => a.libraryId === id)).map(c => c.id)
    }
  }
  async function save(choice: 'Keep' | 'Ignore' | 'Review') {
    if (!canSave || !group.reviewKey) return
    const owner = libraryId, key = group.reviewKey
    saving = true; error = null
    try {
      await api.saveExactDuplicateReview(owner, key, choice, choice === 'Keep' ? keepIds : [])
      if (!disposed && owner === libraryId && key === group.reviewKey) {
        await onrefresh(owner)
        if (choice !== 'Keep') { await tick(); if (!disposed) summaryElement?.focus() }
      }
    } catch (err) { if (!disposed && owner === libraryId && key === group.reviewKey) error = err instanceof Error ? err.message : i18n.m.inventory.error_load }
    finally { saving = false }
  }
</script>

<details class="card duplicate-group" open={group.copies.length <= 5 && savedChoice !== 'Ignore'}>
  <summary bind:this={summaryElement} class="focus-ring flex min-w-0 items-center gap-4 p-5 sm:p-6">
    <Thumbnail mediaFileId={group.copies[0].id} shape={['Film','Tv'].includes(group.copies[0].libraryType ?? '') ? 'portrait' : 'square'} size="md" />
    <div class="min-w-0 flex-1">
      <div class="flex flex-wrap items-center gap-2"><h2 class="break-words text-base font-semibold text-ink">{displayName}</h2>
        {#if group.reviewKey}<span class="badge" class:badge-ok={savedChoice === 'Keep'}>{savedChoice === 'Keep' ? i18n.m.duplicates.kept : savedChoice === 'Ignore' ? i18n.m.duplicates.ignored : i18n.m.duplicates.unreviewed}</span>{/if}
      </div>
      <p class="mt-1 text-sm text-ink-3" title={`SHA-256 ${group.sha256}`}>{t(i18n.m.duplicates.copies,{count:group.totalCopies || group.copies.length})} · {formatSize(group.sizeBytes)} · {i18n.m.duplicates.fullBytes}</p>
    </div>
    <Icon name="chevron" class="h-4 w-4 shrink-0 text-ink-3" />
  </summary>
  <div class="border-t border-line p-5 sm:p-6">
    {#if group.reviewKey}
      <div class="mb-5">
        <label class="label" for={presetId}>{i18n.m.duplicates.keeperPreset}</label>
        <select class="input min-h-11 max-w-md" id={presetId} value={preset} disabled={!canSave} onchange={event => applyPreset(event.currentTarget.value)} aria-describedby={`${presetId}-hint`}>
          <option value="manual">{i18n.m.duplicates.manualKeep}</option>
          <option value="all">{i18n.m.duplicates.keepAll}</option>
          {#each keeperLibraries as library (library.id)}<option value={`library:${library.id}`}>{t(i18n.m.duplicates.keepLibrary, { library: library.name ?? '' })}</option>{/each}
        </select>
        <p class="mt-2 text-xs leading-relaxed text-ink-3" id={`${presetId}-hint`}>{i18n.m.duplicates.identicalRanking}</p>
      </div>
    {/if}
    <ul class="copy-grid">
      {#each group.copies as copy (copy.id)}
        <li class="copy-card" class:copy-kept={keepIds.includes(copy.id)}>
          <div class="copy-heading flex min-w-0 items-start gap-4">
            <Thumbnail mediaFileId={copy.id} shape={['Film','Tv'].includes(copy.libraryType ?? '') ? 'portrait' : 'square'} size="lg" />
            <div class="min-w-0 flex-1">
              {#if copy.libraryName}<span class="badge mb-2">{copy.libraryName}</span>{/if}
              <h3 class="break-words text-sm font-semibold leading-relaxed text-ink">{copy.relativePath.split(/[\\/]/).at(-1)}</h3>
              <p class="mt-2 text-sm text-ink-3">{formatSize(group.sizeBytes)}</p>
              {#if copy.hardLinkCount === null || copy.hardLinkCount > 1}<p class="mt-2 text-xs leading-relaxed text-ink-3">{copy.hardLinkCount === null ? i18n.m.duplicates.unknown : t(i18n.m.duplicates.linked,{count:copy.hardLinkCount})}</p>{/if}
            </div>
          </div>
          {#if /[\\/]/.test(copy.relativePath)}<p class="mt-4 break-all font-mono text-sm leading-relaxed text-ink">{copy.relativePath}</p>{/if}
          {#if copy.aliases?.length}
            <div class="mt-4 border-t border-line pt-3"><p class="text-xs font-semibold text-ink-2">{i18n.m.duplicates.aliases}</p>
              {#each copy.aliases as alias (alias.id)}<p class="mt-2 break-all text-xs leading-relaxed text-ink-3">{alias.libraryName} · {alias.relativePath}</p>{/each}
            </div>
          {/if}
          {#if group.reviewKey}<label class="keep-control mt-4 flex min-h-11 cursor-pointer items-center gap-3 border-t border-line pt-3 text-sm font-medium text-ink">
            <input class="h-4 w-4 accent-accent" type="checkbox" checked={keepIds.includes(copy.id)} disabled={!canSave} onchange={event => toggle(copy.id,event.currentTarget.checked)} aria-label={`${i18n.m.duplicates.keepCopy}: ${copy.libraryName ?? ''} ${copy.relativePath}`} />
            {i18n.m.duplicates.keepCopy}
          </label>{/if}
        </li>
      {/each}
    </ul>
    <p class="mt-4 text-sm leading-relaxed text-ink-3">{group.extraCopyBytes === null ? i18n.m.duplicates.spaceUnknown : t(i18n.m.duplicates.extra,{bytes:formatSize(group.extraCopyBytes)})}</p>
    {#if group.reviewKey}
      <div class="mt-5 border-t border-line pt-4">
        <p class="text-sm leading-relaxed text-ink-2">{i18n.m.duplicates.reviewHint}</p>
        <div class="mt-4 flex flex-wrap items-center gap-3">
          <button class="btn btn-primary" disabled={!canSave || keepIds.length === 0} onclick={() => save('Keep')}>{saving ? i18n.m.common.loading_short : i18n.m.duplicates.saveKeep}</button>
          <button class="btn" disabled={!canSave} onclick={() => save('Ignore')}>{i18n.m.duplicates.ignoreGroup}</button>
          {#if group.decision}<button class="btn" disabled={!canSave} onclick={() => save('Review')}>{i18n.m.duplicates.reviewAgain}</button>{/if}
        </div>
        {#if group.decision}<p class="mt-3 text-xs text-ink-3" role="status">{t(i18n.m.duplicates.saved, { date: new Date(group.decision.savedAt).toLocaleString() })}</p>{/if}
        {#if !group.canDecide || disabled}<p class="mt-3 text-sm text-ink-3">{i18n.m.duplicates.decisionUnavailable}</p>{/if}
        <p class="mt-3 text-xs leading-relaxed text-ink-3">{i18n.m.duplicates.preferencesOnly}</p>
      </div>
    {/if}
    {#if group.canDecide === false && !group.reviewKey}<p class="mt-4 text-sm text-ink-3">{i18n.m.duplicates.decisionUnavailable}</p>{/if}
    {#if error}<div class="mt-4"><Banner kind="error">{error}</Banner></div>{/if}
  </div>
</details>

<style>
  .copy-grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(min(100%,22rem),1fr)); gap:1rem; list-style:none; padding:0; }
  .copy-card { display:flex; flex-direction:column; min-width:0; container-type:inline-size; padding:1rem; border:1px solid var(--line); border-radius:var(--radius-md,12px); background:var(--raised); }
  .keep-control { margin-top:auto; padding-top:1rem; }
  .copy-kept { border-color:var(--accent-line); background:var(--accent-soft); }
  .badge { color:var(--ink-2); background:var(--panel); }
  .badge-ok { color:var(--accent-strong); background:var(--accent-soft); }
  .copy-card h3 { overflow-wrap:anywhere; }
  @container (max-width:14rem) { .copy-heading { flex-direction:column; } }
  .keep-control:has(input:disabled) { cursor:default; }
  button { min-width:0; max-width:100%; min-height:2.75rem; overflow-wrap:anywhere; }
  summary { cursor:pointer; list-style:none; }
  summary::-webkit-details-marker { display:none; }
  details[open] summary > :global(svg:last-child) { transform:rotate(180deg); }
  @media (max-width:639px) { .copy-grid { gap:.75rem; } }
</style>
