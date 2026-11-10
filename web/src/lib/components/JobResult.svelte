<script lang="ts">
  // The result of one job, for the person deciding what to do next. A passed job says plainly
  // what replacing does and offers it; a job that missed a check says what can be done instead.
  // There is no "replace anyway": a file that missed a check never replaces an original.
  import type { Job, VerificationCheck } from '../api'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import { isPictureQualityCheck } from '../plain-checks'
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
  const missedPicture = $derived(failed.some((check) => isPictureQualityCheck(check.name)))
  const undo = $derived(retentionDays === null
    ? i18n.m.jobResult.replace_undo
    : retentionDays === 0
      ? i18n.m.jobResult.replace_undo_kept
      : plural(retentionDays, i18n.m.jobResult.replace_undo_days_one, i18n.m.jobResult.replace_undo_days_other))
</script>

{#if passed}
  <section class="job-result" aria-labelledby="job-result-heading" data-job-result="passed">
    <h3 id="job-result-heading">{i18n.m.jobResult.passed_title}</h3>
    <p>{i18n.m.jobResult.passed_intro}</p>
    <h4>{i18n.m.jobResult.replace_title}</h4>
    <ul>
      <li>{i18n.m.jobResult.replace_moves}</li>
      <li data-replace-undo>{undo}</li>
      <li>{i18n.m.jobResult.replace_space}</li>
    </ul>
    <div class="mt-3">
      <button class="btn btn-primary min-h-11" onclick={onreplace} disabled={dryRun || replaceBlocked}
        aria-describedby={dryRun ? 'replace-locked-reason' : undefined}>
        {replacing ? i18n.m.queue.action_replacing_ellipsis : i18n.m.queue.action_replace_original}
      </button>
    </div>
    {#if dryRun}<div class="mt-3"><DryRunUnlock {onunlocked} /></div>{/if}
  </section>
{:else if missed}
  <section class="job-result" aria-labelledby="job-result-heading" data-job-result="missed">
    <h3 id="job-result-heading">{plural(failed.length, i18n.m.jobResult.missed_title_one, i18n.m.jobResult.missed_title_other)}</h3>
    <p>{i18n.m.jobResult.missed_intro}</p>
    <h4>{i18n.m.jobResult.advice_title}</h4>
    <ul>
      {#if canRetryHigher}
        <li data-advice="retry"><strong>{i18n.m.jobResult.advice_retry_title}</strong> {t(i18n.m.jobResult.advice_retry_body, { action: i18n.m.queue.action_retry_higher_quality })}</li>
      {/if}
      {#if missedPicture}
        <li data-advice="target">
          <strong>{i18n.m.jobResult.advice_target_title}</strong>
          {i18n.m.jobResult.advice_target_body}
          {#if job.libraryId}
            <a class="focus-ring flex min-h-11 w-fit items-center rounded font-medium text-accent underline" href={`#/libraries/${job.libraryId}/configure/video`}>{i18n.m.jobResult.advice_target_action}</a>
          {/if}
        </li>
      {/if}
      <li data-advice="exclude"><strong>{i18n.m.jobResult.advice_exclude_title}</strong> {t(i18n.m.jobResult.advice_exclude_body, { action: i18n.m.queue.action_exclude })}</li>
    </ul>
    <p class="job-result-rule">{i18n.m.jobResult.no_replace_anyway}</p>
  </section>
{/if}

<style>
  .job-result {
    margin-top: 1rem;
    border-radius: 0.75rem;
    background: var(--sunken);
    padding: 1rem 1.125rem;
    font-size: 0.8125rem;
    line-height: 1.65;
    color: var(--ink-2);
  }
  h3 { font-size: 0.9375rem; font-weight: 600; color: var(--ink); }
  h4 { margin-top: 0.875rem; font-size: 0.8125rem; font-weight: 600; color: var(--ink); }
  ul { margin-top: 0.375rem; display: grid; gap: 0.375rem; padding-left: 1.125rem; list-style: disc; }
  .job-result-rule { margin-top: 0.875rem; color: var(--ink-3); }
</style>
