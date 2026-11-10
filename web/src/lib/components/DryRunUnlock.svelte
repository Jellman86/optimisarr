<script lang="ts">
  // Fresh installs start with replacing locked (dry-run), so the first Replace button a new user
  // meets does nothing. This says why, where the button is, and unlocks it in one click. The lock
  // belongs to every library, and unlocking lets libraries that replace automatically do so, so
  // both are said beside the button rather than behind it.
  import { api } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'

  let { onunlocked }: { onunlocked: () => void } = $props()

  let busy = $state(false)
  let error = $state<string | null>(null)
  const settingsPlace = $derived(`${i18n.m.nav.settings} → ${i18n.m.settings.room_files}`)

  async function unlock() {
    if (busy) return
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
    <button type="button" class="btn min-h-11" onclick={unlock} disabled={busy}>{busy ? i18n.m.jobResult.unlocking : i18n.m.jobResult.unlock}</button>
  </div>
  <p class="mt-2 text-xs leading-relaxed">{t(i18n.m.jobResult.unlock_scope, { place: settingsPlace })}</p>
  {#if error}<p class="mt-2 text-xs text-bad" role="alert">{error}</p>{/if}
</div>
