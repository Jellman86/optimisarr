import type { Page } from '@playwright/test'

// What the layout audits measure on a page: anything wider than the window or than main, cards
// that escape main or clip their children, and switches that stop short of their row's edge.
// Shared by the route-by-route layout audit and the pseudo-locale (long text) audit.
export type Layout = {
  documentOverflow: number
  mainOverflow: number
  escapedCards: string[]
  clippedCards: string[]
  narrowRows: string[]
}

export async function measure(page: Page): Promise<Layout> {
  return page.locator('main').evaluate(main => {
    // Fractional grid rounding can leave 1–3px in scrollWidth without clipping a pixel.
    const epsilon = 4
    const mainBox = main.getBoundingClientRect()
    const cards = [...new Set(main.querySelectorAll<HTMLElement>('.card, [data-config-section]'))]
    const visible = (element: HTMLElement) => {
      const box = element.getBoundingClientRect()
      return box.width > 0 && box.height > 0 && getComputedStyle(element).visibility !== 'hidden'
    }
    const escapedCards = cards.filter(visible).filter(card => {
      const box = card.getBoundingClientRect()
      return box.left < mainBox.left - epsilon || box.right > mainBox.right + epsilon
    }).map(card => card.id || card.className.split(' ').slice(0, 3).join('.'))
    const clippedCards = cards.filter(visible).filter(card => {
      const overflow = getComputedStyle(card).overflowX
      return !['auto', 'scroll'].includes(overflow) && card.scrollWidth > card.clientWidth + epsilon
    }).flatMap(card => {
      const box = card.getBoundingClientRect()
      const offenders = [...card.querySelectorAll<HTMLElement>('*')].filter(visible).filter(child => {
        const childBox = child.getBoundingClientRect()
        return childBox.right > box.right + epsilon || childBox.left < box.left - epsilon
      }).slice(0, 3).map(child => child.id || `${child.tagName.toLowerCase()}.${child.className.toString().split(' ').slice(0, 2).join('.')}`)
      // scrollWidth can include browser-specific fractional text/paint extents. A child
      // actually crossing the card edge distinguishes clipping from those false positives.
      return offenders.length
        ? [`${card.id || card.className.split(' ').slice(0, 3).join('.')} (+${card.scrollWidth - card.clientWidth}px; ${offenders.join(', ')})`]
        : []
    })

    // Text and numeric inputs are intentionally capped for readable line length. Toggle rows
    // and section dividers are not: the switch should align with its enclosing card's right edge.
    const narrowRows: string[] = []
    for (const id of ['global-workload', 'global-replacement', 'global-notifications', 'global-media-servers', 'global-download-managers']) {
      const section = main.querySelector<HTMLElement>(`#${id}`)
      if (!section) continue
      for (const input of section.querySelectorAll<HTMLInputElement>('label input[type="checkbox"]')) {
        const label = input.closest<HTMLElement>('label')
        if (!label || !visible(label)) continue
        // A switch in a setting row sits at the end of that row's control column, which follows
        // its words; a switch in a connection form sits at the end of the form's well.
        const row = label.closest<HTMLElement>('[data-setting-row]')
        const parent = row
          ? row.querySelector<HTMLElement>('.setting-control')
          : label.closest<HTMLElement>('.surface-sunken')
        if (!parent) continue
        const box = parent.getBoundingClientRect()
        if (!row && box.width < 700) continue
        const expectedRight = box.right - Number.parseFloat(getComputedStyle(parent).paddingRight || '0')
        const gap = expectedRight - label.getBoundingClientRect().right
        const name = (label.textContent?.trim() || input.getAttribute('aria-label') || '').replace(/\s+/g, ' ').slice(0, 60)
        if (gap > 24) narrowRows.push(`${id}: ${name} (${Math.round(gap)}px unused)`)
      }
    }
    return {
      documentOverflow: Math.max(0, document.documentElement.scrollWidth - window.innerWidth),
      mainOverflow: Math.max(0, main.scrollWidth - main.clientWidth),
      escapedCards,
      clippedCards,
      narrowRows,
    }
  })
}

