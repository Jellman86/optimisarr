<script lang="ts">
  // Fresh installs start with replacing locked (dry-run), so the first Replace button a new user
  // meets does nothing. This says why, where the button is, and unlocks it.
  //
  // Dry-run holds back more than the Replace button: deleting quarantined originals and duplicate
  // copies past the time limit, daily duplicate clean-up, and automatic replacement of files that
  // are already verified. So the text says all of that, and when any of it would act straight
  // away the server's counts are shown and a second, explicit press is needed. With nothing
  // pending, as on a fresh install, one press is enough.
  import { onMount } from 'svelte'
  import { api, type DryRunUnlockEffects } from '../api'
  import { formatSize } from '../format'
  import { i18n, plural, t } from '../i18n/i18n.svelte'

  let { onunlocked }: { onunlocked: () => void } = $props()

  let busy = $state(false)
  let error = $state<string | null>(null)
  let effects = $state<DryRunUnlockEffects | null>(null)
  // Not knowing is treated as "something may happen": the confirmation is asked for.
  let effectsUnknown = $state(false)
  let confirming = $state(false)
  const settingsPlace = $derived(`${i18n.m.nav.settings} → ${i18n.m.settings.room_files}`)
  const needsConfirmation = $derived(effectsUnknown || effects?.needsConfirmation === true)

  onMount(() => { void loadEffects() })

  async function loadEffects() {
    try {
      effects = await api.dryRunUnlockEffects()
      effectsUnknown = false
    } catch {
      effects = null
      effectsUnknown = true
    }
  }

  async function press() {
    if (busy) return
    if (effects === null && !effectsUnknown) await loadEffects()
    if (needsConfirmation && !confirming) {
      confirming = true
      return
    }
    await unlock()
  }

  async function unlock() {
    busy = true
    error = null
    try {
      // Read first: saving sends every setting, and only this one is meant to change.
      const current = await api.settings()
      await api.saveSettings({ ...current, dryRunMode: false })
      onunlocked()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.jobResult.unlock_failed
    } finally {
      busy = false
    }
  }
</script>

<div class="callout tone-warn" data-dry-run-unlock>
  <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
    <p id="replace-locked-reason" class="min-w-0 flex-1 text-sm font-medium">{i18n.m.jobResult.locked_reason}</p>
    {#if !confirming}
      <button type="button" class="btn min-h-11" onclick={press} disabled={busy}>{busy ? i18n.m.jobResult.unlocking : i18n.m.jobResult.unlock}</button>
    {/if}
  </div>
  <p class="mt-2 text-xs leading-relaxed">{t(i18n.m.jobResult.unlock_scope, { place: settingsPlace })}</p>
  {#if confirming}
    <div class="mt-3" role="group" aria-labelledby="dry-run-unlock-effects" data-dry-run-confirm>
      <p id="dry-run-unlock-effects" class="text-sm font-semibold">{i18n.m.jobResult.unlock_effects_title}</p>
      <ul class="mt-1.5 list-disc space-y-1 pl-5 text-xs leading-relaxed">
        {#if effects}
          {#if effects.jobsToReplace > 0}<li data-unlock-effect="replace">{plural(effects.jobsToReplace, i18n.m.jobResult.effect_replace_one, i18n.m.jobResult.effect_replace_other, effects.jobsToReplace.toLocaleString())}</li>{/if}
          {#if effects.originalsToDelete > 0}<li data-unlock-effect="originals">{t(effects.originalsToDelete === 1 ? i18n.m.jobResult.effect_originals_one : i18n.m.jobResult.effect_originals_other, { count: effects.originalsToDelete.toLocaleString(), size: formatSize(effects.originalBytes) })}</li>{/if}
          {#if effects.duplicatesToDelete > 0}<li data-unlock-effect="duplicates">{t(effects.duplicatesToDelete === 1 ? i18n.m.jobResult.effect_duplicates_one : i18n.m.jobResult.effect_duplicates_other, { count: effects.duplicatesToDelete.toLocaleString(), size: formatSize(effects.duplicateBytes) })}</li>{/if}
          {#if effects.librariesCleaningDuplicatesDaily > 0}<li data-unlock-effect="dedupe">{plural(effects.librariesCleaningDuplicatesDaily, i18n.m.jobResult.effect_dedupe_one, i18n.m.jobResult.effect_dedupe_other, effects.librariesCleaningDuplicatesDaily.toLocaleString())}</li>{/if}
        {:else}
          <li data-unlock-effect="unknown">{i18n.m.jobResult.effects_unknown}</li>
        {/if}
      </ul>
      <div class="mt-3 flex flex-wrap gap-2">
        <button type="button" class="btn btn-danger min-h-11" onclick={unlock} disabled={busy}>{busy ? i18n.m.jobResult.unlocking : i18n.m.jobResult.unlock_confirm}</button>
        <button type="button" class="btn min-h-11" onclick={() => (confirming = false)} disabled={busy}>{i18n.m.common.cancel}</button>
      </div>
    </div>
  {/if}
  {#if error}<p class="mt-2 text-xs text-bad" role="alert">{error}</p>{/if}
</div>
