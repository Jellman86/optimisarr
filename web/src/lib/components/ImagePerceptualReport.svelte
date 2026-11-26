<script lang="ts">
  import type { ImagePerceptualReport as Report } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import MeasureMetrics from './MeasureMetrics.svelte'
  import MeasureReport from './MeasureReport.svelte'

  let { report }: { report: Report | null } = $props()
  let measurement = $derived(report?.measurement)
  let measured = $derived(!report?.error && measurement && Number.isFinite(measurement.score)
    && Number.isFinite(measurement.maximumAlphaError) && measurement.width > 0 && measurement.height > 0)
  function number(value: number) { return Number(value.toFixed(1)).toString() }
</script>

{#if report}
  <MeasureReport title={i18n.m.image_quality.title}
    note={report.gateEnabled ? i18n.m.image_quality.gate_note : i18n.m.image_quality.report_only}>
    {#if measured && measurement}
      <MeasureMetrics metrics={[
        { label: 'SSIMULACRA2', value: number(measurement.score) },
        ...(report.gateEnabled && report.minimumScore != null ? [{ label: i18n.m.image_quality.minimum, value: number(report.minimumScore) }] : []),
      ]} />
      <p class="mt-3 text-caption text-ink-3">{t(i18n.m.image_quality.coverage, { width: measurement.width, height: measurement.height })}</p>
      {#if measurement.alpha}
        <p class="mt-2 text-caption text-ink-3">{t(i18n.m.image_quality.alpha, { difference: number(measurement.maximumAlphaError) })}</p>
      {/if}
    {:else}
      <p class="mt-3 break-words text-body text-ink-3">{report.error ?? i18n.m.image_quality.limits}</p>
    {/if}
  </MeasureReport>
{/if}
