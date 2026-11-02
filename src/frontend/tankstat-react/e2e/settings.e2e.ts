import { expect, test } from '@playwright/test'
import { pageLoaded, signIn } from './support/app.ts'
import { expectSoundLayout } from './support/layout.ts'

test.beforeEach(async ({ page }) => signIn(page))

// Every group of the Server settings page (`SettingsGroup`), by its tab's address and name. The page shows the first tab for a name it
// does not know, so each test checks that its own tab is the one open.
const TABS = [
  ['auth', 'Sign-in'],
  ['smtp', 'E-mail'],
  ['defaults', 'Defaults'],
  ['notifications', 'Notifications'],
  ['sync', 'Offline sync'],
  ['photo_reading', 'Photo reading'],
  ['backups', 'Backups'],
  ['logging', 'Logging'],
  ['telemetry', 'Telemetry'],
] as const

for (const [tab, name] of TABS) {
  test(`the ${name} settings lay out with every section open`, async ({ page }) => {
    await page.goto(`/admin/settings?tab=${tab}`)
    await pageLoaded(page)
    await expect(page.getByRole('tab', { selected: true })).toHaveText(name)
    await expect(page.getByRole('tabpanel')).toBeVisible()
    const closed = page.locator('.MuiAccordionSummary-root[aria-expanded="false"]')
    // Clicked in the page, not by Playwright's pointer: on a broken layout a value lying over the next section would block a real click
    // until the test times out, instead of failing with the lines below.
    while ((await closed.count()) > 0) await closed.first().evaluate((summary) => (summary as HTMLElement).click())
    // Measured once every section has finished opening.
    await expect(page.locator('.MuiAccordion-root > .MuiCollapse-root:not(.MuiCollapse-entered)')).toHaveCount(0)
    await expectSoundLayout(page)
  })
}
