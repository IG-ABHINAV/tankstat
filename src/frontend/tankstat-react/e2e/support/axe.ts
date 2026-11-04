import { AxeBuilder } from '@axe-core/playwright'
import type { Page } from '@playwright/test'

/**
 * The axe violations of the page, or of the part `include` selects, one readable line each. A part is scanned once it is fully drawn:
 * a dialog or popover fades in, and axe measures a node it cannot settle as "incomplete", not as a violation, so a scan mid fade-in
 * passes silently (#235). The wait is for every element `include` selects and its ancestors to be at full opacity.
 */
export async function violations(page: Page, include?: string) {
  if (include)
    await page.waitForFunction((selector) => {
      const parts = [...document.querySelectorAll(selector)]
      return parts.length > 0 && parts.every((part) => {
        for (let node: Element | null = part; node; node = node.parentElement) if (getComputedStyle(node).opacity !== '1') return false
        return true
      })
    }, include)
  const builder = new AxeBuilder({ page })
  if (include) builder.include(include)
  const { violations } = await builder.analyze()
  // Every node named, with the start of its markup, so a failure says what is wrong without a report.
  // A contrast failure names its colours and ratio too (`data` of the check), so it can be judged from the line.
  return violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => `${n.target.join(' ')} <${n.html.slice(0, 120)}> ${n.any[0]?.data ? JSON.stringify(n.any[0].data) : ''}`).join('; ')}`)
}
