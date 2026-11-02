import { expect, test } from '@playwright/test'
import { addVehicle, graphql, logRefueling, openVehicle, pageLoaded, signIn, signInAsNewUser } from './support/app.ts'

// What a person sets up on the screens themselves: a saved chart on the dashboard, the columns of a grid, a server setting.

test('saves a chart on the dashboard, with a trend line', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id)
  await openVehicle(page, vehicle.id)
  await page.getByRole('button', { name: 'Add chart' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add chart' })
  await dialog.getByLabel('Title').fill('Fuel by month')
  await dialog.getByRole('combobox', { name: 'Trend line' }).click()
  await page.getByRole('option', { name: 'Linear trend' }).click()
  await dialog.getByRole('button', { name: 'Save chart' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByRole('heading', { name: 'Fuel by month' })).toBeVisible()
  await page.reload()
  await pageLoaded(page)
  const card = page.locator('.MuiCard-root').filter({ has: page.getByRole('heading', { name: 'Fuel by month' }) })
  // The months without spending count as nothing spent, so a year of them has a trend, which the chart words: it was kept with the chart.
  await expect(card.getByText(/^Total · HUF: the trend line goes from /)).toBeVisible()
})

test('hides a column of a grid and keeps it hidden', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id)
  await openVehicle(page, vehicle.id, 'refuelings')
  const header = page.getByRole('columnheader', { name: 'Cost', exact: true })
  await expect(header).toBeVisible()
  await page.getByRole('button', { name: 'Columns' }).click()
  await page.getByRole('checkbox', { name: 'Show Cost' }).uncheck()
  await page.keyboard.press('Escape')
  await expect(header).toHaveCount(0)
  await page.reload()
  await pageLoaded(page)
  await expect(page.getByRole('row').first()).toBeVisible()
  await expect(header).toHaveCount(0)
})

test('changes a server setting that takes effect at once', async ({ page, isMobile }) => {
  await signIn(page)
  await page.goto('/admin/settings?tab=sync')
  await pageLoaded(page)
  // The server is shared: each size changes a setting of its own (only changed values are saved), to a new random value, so a run
  // side by side (or a repeat) never finds its value already there.
  const [label, key] = isMobile ? ['Remember applied changes (days)', 'Sync:RetentionDays'] : ['Remember removals (days)', 'Sync:TombstoneRetentionDays']
  const value = String(100 + Math.floor(Math.random() * 800))
  const form = page.getByRole('form', { name: 'Offline sync settings' })
  await form.getByRole('textbox', { name: label, exact: true }).fill(value)
  await form.getByRole('button', { name: 'Save changes' }).click()
  // The answer, not the button: it is off while the save runs too.
  await expect(page.getByText('Saved. It is in effect now.')).toBeVisible()

  // Kept in the settings file: the server says so, and the page shows it after a reload.
  const data = await graphql<{ systemSettings: { groups: { settings: { key: string; source: string }[] }[] } }>(
    page,
    '{ systemSettings { groups { settings { key source } } } }',
  )
  expect(data.systemSettings.groups.flatMap((g) => g.settings).find((s) => s.key === key)?.source).toBe('FILE')
  await page.reload()
  await pageLoaded(page)
  await expect(form.getByRole('textbox', { name: label, exact: true })).not.toHaveValue(isMobile ? '30' : '90')
})
