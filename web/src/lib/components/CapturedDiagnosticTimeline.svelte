<script lang="ts">
  import { api } from '../api'
  import { i18n } from '../i18n/i18n.svelte'
  let { captureId, jobId }: { captureId: string; jobId: number } = $props()
  type Event = { id: number; receivedAt: string; occurredAt: string; attempt: number; workerId: number | null;
    reasonCode: string; source: string; currentStatus: string; details?: { report?: { passed: boolean; checks: { name: string; outcome: string }[]; vmafHarmonicMean: number | null; location: string } } }
  let events = $state<Event[]>([])
  let loading = $state(true)
  let error = $state('')
  $effect(() => {
    const id = captureId, job = jobId
    let disposed = false
    loading = true; events = []; error = ''
    void api.diagnosticBundle(id, job).then(blob => blob.text()).then(text => {
      if (!disposed) { const bundle = JSON.parse(text); events = Array.isArray(bundle.events) ? bundle.events : [] }
    }).catch(cause => { if (!disposed) error = cause instanceof Error ? cause.message : String(cause) })
      .finally(() => { if (!disposed) loading = false })
    return () => { disposed = true }
  })
</script>
<section class="mt-6 min-w-0 border-t pt-4" style="border-color: var(--edge)" aria-label={i18n.m.settings.diagnostics_history}>
  <h3 class="text-sm font-semibold text-ink">{i18n.m.settings.diagnostics_history}</h3>
  {#if loading}<p class="mt-2 text-sm text-ink-3" role="status">{i18n.m.common.loading_short}</p>
  {:else if error}<p class="mt-2 text-sm text-bad" role="alert">{error}</p>
  {:else if events.length === 0}<p class="mt-2 text-sm text-ink-3">{i18n.m.settings.diagnostics_desc}</p>
  {:else}
    <p class="mt-2 text-xs text-ink-3">{Math.min(events.length, 100)} / {events.length} {i18n.m.settings.diagnostics_events}</p>
    <ol class="mt-3 space-y-2" aria-label={i18n.m.settings.diagnostics_events}>
      {#each events.slice(-100) as event (event.id)}
        <li class="rounded-lg border p-3 text-xs" style="border-color: var(--edge)">
          <div class="flex flex-wrap items-baseline gap-2"><strong class="break-all text-ink">{event.reasonCode}</strong><span class="text-ink-3">#{event.attempt} · {event.source}{event.workerId == null ? '' : ` · Worker #${event.workerId}`}</span></div>
          <time class="mt-1 block text-ink-3" datetime={event.receivedAt} title={`Local: ${event.occurredAt}`}>{new Date(event.receivedAt).toLocaleString()} · {event.currentStatus}</time>
          {#if event.details?.report}
            <details class="mt-2">
              <summary class="min-h-11 cursor-pointer py-2 text-ink">{i18n.m.queue.col_verification} · {event.details.report.location}</summary>
              <ul class="space-y-1">{#each event.details.report.checks as check}<li class="flex flex-wrap justify-between gap-2"><span>{check.name}</span><strong class:text-bad={check.outcome === 'Failed'}>{check.outcome}</strong></li>{/each}</ul>
              {#if event.details.report.vmafHarmonicMean != null}<p class="mt-2 font-mono">VMAF: {event.details.report.vmafHarmonicMean.toFixed(2)}</p>{/if}
            </details>
          {/if}
        </li>
      {/each}
    </ol>
  {/if}
</section>
