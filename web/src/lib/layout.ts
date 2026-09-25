export type PageWidth = 'fluid' | 'reading'

// Lists, grids and dashboards get more useful as the screen widens, so they use all of it. Forms
// do not: a settings row stretched across a 2560 px screen puts its label and its control a
// hand-span apart, so they keep a readable measure.
export function pageWidth(path: string): PageWidth {
  if (path.startsWith('/settings') || path.startsWith('/tools')) return 'reading'
  if (/^\/libraries\/(?:new|\d+\/configure)(?:\/|$)/.test(path)) return 'reading'
  return 'fluid'
}
