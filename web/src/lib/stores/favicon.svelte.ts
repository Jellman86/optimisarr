import { theme } from './ui.svelte'
import { activity } from './activity.svelte'

function createFavicon() {
  let started = false
  function start() {
    if (started || typeof document === 'undefined') return
    started = true
    // The tab reports activity with a twisted still in the current theme. No animation or PNG serialization loop.
    $effect.root(() => {
      $effect(() => {
        for (const link of document.querySelectorAll<HTMLLinkElement>('link[rel~="icon"]')) {
          link.href = `/brand/favicon-${theme.isDark ? 'dark' : 'light'}-${activity.brandWorking ? 'excited' : 'steady'}.png`
          link.sizes.value = '64x64'
        }
      })
    })
  }
  return { start }
}
export const favicon = createFavicon()
