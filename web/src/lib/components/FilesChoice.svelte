<script lang="ts">
  // What happens to a file once it has passed its checks, as one choice of three. The answer is
  // stored in the library's existing `autoReplace` and `moveOnComplete` switches; see files-choice.ts.
  import { untrack } from 'svelte'
  import { api, type TargetFolderCheck } from '../api'
  import { filesChoice, flagsFor, hasContradictoryFlags, savedElsewherePath, type FilesChoice } from '../files-choice'
  import { formatSize } from '../format'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import AutoAcceptConfirm from './AutoAcceptConfirm.svelte'
  import FolderPicker from './FolderPicker.svelte'
  import Icon from './Icon.svelte'

  let {
    autoReplace = $bindable(false),
    moveOnComplete = $bindable(false),
    targetFolder = $bindable(null),
    moveOverwrite = $bindable(false),
    folderRefused = $bindable(false),
    libraryId = null,
    libraryName,
    libraryPath,
    exampleRelativePath = null,
    retentionDays = null,
    dryRun = false,
  }: {
    autoReplace?: boolean
    moveOnComplete?: boolean
    targetFolder?: string | null
    moveOverwrite?: boolean
    /** True while the chosen folder is one the server will refuse, so the page can hold Save back. */
    folderRefused?: boolean
    libraryId?: number | null
    libraryName: string
    libraryPath: string
    /** A real file from this library, to show where its copy would be saved. */
    exampleRelativePath?: string | null
    /** Null until the server's settings have loaded. */
    retentionDays?: number | null | 'unavailable'
    dryRun?: boolean
  } = $props()

  const id = $props.id()
  const choice = $derived(filesChoice({ autoReplace, moveOnComplete }))
  const contradictory = $derived(hasContradictoryFlags({ autoReplace, moveOnComplete }))
  const settingsPlace = $derived(`${i18n.m.nav.settings} → ${i18n.m.settings.room_files}`)
  let confirming = $state(false)
  let pickerOpen = $state(false)
  let check = $state<TargetFolderCheck | null>(null)
  let checking = $state(false)
  let checkFailed = $state(false)

  const options = $derived<{ value: FilesChoice; label: string; description: string }[]>([
    { value: 'ask', label: i18n.m.filesChoice.ask_label, description: i18n.m.filesChoice.ask_desc },
    { value: 'auto', label: i18n.m.filesChoice.auto_label, description: i18n.m.filesChoice.auto_desc },
    { value: 'elsewhere', label: i18n.m.filesChoice.elsewhere_label, description: i18n.m.filesChoice.elsewhere_desc },
  ])

  function choose(next: FilesChoice, event: Event) {
    if (next === 'auto' && choice !== 'auto') {
      // The radio stays where it was until the risk has been read and accepted.
      event.preventDefault()
      // Safari does not always focus a clicked radio. Give the dialog an explicit opener so
      // cancelling returns keyboard users to this control on every browser.
      ;(event.currentTarget as HTMLInputElement).focus({ preventScroll: true })
      confirming = true
      return
    }
    ;({ autoReplace, moveOnComplete } = flagsFor(next))
  }

  function acceptAutomatic() {
    ;({ autoReplace, moveOnComplete } = flagsFor('auto'))
    confirming = false
  }

  // The server owns the rule, so the reason shown here is the one Save would give.
  $effect(() => {
    const path = moveOnComplete ? targetFolder?.trim() : ''
    if (!path) {
      check = null
      checking = false
      checkFailed = false
      return
    }
    let cancelled = false
    checking = true
    checkFailed = false
    // The name only labels a refusal, so typing it must not ask the server again.
    api.checkTargetFolder({ path, libraryId, libraryName: untrack(() => libraryName), libraryPath })
      .then((result) => { if (!cancelled) check = result })
      .catch(() => { if (!cancelled) { check = null; checkFailed = true } })
      .finally(() => { if (!cancelled) checking = false })
    return () => { cancelled = true }
  })

  $effect(() => {
    folderRefused = moveOnComplete && check !== null && !check.allowed
  })

  const folderMessage = $derived.by(() => {
    if (!check) return null
    switch (check.reason) {
      case 'insideLibrary': return t(i18n.m.filesChoice.folder_inside_library, { library: check.libraryName ?? '' })
      case 'workFolder': return i18n.m.filesChoice.folder_work
      case 'quarantine': return i18n.m.filesChoice.folder_quarantine
      case 'missing': return i18n.m.filesChoice.folder_missing
      case 'unwritable': return i18n.m.filesChoice.folder_unwritable
      default: return check.availableBytes === null
        ? i18n.m.filesChoice.folder_ok
        : t(i18n.m.filesChoice.folder_ok_free, { free: formatSize(check.availableBytes) })
    }
  })
</script>

