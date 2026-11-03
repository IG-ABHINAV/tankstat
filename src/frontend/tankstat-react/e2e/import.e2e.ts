import { expect, test } from './support/test.ts'
import { pageLoaded, signInAsNewUser } from './support/app.ts'

// Importing another app's export (Fuelio): the file, where it goes, what will be imported, and the vehicle it made.

test('imports a Fuelio export into a new vehicle', async ({ page }) => {
  await signInAsNewUser(page)
  await page.goto('/import')
  await pageLoaded(page)
  const chooser = page.waitForEvent('filechooser')
  await page.getByRole('button', { name: 'Choose file' }).click()
  await (await chooser).setFiles('e2e/files/fuelio.csv')
  await page.getByRole('button', { name: 'Read the file' }).click()

  await page.getByRole('radio', { name: 'A new vehicle' }).check()
  await expect(page.getByRole('group', { name: 'The new vehicle' }).getByLabel('Name')).toHaveValue('Polo')
  await page.getByRole('button', { name: 'What will be imported' }).click()
  await expect(page.getByText('3 fuel logs, 1 expenses and 0 recurring expenses, from Jun 1, 2026 to Sep 17, 2026.')).toBeVisible()
  await page.getByRole('button', { name: 'Import', exact: true }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Imported 3 fuel logs, 1 expenses and 0 recurring expenses.' })).toBeVisible()

  await page.getByRole('link', { name: 'Open the vehicle' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Polo' })).toBeVisible()
  await page.getByRole('tab', { name: 'Refuelings' }).click()
  await expect(page.getByRole('row').filter({ hasText: /38\.5/ })).toBeVisible()
})
