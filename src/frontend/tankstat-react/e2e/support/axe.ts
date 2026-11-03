import { AxeBuilder } from '@axe-core/playwright'
import type { Page } from '@playwright/test'

/** The axe violations of the page, or of the part `include` selects, one readable line each. */
export async function violations(page: Page, include?: string) {
  const builder = new AxeBuilder({ page })
  if (include) builder.include(include)
  const { violations } = await builder.analyze()
  // Every node named, with the start of its markup, so a failure says what is wrong without a report.
  // A contrast failure names its colours and ratio too (`data` of the check), so it can be judged from the line.
  return violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => `${n.target.join(' ')} <${n.html.slice(0, 120)}> ${n.any[0]?.data ? JSON.stringify(n.any[0].data) : ''}`).join('; ')}`)
}
