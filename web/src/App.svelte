<script lang="ts">
  import { router } from './lib/stores/ui.svelte'
  import Sidebar from './lib/components/Sidebar.svelte'
  import Dashboard from './lib/pages/Dashboard.svelte'
  import Libraries from './lib/pages/Libraries.svelte'
  import Inventory from './lib/pages/Inventory.svelte'
  import Candidates from './lib/pages/Candidates.svelte'
  import Tools from './lib/pages/Tools.svelte'
  import Settings from './lib/pages/Settings.svelte'

  // Map the active route to its page component.
  let page = $derived.by(() => {
    const path = router.path
    if (path.startsWith('/libraries')) return Libraries
    if (path.startsWith('/inventory')) return Inventory
    if (path.startsWith('/candidates')) return Candidates
    if (path.startsWith('/tools')) return Tools
    if (path.startsWith('/settings')) return Settings
    return Dashboard
  })
</script>

<svelte:head>
  <title>Optimisarr</title>
</svelte:head>

<div class="flex h-screen bg-slate-50 text-slate-800 dark:bg-slate-950 dark:text-slate-200">
  <Sidebar />
  <main class="flex-1 overflow-y-auto p-6 lg:p-8">
    <div class="mx-auto max-w-6xl">
      {#key router.path}
        {@const Page = page}
        <Page />
      {/key}
    </div>
  </main>
</div>
