<script lang="ts">
  import { onMount } from 'svelte'
  import { api, type DiagnosticCapture } from '../api'
  import { i18n } from '../i18n/i18n.svelte'
  let busy = $state(false)
  let error = $state('')
  let capture = $state<DiagnosticCapture | null>(null)
  async function collect() {
    if (!capture) return
    const id = capture.id
    busy = true; error = ''
    try {
      if (capture.status === 'Recording') capture = await api.stopDiagnosticCapture(id)
      window.dispatchEvent(new Event('diagnostic-capture-changed'))
      await api.waitForDiagnosticUploads(id)
      const blob = await api.diagnosticBundle(id)
      const url = URL.createObjectURL(blob); const link = document.createElement('a')
      link.href = url; link.download = `optimisarr-diagnostics-session-${id}.json`; link.click()
      window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) { error = cause instanceof Error ? cause.message : String(cause) }
    finally { busy = false }
  }
  onMount(() => {
    let disposed = false
    const refresh = () => {
      if (busy) return
      void api.diagnosticCapture().then(value => {
        if (disposed || busy) return
        if (capture?.id !== value?.id) error = ''
        capture = value
      }).catch(() => {})
    }
    refresh()
    const timer = window.setInterval(refresh, 30000)
    window.addEventListener('diagnostic-capture-changed', refresh)
    return () => { disposed = true; window.clearInterval(timer); window.removeEventListener('diagnostic-capture-changed', refresh) }
  })
</script>
{#if capture && (capture.status === 'Recording' || busy || error)}
  <aside class="mb-4 flex flex-wrap items-center justify-between gap-2 rounded-lg border border-accent/30 bg-accent/5 px-4 py-2 text-sm" aria-label={i18n.m.settings.diagnostics_title}>
    {#if busy}<span role="status">{i18n.m.settings.diagnostics_collecting}</span>
    {:else}<span>{capture.status === 'Recording' ? i18n.m.settings.diagnostics_recording : i18n.m.settings.diagnostics_off} · {capture.eventsStored.toLocaleString()} {i18n.m.settings.diagnostics_events}{capture.eventLimitReached ? ` · ${i18n.m.settings.diagnostics_cap_reached}` : ''}</span>{/if}
    <button class="btn btn-ghost min-h-11" disabled={busy} onclick={collect}>{capture.status === 'Recording' ? i18n.m.settings.diagnostics_stop_collect : i18n.m.settings.diagnostics_download}</button>
    {#if error}
      <p class="w-full text-sm text-bad" role="alert">{error}</p>
      <button class="btn btn-ghost min-h-11" onclick={() => { error = '' }}>{i18n.m.common.close}</button>
    {/if}
  </aside>
{/if}
