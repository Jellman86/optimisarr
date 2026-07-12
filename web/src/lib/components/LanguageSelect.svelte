<script lang="ts">
  import { i18n, AVAILABLE_LOCALES } from '../i18n/i18n.svelte'

  function onChange(event: Event & { currentTarget: HTMLSelectElement }) {
    i18n.set(event.currentTarget.value)
  }
</script>

<label class="flex items-center gap-2">
  <span class="sr-only">{i18n.m.language.label}</span>
  <svg class="h-4 w-4 flex-shrink-0 text-slate-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2" aria-hidden="true">
    <path stroke-linecap="round" stroke-linejoin="round" d="M12 3a9 9 0 100 18 9 9 0 000-18zM3.6 9h16.8M3.6 15h16.8M12 3a13 13 0 000 18M12 3a13 13 0 010 18" />
  </svg>
  <select
    class="input h-8 w-full py-0 text-xs"
    aria-label={i18n.m.language.label}
    value={i18n.locale}
    onchange={onChange}
  >
    {#each AVAILABLE_LOCALES as loc (loc.code)}
      <option value={loc.code}>{loc.name}</option>
    {/each}
  </select>
</label>
