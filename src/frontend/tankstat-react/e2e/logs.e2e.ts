import { expect, test, type Page } from '@playwright/test'
import { addDueRecurring, addExpense, addVehicle, logRefueling, openVehicle, pageLoaded, signInAsNewUser } from './support/app.ts'
import { expectSoundLayout } from './support/layout.ts'

// The logs of a vehicle: refuellings, expenses and recurring expenses, changed and trashed through the vehicle page and the home card.
// Every test signs in as a user of its own, so its home page and trash hold only its own data.

test.beforeEach(async ({ page }) => {
  await signInAsNewUser(page)
})

/** The grid's row that holds the text (a value shown at both sizes). */
const row = (page: Page, text: string | RegExp) => page.getByRole('row').filter({ hasText: text })

test('edits a refuelling', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id, { date: '2026-01-15', volume: 40 })
  await openVehicle(page, vehicle.id, 'refuelings')
  await page.getByRole('button', { name: 'Edit the refuelling of Jan 15, 2026' }).click()
  const dialog = page.getByRole('dialog', { name: 'Edit refuelling' })
  await dialog.getByLabel(/^Volume/).fill('38.25')
  await dialog.getByRole('button', { name: 'Save changes' }).click()
  await expect(dialog).toBeHidden()
  await expect(row(page, '38.25 L')).toBeVisible()
})

test('types the litres, the pump price and the amount paid: the price per litre follows, the litres never change', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await openVehicle(page, vehicle.id, 'refuelings')
  await page.getByRole('button', { name: 'Add refuelling' }).first().click()
  const dialog = page.getByRole('dialog', { name: 'Add refuelling' })
  await dialog.getByLabel(/^Volume/).fill('43')
  await dialog.getByLabel(/^Price per/).fill('818')
  await expect(dialog.getByLabel('Total cost')).toHaveValue('35174') // the pump's total
  await dialog.getByLabel('Total cost').fill('34744') // what the receipt says was paid, after a discount
  await expect(dialog.getByLabel(/^Price per/)).toHaveValue('808') // the price paid per litre; the litres stay 43
  await expect(dialog.getByLabel(/^Volume/)).toHaveValue('43')
  await dialog.getByLabel(/^Odometer/).fill('12450')
  await dialog.getByRole('button', { name: 'Add refuelling' }).click()
  await expect(dialog).toBeHidden()
  await expect(row(page, '43 L')).toBeVisible()
})

test('the litres, the amount paid and the pump price give the discount, which is saved with the refuelling', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await openVehicle(page, vehicle.id, 'refuelings')
  await page.getByRole('button', { name: 'Add refuelling' }).first().click()
  const dialog = page.getByRole('dialog', { name: 'Add refuelling' })
  await dialog.getByLabel(/^Volume/).fill('43')
  await dialog.getByLabel('Total cost').fill('34744') // what the receipt says was paid
  await expect(dialog.getByLabel(/^Price per/)).toHaveValue('808')
  await dialog.getByLabel(/^Price per/).fill('818') // the pump's price: the amount paid stays, the discount appears (#204)
  await expect(dialog.getByLabel('Discount')).toHaveValue('430')
  await expect(dialog.getByLabel('Total cost')).toHaveValue('34744')
  await expectSoundLayout(page) // the row of discount, total and currency, at both sizes
  await dialog.getByLabel(/^Odometer/).fill('12450')
  await dialog.getByRole('button', { name: 'Add refuelling' }).click()
  await expect(dialog).toBeHidden()
  await expect(row(page, '43 L')).toContainText(/34,744/) // the cost column shows what was paid, at both sizes

  // The discount is saved: it is back in the edit dialog, and the pump's price with it.
  await page.getByRole('button', { name: /^Edit the refuelling of/ }).click()
  const edit = page.getByRole('dialog', { name: 'Edit refuelling' })
  await expect(edit.getByLabel('Discount')).toHaveValue('430')
  await expect(edit.getByLabel(/^Price per/)).toHaveValue('818')
})

test('trashes a refuelling and takes it back with Undo', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id, { date: '2026-01-15', volume: 41 })
  await openVehicle(page, vehicle.id, 'refuelings')
  await page.getByRole('button', { name: 'Delete the refuelling of Jan 15, 2026' }).click()
  await page.getByRole('alertdialog', { name: 'Move this refuelling to the trash?' }).getByRole('button', { name: 'Move to trash' }).click()
  await expect(row(page, '41 L')).toHaveCount(0)
  await page.getByRole('button', { name: 'Undo' }).click()
  await expect(row(page, '41 L')).toBeVisible()
})

