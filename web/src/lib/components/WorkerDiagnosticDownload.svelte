<script lang="ts">
  import { api } from '../api'
  import { i18n } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  let { workerId }: { workerId: number } = $props()
  let busy = $state(false)
  let error = $state('')
  async function download() {
    busy = true
    error = ''
    try {
      const capture = await api.diagnosticCapture()
      if (!capture) { router.go('/settings/system#diagnostic-capture'); return }
      const blob = await api.diagnosticBundle(capture.id, undefined, workerId)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a'); link.href = url
      link.download = `optimisarr-worker-${workerId}-${capture.id}.json`; link.click()
      window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) { error = cause instanceof Error ? cause.message : String(cause) }
    finally { busy = false }
  }
</script>
<button class="btn btn-ghost min-h-11 text-xs" disabled={busy} onclick={download}>{i18n.m.settings.diagnostics_download}</button>
{#if error}<p class="text-sm text-bad" role="alert">{error}</p>{/if}
