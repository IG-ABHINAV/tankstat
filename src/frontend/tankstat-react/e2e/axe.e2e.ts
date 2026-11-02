import { expect, test } from '@playwright/test'
import { addVehicle, pageLoaded, signIn } from './support/app.ts'
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
  await page.getByRole('button', { name: 'Add widget' }).click()
  await expect(page.getByRole('region', { name: 'Add widget' })).toBeVisible()
  expect(await violations(page)).toEqual([])
})

test('the add vehicle dialog has no axe violations', async ({ page }) => {
  await page.goto('/')
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Add vehicle' }).first().click()
  await expect(page.getByRole('dialog', { name: 'Add vehicle' })).toBeVisible()
  expect(await violations(page, '[role="dialog"]')).toEqual([])
})
