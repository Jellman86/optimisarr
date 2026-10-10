<script lang="ts">
  import type { ImagePerceptualReport as Report } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'

  let { report }: { report: Report | null } = $props()
  let measurement = $derived(report?.measurement)
  let measured = $derived(!report?.error && measurement && Number.isFinite(measurement.score)
    && Number.isFinite(measurement.maximumAlphaError) && measurement.width > 0 && measurement.height > 0)
  function number(value: number) { return Number(value.toPrecision(6)).toString() }
</script>

{#if report}
  <section class="min-w-0 rounded-lg border border-line bg-panel p-4" aria-label={i18n.m.image_quality.title}>
    <h3 class="text-sm font-semibold text-ink-2">{i18n.m.image_quality.title}</h3>
    {#if measured && measurement}
      <dl class="mt-4 grid min-w-0 grid-cols-1 gap-3 sm:grid-cols-2">
        <div class="min-w-0 rounded-md border border-line-soft bg-sunken p-3">
          <dt class="text-xs text-ink-3">SSIMULACRA2</dt>
          <dd class="mt-1 font-mono text-lg tabular-nums text-ink-2">{number(measurement.score)}</dd>
        </div>
        {#if report.gateEnabled && report.minimumScore != null}
          <div class="min-w-0 rounded-md border border-line-soft bg-sunken p-3">
            <dt class="text-xs text-ink-3">{i18n.m.image_quality.minimum}</dt>
            <dd class="mt-1 font-mono text-lg tabular-nums text-ink-2">{number(report.minimumScore)}</dd>
          </div>
        {/if}
      </dl>
      <p class="mt-3 text-xs text-ink-3">{t(i18n.m.image_quality.coverage, { width: measurement.width, height: measurement.height })}</p>
      {#if measurement.alpha}
        <p class="mt-2 text-xs text-ink-3">{t(i18n.m.image_quality.alpha, { difference: number(measurement.maximumAlphaError) })}</p>
      {/if}
    {:else}
      <p class="mt-3 break-words text-sm text-ink-3">{report.error ?? i18n.m.image_quality.limits}</p>
    {/if}
    <p class="mt-3 border-t border-line-soft pt-3 text-xs leading-relaxed text-ink-3">
      {report.gateEnabled ? i18n.m.image_quality.gate_note : i18n.m.image_quality.report_only}
    </p>
  </section>
{/if}
