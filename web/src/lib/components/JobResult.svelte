<script lang="ts">
  // The result of one job, for the person deciding what to do next. A passed job says plainly
  // what replacing does and offers it; a job that missed a check says what can be done instead.
  // The one exception is a picture-quality score under target, which a person may accept by hand.
  import type { Job, VerificationCheck } from '../api'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import { isPictureQualityCheck, pictureQualityWasMeasured } from '../plain-checks'
  import DryRunUnlock from './DryRunUnlock.svelte'

  let { job, checks, dryRun, retentionDays, canRetryHigher, replacing, replaceBlocked, onreplace, onunlocked }: {
    job: Job
    checks: VerificationCheck[] | null
    /** Replacing is locked for every library. */
    dryRun: boolean
    /** Days a replaced original stays in Quarantine; 0 keeps it until approved, null when not known. */
    retentionDays: number | null
    /** The job's footer offers a retry at higher quality. */
    canRetryHigher: boolean
    replacing: boolean
    /** Another replacement is under way, so this one has to wait. */
    replaceBlocked: boolean
    onreplace: () => void
    onunlocked: () => void
  } = $props()

  const failed = $derived((checks ?? []).filter((check) => check.outcome === 'Failed'))
  const passed = $derived(job.status === 'ReadyToReplace' && job.verificationPassed === true && !job.finalizing)
  const missed = $derived(job.status === 'Failed' && failed.length > 0)
  // A lower target only helps when there was a score that fell short of it.
  const missedPicture = $derived(failed.some(pictureQualityWasMeasured))
  const unmeasuredPicture = $derived(failed.some((check) => isPictureQualityCheck(check.name) && !pictureQualityWasMeasured(check)))
  const undo = $derived(retentionDays === null
    ? i18n.m.jobResult.replace_undo
    : retentionDays === 0
      ? i18n.m.jobResult.replace_undo_kept
      : plural(retentionDays, i18n.m.jobResult.replace_undo_days_one, i18n.m.jobResult.replace_undo_days_other))
</script>

{#if passed}
  <section class="surface-sunken mt-4 px-4.5 py-4 text-label leading-relaxed text-ink-2" aria-labelledby="job-result-heading" data-job-result="passed">
    <h3 id="job-result-heading" class="text-title font-semibold text-ink">{i18n.m.jobResult.passed_title}</h3>
    <p>{i18n.m.jobResult.passed_intro}</p>
    <h4 class="mt-3.5 text-label font-semibold text-ink">{i18n.m.jobResult.replace_title}</h4>
    <ul class="mt-1.5 grid list-disc gap-1.5 pl-4.5">
      <li>{i18n.m.jobResult.replace_moves}</li>
      <li data-replace-undo>{undo}</li>
      <li>{i18n.m.jobResult.replace_space}</li>
    </ul>
    <div class="mt-3">
      <button class="btn btn-primary" onclick={onreplace} disabled={dryRun || replaceBlocked}
        aria-describedby={dryRun ? 'replace-locked-reason' : undefined}>
        {replacing ? i18n.m.queue.action_replacing_ellipsis : i18n.m.queue.action_replace_original}
      </button>
    </div>
    {#if dryRun}<div class="mt-3"><DryRunUnlock {onunlocked} /></div>{/if}
  </section>
{:else if missed}
  <section class="surface-sunken mt-4 px-4.5 py-4 text-label leading-relaxed text-ink-2" aria-labelledby="job-result-heading" data-job-result="missed">
    <h3 id="job-result-heading" class="text-title font-semibold text-ink">{plural(failed.length, i18n.m.jobResult.missed_title_one, i18n.m.jobResult.missed_title_other)}</h3>
    <p>{i18n.m.jobResult.missed_intro}</p>
    <h4 class="mt-3.5 text-label font-semibold text-ink">{i18n.m.jobResult.advice_title}</h4>
    <ul class="mt-1.5 grid list-disc gap-1.5 pl-4.5">
      {#if canRetryHigher}
        <li data-advice="retry"><strong>{i18n.m.jobResult.advice_retry_title}</strong> {t(i18n.m.jobResult.advice_retry_body, { action: i18n.m.queue.action_retry_higher_quality })}</li>
      {/if}
      {#if unmeasuredPicture}
        <li data-advice="unmeasured"><strong>{i18n.m.jobResult.advice_unmeasured_title}</strong> {i18n.m.jobResult.advice_unmeasured_body}</li>
      {/if}
      {#if missedPicture}
        <li data-advice="target">
          <strong>{i18n.m.jobResult.advice_target_title}</strong>
          {i18n.m.jobResult.advice_target_body}
          {#if job.libraryId}
            <a class="focus-ring flex hit-target w-fit items-center rounded font-medium text-accent underline" href={`#/libraries/${job.libraryId}/configure/video`}>{i18n.m.jobResult.advice_target_action}</a>
          {/if}
        </li>
      {/if}
      {#if job.belowTarget?.eligible}
        <li data-advice="accept"><strong>{i18n.m.jobResult.advice_accept_title}</strong> {t(i18n.m.jobResult.advice_accept_body, { action: i18n.m.queue.action_accept_anyway })}</li>
      {/if}
      <li data-advice="exclude"><strong>{i18n.m.jobResult.advice_exclude_title}</strong> {t(i18n.m.jobResult.advice_exclude_body, { action: i18n.m.queue.action_exclude })}</li>
    </ul>
    <p class="mt-3.5 text-ink-3">{job.belowTarget?.eligible ? i18n.m.jobResult.accept_rule : i18n.m.jobResult.no_replace_anyway}</p>
  </section>
{/if}