<fieldset class="min-w-0" data-files-choice>
  <legend class="text-sm font-semibold text-ink">{i18n.m.filesChoice.heading}</legend>
  <p class="mt-1 text-sm leading-relaxed text-ink-3">{i18n.m.filesChoice.intro}</p>

  {#if contradictory}
    <p class="callout tone-warn mt-3 text-sm leading-relaxed" data-files-choice-both>{i18n.m.filesChoice.both_note}</p>
  {/if}

  <div class="mt-4 grid gap-3">
    {#each options as option (option.value)}
      {@const selected = choice === option.value}
      <div class="choice rounded-xl p-4 {selected ? 'choice-selected' : ''}">
        <label class="flex min-h-11 cursor-pointer items-start gap-3">
          <input
            type="radio"
            name={`${id}-files-choice`}
            class="mt-1 h-4 w-4 flex-shrink-0 accent-accent-fill"
            value={option.value}
            checked={selected}
            aria-labelledby={`${id}-${option.value}-label`}
            aria-describedby={`${id}-${option.value}-desc`}
            onclick={(event) => choose(option.value, event)}
          />
          <span class="min-w-0">
            <span id={`${id}-${option.value}-label`} class="block text-sm font-semibold text-ink">{option.label}</span>
            <span id={`${id}-${option.value}-desc`} class="mt-1 block text-sm font-normal leading-relaxed text-ink-2">{option.description}</span>
            {#if option.value === 'auto'}
              <span class="mt-1 block text-xs font-normal leading-relaxed text-ink-3">{i18n.m.filesChoice.auto_confirm_note}</span>
            {/if}
          </span>
        </label>

        {#if selected}
          <dl class="mt-3 grid gap-2 border-t border-line pt-3 text-sm sm:grid-cols-[10rem_minmax(0,1fr)]" aria-label={i18n.m.filesChoice.where_title}>
            <dt class="font-medium text-ink-2">{i18n.m.filesChoice.original}</dt>
            <dd class="leading-relaxed text-ink-2">
              {#if option.value === 'elsewhere'}
                {i18n.m.filesChoice.original_stays}
              {:else}
                {i18n.m.filesChoice.original_quarantine}
                {#if retentionDays === 'unavailable'}
                  {i18n.m.librarySummary.originals_unknown}
                {:else if retentionDays !== null}
                  {retentionDays > 0
                    ? plural(retentionDays, i18n.m.filesChoice.original_deleted_one, i18n.m.filesChoice.original_deleted_other)
                    : i18n.m.filesChoice.original_kept}
                {/if}
              {/if}
            </dd>
            <dt class="font-medium text-ink-2">{i18n.m.filesChoice.optimised}</dt>
            <dd class="leading-relaxed text-ink-2">{option.value === 'elsewhere' ? i18n.m.filesChoice.optimised_saved : i18n.m.filesChoice.optimised_replaces}</dd>
          </dl>

          {#if option.value === 'elsewhere'}
            <p class="mt-2 text-xs leading-relaxed text-ink-3">{i18n.m.filesChoice.not_told}</p>
            <div class="mt-4 max-w-2xl">
              <label class="label" for={`${id}-target`}>{i18n.m.filesChoice.folder_label}</label>
              <div class="flex gap-2">
                <input
                  id={`${id}-target`}
                  class="input"
                  readonly
                  placeholder={i18n.m.filesChoice.folder_placeholder}
                  value={targetFolder ?? ''}
                  aria-invalid={folderRefused ? 'true' : undefined}
                  aria-describedby={`${id}-target-state`}
                />
                <button type="button" class="btn min-h-11 flex-shrink-0" onclick={(event) => { event.currentTarget.focus({ preventScroll: true }); pickerOpen = true }}>{i18n.m.libraries.browse}</button>
              </div>
              <div id={`${id}-target-state`} class="mt-2 text-xs leading-relaxed" aria-live="polite" data-target-folder-state={check?.reason ?? (checking ? 'checking' : 'none')}>
                {#if !targetFolder?.trim()}
                  <p class="text-warn">{i18n.m.filesChoice.folder_required}</p>
                {:else if checking}
                  <p class="text-ink-3">{i18n.m.filesChoice.folder_checking}</p>
                {:else if checkFailed}
                  <p class="text-ink-3">{i18n.m.filesChoice.folder_unchecked}</p>
                {:else if check && folderMessage}
                  <p class="flex items-start gap-1.5 {check.allowed ? 'text-ok' : 'text-bad'}">
                    <Icon name={check.allowed ? 'check' : 'warning'} class="mt-0.5 h-3.5 w-3.5 flex-shrink-0" />
                    <span>{folderMessage}</span>
                  </p>
                  {#if check.allowed && exampleRelativePath && targetFolder}
                    <p class="mt-1 break-all text-ink-3">{t(i18n.m.filesChoice.example, {
                      from: savedElsewherePath(libraryPath, exampleRelativePath),
                      to: savedElsewherePath(targetFolder, exampleRelativePath),
                    })}</p>
                  {/if}
                {/if}
              </div>
              <label class="mt-3 flex cursor-pointer items-start gap-2 text-sm">
                <input type="checkbox" class="checkbox mt-0.5" bind:checked={moveOverwrite} />
                <span>
                  {i18n.m.filesChoice.overwrite_label}
                  <span class="mt-0.5 block text-xs font-normal leading-relaxed text-ink-3">{i18n.m.filesChoice.overwrite_hint}</span>
                </span>
              </label>
            </div>
          {:else}
            <p class="mt-2 text-xs leading-relaxed text-ink-3">{t(i18n.m.filesChoice.retention_scope, { place: settingsPlace })}</p>
            {#if dryRun}
              <p class="callout tone-warn mt-3 text-xs leading-relaxed" data-files-choice-dry-run>{t(i18n.m.filesChoice.dry_run_on, { place: settingsPlace })}</p>
            {/if}
          {/if}
        {/if}
      </div>
    {/each}
  </div>
</fieldset>

{#if confirming}
  <AutoAcceptConfirm {libraryName} onaccept={acceptAutomatic} oncancel={() => (confirming = false)} />
{/if}

{#if pickerOpen}
  <FolderPicker
    initialPath={targetFolder ?? ''}
    onSelect={(path) => {
      targetFolder = path
      pickerOpen = false
    }}
    onClose={() => (pickerOpen = false)}
  />
{/if}
