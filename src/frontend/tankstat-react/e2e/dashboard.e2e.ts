import { expect, test, type Page } from '@playwright/test'
import { addVehicle, logRefueling, openVehicle, pageLoaded, signInAsNewUser } from './support/app.ts'
import { violations } from './support/axe.ts'
import { expectSoundLayout } from './support/layout.ts'

// Arranging a vehicle's dashboard (#211): the vehicle's, which everyone who sees it gets, and one's own, which wins for oneself alone.

/** The titles of the dashboard's charts, in the order it shows them. */
const chartTitles = (page: Page) => page.getByRole('region', { name: 'Charts' }).getByRole('heading', { level: 3 }).allTextContents()

/** The key figures' labels, in order. */
const figureLabels = (page: Page) => page.getByRole('region', { name: 'Key figures' }).locator('.MuiCard-root > p:first-child').allTextContents()

test('customizes the vehicle\'s dashboard: a figure moved, a ready-made chart taken off, kept after a reload', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id)
  await openVehicle(page, vehicle.id)
  await expect.poll(() => chartTitles(page)).toEqual(['Monthly costs', 'Consumption', 'Fuel price', 'Expenses by category', 'Distance per month'])

  await page.getByRole('button', { name: 'Customize' }).click()
  const dialog = page.getByRole('dialog', { name: 'Customize the dashboard' })
  await expect(dialog.getByRole('radio', { name: 'Everyone who sees this vehicle' })).toBeChecked()
  await expectSoundLayout(page)
  expect(await violations(page, '[role="dialog"]')).toEqual([])
  await dialog.getByRole('button', { name: 'Move Odometer up' }).click()
  await expect(dialog.getByRole('status')).toHaveText('Odometer moved to position 2 of 4.')
  await dialog.getByRole('button', { name: 'Remove Fuel price' }).click()
  await dialog.getByRole('button', { name: 'Save dashboard' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByText('Saved.')).toBeVisible()

  await page.reload()
  await pageLoaded(page)
  await expect.poll(() => chartTitles(page)).toEqual(['Monthly costs', 'Consumption', 'Expenses by category', 'Distance per month'])
  expect(await figureLabels(page)).toEqual(['Spent this month', 'Odometer', 'Average consumption', 'Last fill-up'])
  await expectSoundLayout(page)
})

test('arranges a dashboard of one\'s own, then goes back to the vehicle\'s', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await openVehicle(page, vehicle.id)
  await expect(page.getByRole('heading', { name: 'Monthly costs' })).toBeVisible()

  await page.getByRole('button', { name: 'Customize' }).click()
  const dialog = page.getByRole('dialog', { name: 'Customize the dashboard' })
  // The segment's label, as a person taps it (the radio inside is visually hidden).
  await dialog.locator('label', { hasText: 'Just me' }).click()
  await expect(dialog.getByRole('radio', { name: 'Just me' })).toBeChecked()
  for (const name of ['Monthly costs', 'Consumption', 'Fuel price', 'Expenses by category']) await dialog.getByRole('button', { name: `Remove ${name}` }).click()
  await dialog.getByRole('button', { name: 'Save dashboard' }).click()
  await expect(dialog).toBeHidden()

  await expect(page.getByText('You see your own arrangement of this dashboard.')).toBeVisible()
  await expect.poll(() => chartTitles(page)).toEqual(['Distance per month'])
  await page.getByRole('button', { name: "Use the vehicle's dashboard" }).click()
  await expect(page.getByText('You see your own arrangement of this dashboard.')).toBeHidden()
  await expect.poll(() => chartTitles(page)).toEqual(['Monthly costs', 'Consumption', 'Fuel price', 'Expenses by category', 'Distance per month'])
})