test('lets a tap on a toast through to the page under it, only its Undo takes one', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id, { date: '2026-01-15', volume: 42 })
  await openVehicle(page, vehicle.id, 'refuelings')
  await page.getByRole('button', { name: 'Delete the refuelling of Jan 15, 2026' }).click()
  await page.getByRole('alertdialog', { name: 'Move this refuelling to the trash?' }).getByRole('button', { name: 'Move to trash' }).click()
  const undo = page.getByRole('button', { name: 'Undo' })
  await expect(undo).toBeVisible()
  // The toast grows in: it is hit-tested once its box holds still.
  let last = ''
  await expect
    .poll(async () => {
      const now = JSON.stringify(await page.locator('.MuiSnackbar-root').boundingBox())
      const still = now === last
      last = now
      return still
    }, { intervals: [100] })
    .toBe(true)
  // What a tap would reach (#239): the page at the message and beside it (on a phone the toast's root spans the screen's width), the
  // button at Undo. A click on whatever lies under the toast would not do: it differs by size, and Playwright waits rather than fails.
  const hits = await page.evaluate(() => {
    const root = document.querySelector('.MuiSnackbar-root')!
    const message = root.querySelector('.MuiAlert-message')!.getBoundingClientRect()
    const button = root.querySelector('.MuiAlert-action button')!.getBoundingClientRect()
    const box = root.getBoundingClientRect()
    const inToast = (x: number, y: number) => root.contains(document.elementFromPoint(x, y))
    return {
      message: inToast(message.left + message.width / 2, message.top + message.height / 2),
      beside: inToast(box.left + 2, box.top + box.height / 2),
      undo: document.elementFromPoint(button.left + button.width / 2, button.top + button.height / 2)?.closest('button')?.textContent ?? null,
    }
  })
  expect(hits).toEqual({ message: false, beside: false, undo: 'Undo' })
  await undo.click()
  await expect(row(page, '42 L')).toBeVisible()
})

test('restores a trashed refuelling from the trash', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id, { date: '2026-01-15', volume: 42 })
  await openVehicle(page, vehicle.id, 'refuelings')
  await page.getByRole('button', { name: 'Delete the refuelling of Jan 15, 2026' }).click()
  await page.getByRole('alertdialog', { name: 'Move this refuelling to the trash?' }).getByRole('button', { name: 'Move to trash' }).click()
  await expect(row(page, '42 L')).toHaveCount(0)

  await page.goto('/trash')
  await pageLoaded(page)
  await page.getByRole('tab', { name: 'Refuelings' }).click()
  await page.getByRole('button', { name: /^Restore/ }).first().click()
  // Gone from the trash once the server restored it: found by its vehicle, whose column the trash shows at both sizes (the volume's is
  // hidden on a phone, where a wait for it passed at once and the navigation below cut the restore short).
  await expect(page.getByRole('tabpanel').getByRole('row').filter({ hasText: vehicle.name })).toHaveCount(0)

  await openVehicle(page, vehicle.id, 'refuelings')
  await expect(row(page, '42 L')).toBeVisible()
})

test('adds, edits and trashes an expense', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await openVehicle(page, vehicle.id, 'expenses')
  await page.getByRole('button', { name: 'Add expense' }).click()
  const add = page.getByRole('dialog', { name: 'Add expense' })
  await add.getByLabel('Title').fill('Parking')
  await add.getByLabel('Amount').fill('1500')
  await add.getByLabel('Discount').fill('300') // a coupon: the amount is what was paid
  await expectSoundLayout(page) // the row of amount, discount and currency, at both sizes
  await add.getByRole('button', { name: 'Add expense' }).click()
  await expect(add).toBeHidden()
  await expect(row(page, 'Parking')).toBeVisible()

  await page.getByRole('button', { name: 'Edit the expense Parking' }).click()
  const edit = page.getByRole('dialog', { name: 'Edit expense' })
  await expect(edit.getByLabel('Discount')).toHaveValue('300') // saved with the expense
  await edit.getByLabel('Title').fill('Parking garage')
  await edit.getByRole('button', { name: 'Save changes' }).click()
  await expect(edit).toBeHidden()
  await expect(row(page, 'Parking garage')).toBeVisible()

  await page.getByRole('button', { name: 'Delete the expense Parking garage' }).click()
  await page.getByRole('alertdialog', { name: 'Move this expense to the trash?' }).getByRole('button', { name: 'Move to trash' }).click()
  await expect(row(page, 'Parking garage')).toHaveCount(0)
})

