<script lang="ts">
  // The sheet that lists every keyboard shortcut. Each one moves, finds, selects or opens
  // something to look at; the note at the foot says so, because the absence of a shortcut for
  // Replace or Delete is a decision, not an omission.
  import { i18n } from '../i18n/i18n.svelte'
  import { GO_SHORTCUTS, isApplePlatform, type GoKey } from '../keyboard'
  import DetailDialog from './DetailDialog.svelte'

  let { onclose }: { onclose: () => void } = $props()

  const commandKey = isApplePlatform(typeof navigator === 'undefined' ? undefined : navigator) ? '⌘' : 'Ctrl'

  type Row = { label: string; keys: string[]; chord?: boolean }

  let navigation = $derived<Row[]>(
    (Object.keys(GO_SHORTCUTS) as GoKey[]).map((key) => ({ label: pageName(key), keys: ['g', key], chord: true })),
  )
  let lists = $derived<Row[]>([
    { label: i18n.m.shortcuts.list_search, keys: ['/'] },
    { label: i18n.m.shortcuts.list_next, keys: ['j'] },
    { label: i18n.m.shortcuts.list_prev, keys: ['k'] },
    { label: i18n.m.shortcuts.list_select, keys: ['x'] },
    { label: i18n.m.shortcuts.list_open, keys: ['Enter'] },
  ])
  let general = $derived<Row[]>([
    { label: i18n.m.shortcuts.general_jump, keys: [commandKey, 'K'] },
    { label: i18n.m.shortcuts.general_help, keys: ['?'] },
    { label: i18n.m.shortcuts.general_close, keys: ['Esc'] },
  ])
  let groups = $derived([
    { id: 'shortcuts-navigation', title: i18n.m.shortcuts.group_navigation, rows: navigation },
    { id: 'shortcuts-lists', title: i18n.m.shortcuts.group_lists, rows: lists },
    { id: 'shortcuts-general', title: i18n.m.shortcuts.group_general, rows: general },
  ])

  function pageName(key: GoKey): string {
    const names: Record<GoKey, string> = {
      d: i18n.m.nav.dashboard,
      l: i18n.m.nav.libraries,
      i: i18n.m.nav.inventory,
      q: i18n.m.nav.queue,
      r: i18n.m.nav.quarantine,
      s: i18n.m.nav.schedule,
      ',': i18n.m.nav.settings,
    }
    return names[key]
  }
</script>

<DetailDialog id="shortcuts-dialog" title={i18n.m.shortcuts.title} titleId="shortcuts-title" closeLabel={i18n.m.shortcuts.close} {onclose}>
  {#snippet meta()}<span>{i18n.m.shortcuts.subtitle}</span>{/snippet}

  <div class="gap-10 sm:columns-2">
    {#each groups as group (group.id)}
      <section class="mb-6 break-inside-avoid last:mb-0" aria-labelledby={group.id}>
        <h3 id={group.id} class="mb-1 text-caption font-semibold text-ink-3">{group.title}</h3>
        <dl>
          {#each group.rows as row (row.label)}
            <div class="shortcut-row flex items-center justify-between gap-4 py-2">
              <dt class="min-w-0 text-label text-ink">{row.label}</dt>
              <dd class="flex shrink-0 items-center gap-1">
                {#each row.keys as key, index (index)}
                  {#if index > 0 && row.chord}<span class="text-micro text-ink-3">{i18n.m.shortcuts.key_then}</span>{/if}
                  <kbd class="kbd">{key}</kbd>
                {/each}
              </dd>
            </div>
          {/each}
        </dl>
      </section>
    {/each}
  </div>

  {#snippet footer()}<p class="text-caption text-ink-2" data-shortcuts-safety>{i18n.m.shortcuts.safety_note}</p>{/snippet}
</DetailDialog>
