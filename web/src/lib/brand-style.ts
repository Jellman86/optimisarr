export type BrandStyle = 'stellar' | 'precession'

export function parseBrandStyle(value: string | null): BrandStyle {
  return value === 'stellar' ? 'stellar' : 'precession'
}

export function brandAsset(style: BrandStyle, dark: boolean, working: boolean, favicon = false) {
  const directory = style === 'stellar' ? '/brand/stellar' : '/brand'
  return `${directory}/${favicon ? 'favicon-' : ''}${dark ? 'dark' : 'light'}-${working ? 'excited' : 'steady'}.${favicon ? 'png' : 'webp'}`
}