test('adds a recurring expense and marks it as done', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id)
  await openVehicle(page, vehicle.id, 'recurring')
  await page.getByRole('button', { name: 'Add recurring expense' }).click()
  const add = page.getByRole('dialog', { name: 'Add recurring expense' })
  await add.getByLabel('Title').fill('Oil change')
  await add.getByLabel('Every (kilometers)').fill('15000')
  await add.getByRole('button', { name: 'Add recurring expense' }).click()
  await expect(add).toBeHidden()
  await expect(page.getByRole('rowheader', { name: 'Oil change' })).toBeVisible()

  await page.getByRole('button', { name: 'Mark Oil change as done' }).click()
  const done = page.getByRole('dialog', { name: 'Mark as done: Oil change' })
  await done.getByLabel('Amount (optional)').fill('35000')
  await done.getByLabel('Discount').fill('2000') // taken off at the workshop: kept with the visit's expense
  await expectSoundLayout(page) // the row of amount (with its hint), discount and currency, at both sizes
  await done.getByRole('button', { name: 'Mark as done' }).click()
  await expect(done).toBeHidden()
  await expect(page.getByRole('row').filter({ hasText: 'Oil change' })).toBeVisible()
})

test('logs a refuelling and an expense from the home card', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await page.goto('/')
  await pageLoaded(page)
  await page.getByRole('button', { name: `Refuel ${vehicle.name}` }).click()
  const refuel = page.getByRole('dialog', { name: 'Add refuelling' })
  await refuel.getByLabel(/^Volume/).fill('30')
  await refuel.getByLabel('Total cost').fill('18000')
  await refuel.getByLabel(/^Odometer/).fill('2500')
  await refuel.getByRole('button', { name: 'Add refuelling' }).click()
  await expect(refuel).toBeHidden()
  // The card asks for its figures afresh: the odometer is the new reading.
  await expect(page.getByRole('listitem').filter({ hasText: vehicle.name }).getByText('2,500 km')).toBeAttached()

  await page.getByRole('button', { name: `Expense for ${vehicle.name}` }).click()
  const expense = page.getByRole('dialog', { name: 'Add expense' })
  await expense.getByLabel('Title').fill('Vignette')
  await expense.getByLabel('Amount').fill('5000')
  await expense.getByRole('button', { name: 'Add expense' }).click()
  await expect(expense).toBeHidden()
  await openVehicle(page, vehicle.id, 'expenses')
  await expect(row(page, 'Vignette')).toBeVisible()
})

test('marks a due recurring expense as done from the home card, with only its category ticked', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await addDueRecurring(page, vehicle.id, 'Insurance', 'Insurance')
  await addDueRecurring(page, vehicle.id, 'Vignette', 'Fees')
  await page.goto('/')
  await pageLoaded(page)
  const attention = page.getByRole('list', { name: 'Needs attention' })
  await expect(attention.getByText('Insurance')).toBeVisible()
  await page.getByRole('button', { name: `Mark Insurance of ${vehicle.name} as done` }).click()
  const done = page.getByRole('dialog', { name: 'Mark as done: Insurance' })
  await expect(done.getByRole('checkbox', { name: 'Insurance' })).toBeChecked()
  await expect(done.getByRole('checkbox', { name: 'Vignette' })).not.toBeChecked() // due too, but of another category: not part of this visit
  await done.getByRole('button', { name: 'Mark as done' }).click()
  await expect(done).toBeHidden()
  await expect(attention.getByText('Insurance')).toHaveCount(0)
  await expect(attention.getByText('Vignette')).toBeVisible()
})

test('shows an expense logged through the API on the dashboard', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await addExpense(page, vehicle.id, { title: 'Tyres', date: new Date().toISOString().slice(0, 10), amount: 64000 })
  await openVehicle(page, vehicle.id)
  await expect(page.getByRole('region', { name: 'Dashboard' }).getByText('HUF 64,000').first()).toBeVisible()
})
