<script lang="ts">
  import type { AudioQualityReport as Report } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import InfoTip from './InfoTip.svelte'

  let { report }: { report: Report | null } = $props()
  let assessment = $derived(report?.evidence?.assessment)
  let measured = $derived(assessment?.measured === true && Array.isArray(assessment.windows) && assessment.windows.length > 0)
  let channels = $derived(measured ? assessment!.windows[0]?.distances?.channelDistances?.length ?? 0 : 0)
  function distance(channel: number) {
    const values = assessment?.windows.map(sample => sample.distances.channelDistances[channel]) ?? []
    return values.length && values.every(value => Number.isFinite(value)) ? Number(Math.max(...values).toPrecision(6)).toString() : '—'
  }
</script>

{#if report}
  <section class="mt-5 min-w-0 rounded-lg border border-line bg-panel p-4" aria-label={i18n.m.audio_quality.title}>
    <div class="flex min-w-0 flex-wrap items-center justify-between gap-2">
      <div class="flex min-w-0 items-center gap-2">
        <h3 class="text-sm font-semibold text-ink-2">{i18n.m.audio_quality.title}</h3>
        <InfoTip label={t(i18n.m.common.about_information, { label: i18n.m.audio_quality.title })} text={report.gateEnabled ? i18n.m.audio_quality.gate_hint : i18n.m.audio_quality.hint} />
      </div>
      <span class="badge" class:tone-ok={report.gateEnabled && report.gatePassed === true}
        class:tone-bad={report.gateEnabled && report.gatePassed === false}
        class:tone-muted={!report.gateEnabled}>
        {report.gateEnabled ? (report.gatePassed ? i18n.m.audio_quality.passed : i18n.m.audio_quality.blocked)
          : measured ? i18n.m.audio_quality.badge : i18n.m.audio_quality.unavailable}
      </span>
    </div>
    {#if report.gateEnabled && report.maximumDistance != null}
      <p class="mt-3 text-sm text-ink-2">{t(i18n.m.audio_quality.gate_limit, { limit: report.maximumDistance })}</p>
    {/if}
    {#if measured && assessment}
      <p class="mt-2 text-xs text-ink-3">Zimtohrli · {report.measurementLocation === 'Worker' ? i18n.m.queue.lane_workers : i18n.m.dashboard.this_server}</p>
      <dl class="mt-4 grid min-w-0 grid-cols-1 gap-3 sm:grid-cols-2">
        {#each Array.from({ length: Math.min(channels, 2) }) as _, channel}
          <div class="min-w-0 rounded-md border border-line-soft bg-sunken p-3">
            <dt class="text-xs text-ink-3">{t(i18n.m.audio_quality.channel, { channel: channel + 1 })}</dt>
            <dd class="mt-1 font-mono text-lg tabular-nums text-ink-2">{distance(channel)}</dd>
          </div>
        {/each}
      </dl>
      <p class="mt-3 text-xs text-ink-3">{t(i18n.m.audio_quality.coverage, { seconds: Math.round(assessment.coveredSeconds * 10) / 10, windows: assessment.windows.length })}</p>
      <p class="mt-2 text-xs leading-relaxed text-ink-3">{i18n.m.audio_quality.distance_note}</p>
    {:else}
      <p class="mt-3 break-words text-sm text-ink-3">{report.unavailableReason ?? i18n.m.audio_quality.unavailable}</p>
    {/if}
    <p class="mt-3 border-t border-line-soft pt-3 text-xs leading-relaxed text-ink-3">{report.gateEnabled ? i18n.m.audio_quality.gate_note : i18n.m.audio_quality.note}</p>
  </section>
{/if}
