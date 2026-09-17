import { brand } from './brand.svelte'
import { brandAsset } from '../brand-style'
import { theme } from './ui.svelte'
import { activity } from './activity.svelte'

function createFavicon() {
  let started = false
  function start() {
    if (started || typeof document === 'undefined') return
    started = true
    // The tab reports activity with the selected design in the current theme. No animation or PNG serialization loop.
    $effect.root(() => {
      $effect(() => {
        for (const link of document.querySelectorAll<HTMLLinkElement>('link[rel~="icon"]')) {
          link.href = brandAsset(brand.style, theme.isDark, activity.brandWorking, true)
          link.sizes.value = '64x64'
        }
      })
    })
  }
  return { start }
}
export const favicon = createFavicon()
