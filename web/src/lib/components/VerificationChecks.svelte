<script lang="ts">
  // The pass/fail verification gate list, shared by the Queue job detail, its earlier attempts,
  // the preview and the Quarantine compare-to-approve panel so the report reads the same
  // everywhere. Each check is named in plain words; the name the code uses stays as a tooltip,
  // because it is the one a log or a bug report will quote.
  import type { VerificationCheck } from '../api'
  import { i18n } from '../i18n/i18n.svelte'
  import { plainCheckName } from '../plain-checks'
  import EvidenceList from './EvidenceList.svelte'

  let { checks }: { checks: VerificationCheck[] } = $props()
</script>

<EvidenceList items={checks.map((check) => ({
  id: check.name,
  passed: check.outcome === 'Passed',
  label: plainCheckName(check.name, i18n.m.checkNames),
  detail: check.detail,
}))} />
