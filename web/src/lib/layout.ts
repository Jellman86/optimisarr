export type PageWidth = 'fluid' | 'reading'

// Lists, grids and dashboards get more useful as the screen widens, so they use all of it. Library
// forms keep a readable measure: a row stretched across a 2560 px screen puts its label and its
// control a hand-span apart. Settings no longer does: capping its whole frame made it the one page
// narrower than the rest, so it uses the full width and keeps each row compact instead.
export function pageWidth(path: string): PageWidth {
  if (/^\/libraries\/(?:new|\d+\/configure)(?:\/|$)/.test(path)) return 'reading'
  return 'fluid'
}
