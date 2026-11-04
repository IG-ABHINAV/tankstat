import { expect, type Page } from '@playwright/test'

/**
 * What breaks a page's layout, as readable lines (none when it is fine):
 * - the document is wider than the viewport (the page scrolls sideways);
 * - a value of a label/value list (`dd`) ends past its panel;
 * - a value's help button covers the value;
 * - a dialog's frame scrolls as a whole, not only its content (#249);
 * - on a touch screen (`(pointer: coarse)`), a control smaller than 44 px (frontend rules, "Touch targets"): buttons, tabs and icon
 *   links 44 × 44, a text link 44 px tall. Exempt are a link inline in a sentence (its parent has text of its own, WCAG 2.5.8) and
 *   whatever is out of sight (the skip link until it is focused); a stretched link (an absolutely positioned `::after`) counts by the
 *   box its `::after` covers.
 *
 * Measure before a full-page screenshot: under phone emulation the screenshot drops the touch emulation.
 */
export async function layoutProblems(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const problems: string[] = []
    const doc = document.documentElement
    if (doc.scrollWidth > doc.clientWidth) problems.push(`page ${doc.scrollWidth}px wide in a ${doc.clientWidth}px viewport`)

    for (const dd of document.querySelectorAll('dd')) {
      const text = document.createTreeWalker(dd, NodeFilter.SHOW_TEXT, (node) =>
        node.textContent?.trim() ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP,
      ).nextNode()
      if (!text || !dd.checkVisibility({ visibilityProperty: true })) continue
      const range = document.createRange()
      range.selectNodeContents(text)
      const value = range.getBoundingClientRect()
      const panel = (dd.closest('.MuiPaper-root, .MuiAccordion-root') ?? document.body).getBoundingClientRect()
      const label = dd.previousElementSibling?.textContent?.trim() || '?'
      if (value.right > panel.right + 0.5) problems.push(`"${label}": value ends ${Math.round(value.right - panel.right)}px past its panel`)
      const help = dd.querySelector('button')?.getBoundingClientRect()
      if (help && value.right > help.left + 1 && value.left < help.right && value.bottom > help.top && value.top < help.bottom)
        problems.push(`"${label}": help button covers the value`)
    }

    // A dialog scrolls in its content, never as a whole (#249): something laid out against its frame (an absolutely positioned hidden
    // input whose containing block is the frame) stretches the frame past its title and content, so it scrolls too, with a second
    // scrollbar and blank space under the content. A popover's paper is its own scroll container and is not checked.
    for (const paper of document.querySelectorAll<HTMLElement>('.MuiDialog-paper')) {
      if (!paper.checkVisibility({ visibilityProperty: true }) || paper.scrollHeight <= paper.clientHeight + 1) continue
      const title = paper.getAttribute('aria-labelledby')
      const name = (title && document.getElementById(title)?.textContent?.trim()) || paper.getAttribute('aria-label') || 'dialog'
      problems.push(`dialog "${name}": its frame scrolls ${paper.scrollHeight}/${paper.clientHeight}`)
    }

    if (!matchMedia('(pointer: coarse)').matches) return problems
    const ownText = (el: Element | null) => !!el && [...el.childNodes].some((n) => n.nodeType === Node.TEXT_NODE && n.textContent?.trim())
    /** The box a link's absolutely positioned `::after` covers: the nearest positioned element from the link up. */
    const stretched = (el: HTMLElement) => {
      const after = getComputedStyle(el, '::after')
      if (after.content === 'none' || after.position !== 'absolute') return null
      for (let box: HTMLElement | null = el; box; box = box.parentElement) if (getComputedStyle(box).position !== 'static') return box
      return null
    }
    for (const el of document.querySelectorAll<HTMLElement>('button, a[href], [role="button"], [role="tab"]')) {
      if (!el.checkVisibility({ visibilityProperty: true })) continue
      const textLink = el.matches('a[href]:not(.MuiButtonBase-root)') && !!el.textContent?.trim()
      if (textLink && ownText(el.parentElement)) continue // inline in a sentence
      const r = (stretched(el) ?? el).getBoundingClientRect()
      if (r.width === 0 || r.height === 0) continue
      if (r.bottom <= 0 || r.right <= 0 || r.top >= doc.scrollHeight || r.left >= doc.clientWidth) continue // out of sight
      // Half a pixel of slack: on a phone screen (2.625 device pixels to a CSS pixel) a box moving into place snaps to device pixels.
      if (r.height < 43.5 || (!textLink && r.width < 43.5)) {
        const name = (el.getAttribute('aria-label') || el.textContent || el.tagName).trim().slice(0, 40)
        problems.push(`touch target ${Math.round(r.width)}x${Math.round(r.height)}: ${name}`)
      }
    }
    return problems
  })
}

/**
 * Asserts a page has no layout problem, listing every one it has. Measured again until it holds (or the expect timeout ends): the page
 * comes in with a short move (App.tsx), and under load a box was once measured mid-way, a fraction of a pixel short.
 */
export async function expectSoundLayout(page: Page) {
  await expect.poll(() => layoutProblems(page)).toEqual([])
}
