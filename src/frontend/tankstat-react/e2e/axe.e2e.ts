import { expect, test } from './support/test.ts'
import { addVehicle, graphql, pageLoaded, signIn } from './support/app.ts'
import { violations } from './support/axe.ts'

// axe in a real browser: what jsdom cannot judge (the colour contrast of the real styles, what is hidden after layout). The jsdom scans
// in tests/app/Accessibility*.integration.test.tsx stay; these add the pages as they are drawn, at both sizes.

test.beforeEach(async ({ page }) => signIn(page))

for (const [name, path] of [
  ['the home page', '/'],
  ['the Overview', '/admin'],
  ['the Server settings', '/admin/settings'],
  ['the Backups page', '/admin/backups'],
  ['the account page', '/account'],
  ['the People page', '/admin/people'],
] as const) {
  test(`${name} has no axe violations`, async ({ page }) => {
    await page.goto(path)
    await pageLoaded(page)
    expect(await violations(page)).toEqual([])
  })
}

test('a vehicle page and its add refuelling dialog have no axe violations', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await page.goto(`/vehicles/${vehicle.id}?tab=refuelings`)
  await pageLoaded(page)
  expect(await violations(page)).toEqual([])
  await page.getByRole('button', { name: 'Add refuelling' }).first().click()
  const dialog = page.getByRole('dialog', { name: 'Add refuelling' })
  await expect(dialog).toBeVisible()
  expect(await violations(page, '[role="dialog"]')).toEqual([])
})

test('a vehicle\'s dashboard, shown and being arranged, has no axe violations', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await page.goto(`/vehicles/${vehicle.id}`)
  await pageLoaded(page)
  await expect(page.getByRole('heading', { name: 'Monthly costs' })).toBeVisible()
  expect(await violations(page)).toEqual([])
  await page.getByRole('button', { name: 'Customize' }).click()
  await expect(page.getByRole('button', { name: 'Save dashboard' })).toBeVisible()
  // Not over a button: Save dashboard takes Customize's place, and a hovered primary button falls short of the contrast (#235).
  await page.mouse.move(0, 0)
  expect(await violations(page)).toEqual([])
  await page.getByRole('button', { name: 'Add widget' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add widget' })
  await expect(dialog).toBeVisible()
  await expect(page.locator('.MuiDialog-container')).toHaveCSS('opacity', '1') // faded in: axe reads the colours as they are drawn
  expect(await violations(page, '[role="dialog"]')).toEqual([])
  await dialog.getByRole('button', { name: 'Blank chart' }).click()
  await expect(dialog.getByLabel('Title')).toBeVisible()
  expect(await violations(page, '[role="dialog"]')).toEqual([])
})

test('a trend tile\'s period popover has no axe violations (#230)', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await graphql(page, 'mutation($i: SaveDashboardLayoutInput!) { saveDashboardLayout(input: $i) }', {
    i: { vehicleId: vehicle.id, personal: false, tiles: [{ id: 'spendingTrend', x: 0, y: 0, w: 1, h: 4, pinned: false }], hidden: [] },
  })
  await page.goto(`/vehicles/${vehicle.id}`)
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Spending period: 12 full months. Change it' }).click()
  const popover = page.getByRole('dialog', { name: 'Period of Spending' })
  await expect(popover.getByTestId('trend-timeline')).toBeVisible()
  await expect(page.locator('.MuiPopover-paper')).toHaveCSS('opacity', '1') // grown in: axe reads the colours as they are drawn
  expect(await violations(page, '[role="dialog"]')).toEqual([])
  await popover.getByRole('tab', { name: 'Last' }).click()
  await expect(popover.getByRole('switch', { name: 'Whole weeks, Monday to Sunday' })).toBeVisible()
  expect(await violations(page, '[role="dialog"]')).toEqual([])
})

test('the add vehicle dialog has no axe violations', async ({ page }) => {
  await page.goto('/')
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Add vehicle' }).first().click()
  await expect(page.getByRole('dialog', { name: 'Add vehicle' })).toBeVisible()
  expect(await violations(page, '[role="dialog"]')).toEqual([])
})