export type ControlCollisions = {
  /** Buttons whose own label runs past their edge (an ellipsis on purpose does not count). */
  clippedControls: string[]
  /** Pairs of visible controls drawn on top of each other. */
  overlappingControls: string[]
  /** Fields crushed too narrow to show a value, usually by a neighbour that will not wrap. */
  squeezedFields: string[]
  /** Column headers squeezed until their words break letter by letter into a tall stack. */
  crushedHeaders: string[]
}

// Primary controls only: buttons, links styled as buttons, tabs and form fields. Information tips
// and row checkboxes are left out because their touch targets overhang their neighbours by design.
export async function measureControls(page: Page): Promise<ControlCollisions> {
  return page.locator('body').evaluate(body => {
    const epsilon = 2
    const describe = (element: Element) => {
      const text = (element.textContent || element.getAttribute('aria-label') || '').replace(/\s+/g, ' ').trim().slice(0, 50)
      return `${element.tagName.toLowerCase()}${element.id ? `#${element.id}` : ''} "${text}"`
    }
    const shown = (element: HTMLElement) => {
      if (element.closest('[inert], [hidden], [aria-hidden="true"]')) return false
      const box = element.getBoundingClientRect()
      if (box.width < 1 || box.height < 1) return false
      // Also false inside a closed <details>, which still reports a box for its content.
      return element.checkVisibility({ opacityProperty: true, visibilityProperty: true, contentVisibilityAuto: true })
    }
    // A sticky or fixed bar floats over the page by design and the page's controls scroll beneath
    // it, so only controls in the same layer can collide.
    const layer = (element: HTMLElement) => {
      for (let node = element.parentElement; node; node = node.parentElement) {
        if (['sticky', 'fixed'].includes(getComputedStyle(node).position)) return node
      }
      return null
    }
    const controls = [...body.querySelectorAll<HTMLElement>('main .btn, main [role="tab"], main select, main input.input, main textarea')]
      .filter(shown)
    const clippedControls = controls
      .filter(control => control.matches('button, a'))
      .filter(control => getComputedStyle(control).textOverflow !== 'ellipsis' && control.scrollWidth > control.clientWidth + epsilon)
      .map(control => `${describe(control)} (+${control.scrollWidth - control.clientWidth}px)`)
    const overlappingControls: string[] = []
    const boxes = controls.map(control => control.getBoundingClientRect())
    const layers = controls.map(layer)
    for (let a = 0; a < controls.length; a++) {
      for (let b = a + 1; b < controls.length; b++) {
        if (layers[a] !== layers[b] || controls[a].contains(controls[b]) || controls[b].contains(controls[a])) continue
        const x = Math.min(boxes[a].right, boxes[b].right) - Math.max(boxes[a].left, boxes[b].left)
        const y = Math.min(boxes[a].bottom, boxes[b].bottom) - Math.max(boxes[a].top, boxes[b].top)
        if (x > epsilon && y > epsilon) overlappingControls.push(`${describe(controls[a])} × ${describe(controls[b])}`)
      }
    }
    const squeezedFields = controls
      .filter(control => control.matches('select, input, textarea'))
      .filter(control => control.getBoundingClientRect().width < 48)
      .map(control => `${describe(control)} (${Math.round(control.getBoundingClientRect().width)}px wide)`)
    const crushedHeaders = [...body.querySelectorAll<HTMLElement>('main th')]
      .filter(header => header.checkVisibility())
      .filter(header => {
        const style = getComputedStyle(header)
        const lineHeight = Number.parseFloat(style.lineHeight) || Number.parseFloat(style.fontSize) * 1.5
        const range = document.createRange()
        range.selectNodeContents(header)
        return range.getBoundingClientRect().height > lineHeight * 4.5
      })
      .map(header => {
        const range = document.createRange()
        range.selectNodeContents(header)
        return `${describe(header)} (${Math.round(range.getBoundingClientRect().height)}px of text)`
      })
    return { clippedControls, overlappingControls, squeezedFields, crushedHeaders }
  })
}
