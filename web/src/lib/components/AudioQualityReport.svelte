<script lang="ts">
  import type { AudioQualityReport as Report } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import MeasureMetrics from './MeasureMetrics.svelte'
  import MeasureReport from './MeasureReport.svelte'

  let { report, nested = false }: { report: Report | null; nested?: boolean } = $props()
  let assessment = $derived(report?.evidence?.assessment)
  let measured = $derived(assessment?.measured === true && Array.isArray(assessment.windows) && assessment.windows.length > 0)
  let channels = $derived(measured ? assessment!.windows[0]?.distances?.channelDistances?.length ?? 0 : 0)
  function distance(channel: number) {
    const values = assessment?.windows.map(sample => sample.distances.channelDistances[channel]) ?? []
    return values.length && values.every(value => Number.isFinite(value)) ? Number(Math.max(...values).toPrecision(3)).toString() : '—'
  }
</script>

{#if report}
  <MeasureReport {nested} title={i18n.m.audio_quality.title}
    hintLabel={t(i18n.m.common.about_information, { label: i18n.m.audio_quality.title })}
    hint={report.gateEnabled ? i18n.m.audio_quality.gate_hint : i18n.m.audio_quality.hint}
    badge={report.gateEnabled
      ? report.gatePassed === true ? { text: i18n.m.audio_quality.passed, tone: 'ok' } : { text: i18n.m.audio_quality.blocked, tone: 'bad' }
      : { text: measured ? i18n.m.audio_quality.badge : i18n.m.audio_quality.unavailable, tone: 'muted' }}
    note={report.gateEnabled ? i18n.m.audio_quality.gate_note : i18n.m.audio_quality.note}>
    {#if report.gateEnabled && report.maximumDistance != null}
      <p class="mt-3 text-body text-ink-2">{t(i18n.m.audio_quality.gate_limit, { limit: report.maximumDistance })}</p>
    {/if}
    {#if measured && assessment}
      <p class="mt-2 text-caption text-ink-3">Zimtohrli · {report.measurementLocation === 'Worker' ? i18n.m.queue.lane_workers : i18n.m.dashboard.this_server}</p>
      <MeasureMetrics metrics={Array.from({ length: Math.min(channels, 2) }, (_, channel) => ({
        label: t(i18n.m.audio_quality.channel, { channel: channel + 1 }), value: distance(channel) }))} />
      <p class="mt-3 text-caption text-ink-3">{t(i18n.m.audio_quality.coverage, { seconds: Math.round(assessment.coveredSeconds * 10) / 10, windows: assessment.windows.length })}</p>
      <p class="mt-2 text-caption leading-relaxed text-ink-3">{i18n.m.audio_quality.distance_note}</p>
    {:else}
      <p class="mt-3 break-words text-body text-ink-3">{report.unavailableReason ?? i18n.m.audio_quality.unavailable}</p>
    {/if}
  </MeasureReport>
{/if}
