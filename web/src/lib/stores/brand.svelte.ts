import { parseBrandStyle, type BrandStyle } from '../brand-style'

function createBrandPreference() {
  let stored: string | null = null
  try { stored = localStorage.getItem('optimisarr.brand') } catch { /* Private browsers may deny storage. */ }
  let style = $state(parseBrandStyle(stored))
  return {
    get style() { return style },
    set(value: BrandStyle) {
      style = parseBrandStyle(value)
      try { localStorage.setItem('optimisarr.brand', style) } catch { /* Keep the session preference. */ }
    },
  }
}
export const brand = createBrandPreference()
