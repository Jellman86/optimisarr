import { api } from '../api'
import { parseBrandStyle, type BrandStyle } from '../brand-style'

// The server holds the brand so every browser, and each paired sidecar's own page, shows the same
// mark. This browser's copy only avoids a flash of the default before the server answers.
function createBrandPreference() {
  let stored: string | null = null
  try { stored = localStorage.getItem('optimisarr.brand') } catch { /* Private browsers may deny storage. */ }
  let style = $state(parseBrandStyle(stored))
  let saveFailed = $state(false)
  let synced = false
  function apply(value: BrandStyle) {
    style = value
    try { localStorage.setItem('optimisarr.brand', value) } catch { /* Keep the session preference. */ }
  }
  return {
    get style() { return style },
    get saveFailed() { return saveFailed },
    async set(value: BrandStyle) {
      apply(parseBrandStyle(value))
      try {
        await api.saveAppearance(style)
        saveFailed = false
      } catch {
        saveFailed = true
      }
    },
    async sync() {
      if (synced) return
      synced = true
      try {
        const { brandStyle } = await api.appearance()
        // Only a name this build knows: an older server, or a mocked one, must not reset the mark.
        if (brandStyle === 'precession' || brandStyle === 'stellar') apply(brandStyle)
      } catch { synced = false }
    },
  }
}
export const brand = createBrandPreference()
