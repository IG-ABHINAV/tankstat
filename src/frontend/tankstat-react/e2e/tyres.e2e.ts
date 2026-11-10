import { type Locator, type Page } from '@playwright/test'
import { expect, test } from './support/test.ts'
import { addVehicle, logRefueling, openVehicle, pageLoaded, signInAsNewUser } from './support/app.ts'
import { expectSoundLayout } from './support/layout.ts'

// A vehicle's tyres (#287): sets added, swapped with their pairs, a past swap from papers, a stretch moved to the trash and back from the
// Trash page. Every test signs in as a user of its own.

test.beforeEach(async ({ page }) => {
  await signInAsNewUser(page)
})

/** Types a day into a date field (the MUI X field's sections in the order the language shows them). */
async function typeDate(page: Page, field: Locator, iso: string) {
  const [year, month, day] = iso.split('-')
  const sections = field.getByRole('spinbutton')
  await expect(sections.first()).toBeVisible() // the picker comes in a chunk of its own
  const digits: string[] = []
  for (let i = 0; i < (await sections.count()); i++) {
    const label = ((await sections.nth(i).getAttribute('aria-label')) ?? '').toLowerCase()
    digits.push(label.includes('year') ? year : label.includes('month') ? month : day)
  }
  await sections.first().click()
  await page.keyboard.press('ControlOrMeta+a')
  await page.keyboard.press('Backspace')
  await page.keyboard.type(digits.join(''))
}

const dateField = (dialog: Locator, name: RegExp) => dialog.getByRole('group', { name })

test('adds two sets, swaps with the pairs, enters a past swap, and brings a trashed stretch back from the trash', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id, { date: '2026-01-15', odometer: 1000 })
  await logRefueling(page, vehicle.id, { date: '2026-06-01', odometer: 6000 })
  await openVehicle(page, vehicle.id, 'tyres')
  await expect(page.getByRole('tab', { name: 'Tyres', selected: true })).toBeVisible()

  // The set on the car, used: in use since a day long ago, with a rough figure.
  await page.getByRole('button', { name: 'Add the tyres on the car' }).click()
  const first = page.getByRole('dialog', { name: 'Add a tyre set' })
  await first.getByLabel(/^Name/).fill('Michelin Primacy 4')
  await typeDate(page, dateField(first, /In use since/), '2023-03-25')
  await first.getByLabel(/^Driven so far/).fill('30000')
  await expectSoundLayout(page)
  await first.getByRole('button', { name: 'Save set' }).click()
  await expect(first).toBeHidden()
  const summer = page.getByRole('article', { name: 'Michelin Primacy 4' })
  await expect(summer).toContainText('On since')

  // The winter set, in storage.
  await page.getByRole('button', { name: 'Add a tyre set' }).click()
  const second = page.getByRole('dialog', { name: 'Add a tyre set' })
  await second.getByLabel(/^Name/).fill('Continental WinterContact')
  await second.getByRole('radio', { name: 'Winter' }).locator('xpath=..').click() // the segment, its radio hidden inside it
  await expect(second.getByRole('radio', { name: 'In storage' })).toBeChecked()
  await typeDate(page, dateField(second, /In use since/), '2022-11-19')
  await second.getByRole('button', { name: 'Save set' }).click()
  await expect(second).toBeHidden()

  // The swap: the winter set on, its pairs recorded from now on.
  await summer.getByRole('button', { name: 'Swap tyres' }).click()
  const swap = page.getByRole('dialog', { name: 'Swap tyres' })
  await expect(swap.getByText(/Optional\. The car's reading that day|Optional\. Left empty/)).toBeVisible()
  await swap.getByRole('radio', { name: 'Pair 2 at the front' }).locator('xpath=..').click()
  await expectSoundLayout(page)
  await swap.getByRole('button', { name: 'Swap tyres' }).click()
  // The summer set went on today when it was added: a second swap today replaces that one, after asking once (#287 Q2).
  await expect(swap.getByText(/This replaces the swap of .* \(Michelin Primacy 4 went on\)/)).toBeVisible()
  await swap.getByRole('button', { name: 'Replace the swap' }).click()
  await expect(swap).toBeHidden()
  const winter = page.getByRole('article', { name: 'Continental WinterContact' })
  await expect(winter).toContainText('pair 2 at the front')
  await expect(page.getByRole('heading', { name: 'On the car' }).locator('..').getByRole('article', { name: 'Continental WinterContact' })).toBeVisible()

  // A swap from papers: the summer set went on on 20 January, no reading.
  await page.getByRole('button', { name: 'Add past swaps' }).click()
  const past = page.getByRole('dialog', { name: 'Add past swaps' })
  await typeDate(page, dateField(past, /^Day/), '2026-01-20')
  await past.getByRole('combobox', { name: 'Goes on' }).click()
  await page.getByRole('option', { name: /Michelin Primacy 4/ }).click()
  await expectSoundLayout(page)
  await past.getByRole('button', { name: 'Save swaps' }).click()
  await expect(past).toBeHidden()

  // Its history: the stretch from 20 January moved to the trash.
  await summer.getByRole('button', { name: 'History' }).click()
  const history = page.getByRole('dialog', { name: 'Michelin Primacy 4' })
  const stretch = history.getByRole('listitem').filter({ hasText: /Jan 20, 2026/ })
  await expect(stretch).toBeVisible()
  await expectSoundLayout(page)
  await stretch.getByRole('button', { name: 'Move to the trash' }).click()
  await page.getByRole('alertdialog', { name: 'Move to the trash?' }).getByRole('button', { name: 'Move to the trash' }).click()
  await expect(stretch).toBeHidden()
  await history.getByRole('button', { name: 'Cancel' }).click()
  await expectSoundLayout(page) // the tab itself, with seven tabs on a phone

  // The Trash page's Tyres tab brings it back.
  await page.goto('/trash?tab=tyres')
  await pageLoaded(page)
  // Two rows of the set: the stretch the swap replaced, and the one from 20 January, trashed last: the first, newest first (a phone hides
  // the column with its days).
  const row = page.getByRole('row').filter({ hasText: 'Michelin Primacy 4: on the car' }).first()
  await expect(row).toBeVisible()
  await expectSoundLayout(page)
  await expect(page.getByRole('row').filter({ hasText: 'Michelin Primacy 4: on the car' })).toHaveCount(2)
  await row.getByRole('button', { name: 'Restore Michelin Primacy 4: on the car' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'Michelin Primacy 4: on the car' })).toHaveCount(1)
  await openVehicle(page, vehicle.id, 'tyres')
  await page.getByRole('article', { name: 'Michelin Primacy 4' }).getByRole('button', { name: 'History' }).click()
  await expect(page.getByRole('dialog', { name: 'Michelin Primacy 4' }).getByRole('listitem').filter({ hasText: /Jan 20, 2026/ })).toBeVisible()
})
