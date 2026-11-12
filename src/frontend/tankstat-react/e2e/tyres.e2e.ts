import { type Locator, type Page } from '@playwright/test'
import { expect, test } from './support/test.ts'
import { addVehicle, graphql, logRefueling, openVehicle, pageLoaded, signInAsNewUser } from './support/app.ts'
import { expectSoundLayout } from './support/layout.ts'

// A vehicle's tyres (#287): sets added, swapped with their pairs, a past swap from papers, a swap moved to the trash and back from the Trash
// page; the tyre log's lines (#288): one added, a swap moved by its line's day, one moved to the trash. Every test signs in as a user of its own.

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

test('adds two sets, swaps with the pairs, enters a past swap, and brings a trashed installation back from the trash', async ({ page }) => {
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

  // Its history: the installation from 20 January moved to the trash.
  await summer.getByRole('button', { name: 'History' }).click()
  const history = page.getByRole('dialog', { name: 'Michelin Primacy 4' })
  const installation = history.getByRole('listitem').filter({ hasText: /Jan 20, 2026/ })
  await expect(installation).toBeVisible()
  await expectSoundLayout(page)
  await installation.getByRole('button', { name: /^Move to the trash: Tyre swap/ }).click()
  const confirm = page.getByRole('alertdialog', { name: 'Move to the trash?' })
  await expect(confirm).toContainText('This removes the swap: Michelin Primacy 4 on the car from Jan 20, 2026')
  await confirm.getByRole('button', { name: 'Move to the trash' }).click()
  await expect(installation).toBeHidden()
  await history.getByRole('button', { name: 'Cancel' }).click()
  await expectSoundLayout(page) // the tab itself, with seven tabs on a phone

  // The Trash page's Tyres tab brings it back.
  await page.goto('/trash?tab=tyres')
  await pageLoaded(page)
  // Two swap lines of the set, each with its installation: the one the swap replaced, and the one from 20 January, trashed last: the first,
  // newest first (a phone hides the column with its days).
  const row = page.getByRole('row').filter({ hasText: 'Tyre swap: Michelin Primacy 4' }).first()
  await expect(row).toBeVisible()
  await expectSoundLayout(page)
  await expect(page.getByRole('row').filter({ hasText: 'Tyre swap: Michelin Primacy 4' })).toHaveCount(2)
  await row.getByRole('button', { name: 'Restore Tyre swap: Michelin Primacy 4' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'Tyre swap: Michelin Primacy 4' })).toHaveCount(1)
  await openVehicle(page, vehicle.id, 'tyres')
  await page.getByRole('article', { name: 'Michelin Primacy 4' }).getByRole('button', { name: 'History' }).click()
  const back = page.getByRole('dialog', { name: 'Michelin Primacy 4' })
  await expect(back.getByRole('listitem').filter({ hasText: /Jan 20, 2026/ })).toBeVisible()

  // The set's own details corrected from its history: its size.
  await back.getByRole('button', { name: 'Edit set' }).click()
  const details = page.getByRole('dialog', { name: 'Edit tyre set' })
  await details.getByLabel('Size').fill('205/55 R16 91V')
  await expectSoundLayout(page)
  await details.getByRole('button', { name: 'Save changes' }).click()
  await expect(details).toBeHidden()
  await back.getByRole('button', { name: 'Cancel' }).click()
  await expect(page.getByRole('article', { name: 'Michelin Primacy 4' })).toContainText('205/55 R16 91V')
})

test('adds a line to the tyre log, moves a swap by its line’s day, and moves a line to the trash and back (#288)', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id, { date: '2026-01-15', odometer: 1000 })
  await logRefueling(page, vehicle.id, { date: '2026-06-01', odometer: 6000 })
  const addSet = 'mutation($i: AddTyreSetInput!) { addTyreSet(input: $i) { sets { set { id name } } } }'
  const set = { vehicleId: vehicle.id, size: null, madeWeek: null, madeYear: null, roughDistance: 0, replace: false }
  await graphql(page, addSet, { i: { ...set, name: 'Michelin Primacy 4', season: 'SUMMER', since: '2023-03-25', fitted: true, day: '2026-01-15' } })
  const added = await graphql<{ addTyreSet: { sets: { set: { id: string; name: string } }[] } }>(page, addSet, {
    i: { ...set, name: 'Continental WinterContact', season: 'WINTER', since: '2022-11-19', fitted: false, day: '2026-01-15' },
  })
  const winterId = added.addTyreSet.sets.find((s) => s.set.name === 'Continental WinterContact')!.set.id
  await graphql(page, 'mutation($i: SwapTyresInput!) { swapTyres(input: $i) { vehicleId } }', { i: { vehicleId: vehicle.id, setId: winterId, day: '2026-05-01' } })
  await openVehicle(page, vehicle.id, 'tyres')
  const log = page.getByRole('region', { name: 'Tyre log' })
  await expect(log.getByRole('button', { name: 'Tyre swap, Continental WinterContact, May 1, 2026' })).toBeVisible()
  await expectSoundLayout(page) // the tab with the log's lines

  // A wheel balancing for the set on the car, with a note.
  await page.getByRole('button', { name: 'Add entry' }).click()
  const add = page.getByRole('dialog', { name: 'Add entry' })
  await add.getByRole('combobox', { name: 'What was done' }).click()
  await page.getByRole('option', { name: 'Wheel balancing' }).click()
  await expect(add.getByRole('combobox', { name: 'Set' })).toContainText('Continental WinterContact')
  await typeDate(page, dateField(add, /^Day/), '2026-05-10')
  await add.getByLabel(/^Note/).fill('Front pair')
  await expectSoundLayout(page)
  await add.getByRole('button', { name: 'Save' }).click()
  await expect(add).toBeHidden()
  const balancing = log.getByRole('button', { name: 'Wheel balancing, Continental WinterContact, May 10, 2026' })
  await expect(balancing).toContainText('Front pair')

  // The swap's line on another day: the swap moves with it, its kind and set locked.
  await log.getByRole('button', { name: 'Tyre swap, Continental WinterContact, May 1, 2026' }).click()
  const edit = page.getByRole('dialog', { name: 'Edit entry' })
  await expect(edit.getByRole('combobox', { name: 'Set' })).toHaveAttribute('aria-disabled', 'true')
  await expect(edit.getByText('Changing the day moves the swap: the set is on the car from that day.')).toBeVisible()
  await typeDate(page, dateField(edit, /^Day/), '2026-05-03')
  await expectSoundLayout(page)
  await edit.getByRole('button', { name: 'Save' }).click()
  await expect(edit).toBeHidden()
  await expect(log.getByRole('button', { name: 'Tyre swap, Continental WinterContact, May 3, 2026' })).toBeVisible()
  await expect(page.getByRole('article', { name: 'Continental WinterContact' })).toContainText(/On since May 3, 2026/)

  // The balancing to the trash, after asking, and back from the Trash page.
  await log.getByRole('button', { name: 'Move to the trash: Wheel balancing, May 10, 2026' }).click()
  const confirm = page.getByRole('alertdialog', { name: 'Move to the trash?' })
  await expect(confirm).toContainText('It can be restored from the trash.')
  await expectSoundLayout(page)
  await confirm.getByRole('button', { name: 'Move to the trash' }).click()
  await expect(balancing).toBeHidden()
  await page.goto('/trash?tab=tyres')
  await pageLoaded(page)
  const row = page.getByRole('row').filter({ hasText: 'Wheel balancing: Continental WinterContact' })
  await expect(row).toBeVisible()
  await row.getByRole('button', { name: 'Restore Wheel balancing: Continental WinterContact' }).click()
  await expect(row).toBeHidden()
  await openVehicle(page, vehicle.id, 'tyres')
  await expect(page.getByRole('region', { name: 'Tyre log' }).getByRole('button', { name: 'Wheel balancing, Continental WinterContact, May 10, 2026' })).toBeVisible()
})

test('a repair with an amount is an expense on the Expenses tab: its day changed there moves the line, and the two go to the trash and come back together (#288)', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id, { date: '2026-01-15', odometer: 1000 })
  await logRefueling(page, vehicle.id, { date: '2026-06-01', odometer: 6000 })
  const addSet = 'mutation($i: AddTyreSetInput!) { addTyreSet(input: $i) { vehicleId } }'
  const set = { vehicleId: vehicle.id, size: null, madeWeek: null, madeYear: null, roughDistance: 0, replace: false }
  await graphql(page, addSet, { i: { ...set, name: 'Michelin Primacy 4', season: 'SUMMER', since: '2023-03-25', fitted: true, day: '2026-01-15' } })
  await openVehicle(page, vehicle.id, 'tyres')

  // A puncture repaired for the set on the car, with what it cost.
  await page.getByRole('button', { name: 'Add entry' }).click()
  const add = page.getByRole('dialog', { name: 'Add entry' })
  await typeDate(page, dateField(add, /^Day/), '2026-05-10')
  await add.getByLabel(/^Paid/).fill('25000')
  await expect(add.getByLabel(/^Category/)).toHaveValue('Tyres')
  await expectSoundLayout(page)
  await add.getByRole('button', { name: 'Save' }).click()
  await expect(add).toBeHidden()
  const log = page.getByRole('region', { name: 'Tyre log' })
  await expect(log.getByRole('button', { name: /^Puncture repaired, Michelin Primacy 4, May 10, 2026, / })).toContainText('25,000')
  await expectSoundLayout(page)
  // What the set cost and its repairs, and the Spent on tyres block (#288).
  const summer = page.getByRole('article', { name: 'Michelin Primacy 4' })
  await expect(summer.getByText('Spent').locator('xpath=following-sibling::*[1]')).toContainText('25,000')
  await expect(summer.getByText('Repairs').locator('xpath=following-sibling::*[1]')).toHaveText('1')
  await expect(page.getByRole('heading', { name: 'Spent on tyres' })).toBeVisible()

  // On the Expenses tab: an ordinary expense with the chip; its date changed there moves the line.
  await page.getByRole('tab', { name: 'Expenses' }).click()
  const row = page.getByRole('row').filter({ hasText: 'Puncture repaired: Michelin Primacy 4' })
  await expect(row).toContainText('Tyre log')
  await row.getByRole('button', { name: /^Edit the expense/ }).click()
  const edit = page.getByRole('dialog', { name: 'Edit expense' })
  await expect(edit.getByText(/The date and the odometer are the same there/)).toBeVisible()
  await expectSoundLayout(page)
  await typeDate(page, dateField(edit, /^Date/), '2026-05-12')
  await edit.getByRole('button', { name: /^Save/ }).click()
  await expect(edit).toBeHidden()
  await page.getByRole('tab', { name: 'Tyres' }).click()
  const moved = page.getByRole('region', { name: 'Tyre log' }).getByRole('button', { name: /^Puncture repaired, Michelin Primacy 4, May 12, 2026/ })
  await expect(moved).toBeVisible()

  // To the trash with its expense (the box ticked), and back from the Trash page together.
  await page.getByRole('button', { name: 'Move to the trash: Puncture repaired, May 12, 2026' }).click()
  const confirm = page.getByRole('alertdialog', { name: 'Move to the trash?' })
  await expect(confirm.getByRole('checkbox', { name: /^Also move its expense/ })).toBeChecked()
  await expectSoundLayout(page)
  await confirm.getByRole('button', { name: 'Move to the trash' }).click()
  await expect(moved).toBeHidden()
  await page.goto('/trash?tab=tyres')
  await pageLoaded(page)
  const trashed = page.getByRole('row').filter({ hasText: 'Puncture repaired: Michelin Primacy 4' })
  await trashed.getByRole('button', { name: /^Restore/ }).click()
  await expect(page.getByText('Restored, with its expense.')).toBeVisible()
  await openVehicle(page, vehicle.id, 'expenses')
  await expect(page.getByRole('row').filter({ hasText: 'Puncture repaired: Michelin Primacy 4' })).toBeVisible()
})

test('a swap with a fee keeps it on its line, and the line’s new day moves the swap and the expense with it (#288)', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id, { date: '2026-01-15', odometer: 1000 })
  const addSet = 'mutation($i: AddTyreSetInput!) { addTyreSet(input: $i) { vehicleId } }'
  const set = { vehicleId: vehicle.id, size: null, madeWeek: null, madeYear: null, roughDistance: 0, replace: false }
  await graphql(page, addSet, { i: { ...set, name: 'Michelin Primacy 4', season: 'SUMMER', since: '2023-03-25', fitted: true, day: '2026-01-15' } })
  await graphql(page, addSet, { i: { ...set, name: 'Continental WinterContact', season: 'WINTER', since: '2022-11-19', fitted: false, day: '2026-01-15' } })
  await openVehicle(page, vehicle.id, 'tyres')

  await page.getByRole('article', { name: 'Michelin Primacy 4' }).getByRole('button', { name: 'Swap tyres' }).click()
  const swap = page.getByRole('dialog', { name: 'Swap tyres' })
  await typeDate(page, dateField(swap, /^Day/), '2026-05-01')
  await swap.getByLabel(/^Paid for the swap/).fill('8000')
  await expectSoundLayout(page)
  await swap.getByRole('button', { name: 'Swap tyres' }).click()
  await expect(swap).toBeHidden()
  const log = page.getByRole('region', { name: 'Tyre log' })
  await log.getByRole('button', { name: /^Tyre swap, Continental WinterContact, May 1, 2026, / }).click()
  const edit = page.getByRole('dialog', { name: 'Edit entry' })
  await expect(edit.getByText(/Its expense and its reading move with it\./)).toBeVisible()
  await typeDate(page, dateField(edit, /^Day/), '2026-05-04')
  await edit.getByRole('button', { name: 'Save' }).click()
  await expect(edit).toBeHidden()
  await expect(log.getByRole('button', { name: /^Tyre swap, Continental WinterContact, May 4, 2026, / })).toBeVisible()
  await expect(page.getByRole('article', { name: 'Continental WinterContact' })).toContainText(/On since May 4, 2026/)

  await page.getByRole('tab', { name: 'Expenses' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'Tyre swap: Continental WinterContact' })).toContainText(/5\/4\/2026|May 4, 2026|2026-05-04/)
})

test('breaks a set up in its History, shows its tyres at their corners, and swaps it back on placing each tyre (#289)', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id, { date: '2026-01-15', odometer: 1000 })
  await openVehicle(page, vehicle.id, 'tyres')
  await page.getByRole('button', { name: 'Add the tyres on the car' }).click()
  const add = page.getByRole('dialog', { name: 'Add a tyre set' })
  await add.getByLabel(/^Name/).fill('Michelin Primacy 4')
  await typeDate(page, dateField(add, /In use since/), '2023-03-25')
  await add.getByRole('button', { name: 'Save set' }).click()
  await expect(add).toBeHidden()
  await page.getByRole('button', { name: 'Add a tyre set' }).click()
  const winterAdd = page.getByRole('dialog', { name: 'Add a tyre set' })
  await winterAdd.getByLabel(/^Name/).fill('Continental WinterContact')
  await winterAdd.getByRole('radio', { name: 'Winter' }).locator('xpath=..').click()
  await typeDate(page, dateField(winterAdd, /In use since/), '2022-11-19')
  await winterAdd.getByRole('button', { name: 'Save set' }).click()
  await expect(winterAdd).toBeHidden()

  // The summer set has been on since March (its first day moved back in its History), so later swaps go in on other days.
  const summer = page.getByRole('article', { name: 'Michelin Primacy 4' })
  await summer.getByRole('button', { name: 'History' }).click()
  const history = page.getByRole('dialog', { name: 'Michelin Primacy 4' })
  await history.getByRole('button', { name: 'Edit' }).first().click()
  const installation = page.getByRole('dialog', { name: 'Edit installation' })
  await typeDate(page, dateField(installation, /First day on the car/), '2026-03-01')
  await installation.getByRole('button', { name: 'Save' }).click()
  await expect(installation).toBeHidden()

  // Track tyres one by one, from the set's History: the S labels suggested, each tyre at its corner.
  await history.getByRole('button', { name: 'Track tyres one by one' }).click()
  const breakUp = page.getByRole('dialog', { name: 'Track tyres one by one' })
  await expect(breakUp.getByLabel(/^Label/).first()).toHaveValue('S1')
  // S4: not sure where it is (decision 50: a tyre without a corner).
  await breakUp.getByRole('combobox', { name: 'Where it is now' }).nth(3).click()
  await page.getByRole('option', { name: 'Not sure' }).click()
  await expectSoundLayout(page)
  await breakUp.getByRole('button', { name: 'Track one by one' }).click()
  await expect(breakUp).toBeHidden()
  await expect(history).toContainText('Tracked tyre by tyre since')
  await history.getByRole('button', { name: 'Cancel' }).click()
  await expect(summer).toContainText('Tracked tyre by tyre · 4 of 4 on the car since')
  for (const [corner, label] of [['Front left', 'S1'], ['Front right', 'S2'], ['Rear left', 'S3']])
    await expect(summer.getByRole('button', { name: new RegExp(`^${corner}: tyre ${label},`) })).toBeVisible()
  await expect(summer.getByRole('button', { name: /^S4.*corner not recorded/ })).toBeVisible() // a row under the picture
  await expectSoundLayout(page)

  // A tyre's own dialog.
  await summer.getByRole('button', { name: /^Rear left: tyre S3,/ }).click()
  const tyre = page.getByRole('dialog', { name: 'Tyre S3' })
  await expect(tyre).toContainText('On the car, rear left')
  await expectSoundLayout(page)
  await tyre.getByRole('button', { name: 'Close' }).click()

  // The winter set on in June, then the summer set back on today, S1 and S3 exchanged.
  await summer.getByRole('button', { name: 'Swap tyres' }).click()
  const off = page.getByRole('dialog', { name: 'Swap tyres' })
  await typeDate(page, dateField(off, /^Day/), '2026-06-01')
  await off.getByRole('button', { name: 'Swap tyres' }).click()
  await expect(off).toBeHidden()
  await expect(summer).toContainText('Tracked tyre by tyre · in storage')
  await summer.getByRole('button', { name: 'Swap to this set' }).click()
  const on = page.getByRole('dialog', { name: 'Swap tyres' })
  const where = on.getByRole('group', { name: 'Where each tyre goes' })
  await expect(where.getByRole('radio', { name: 'Same as last time' })).toBeChecked()
  await where.getByRole('button', { name: 'Front left: Tyre S1' }).click()
  await expect(where.getByRole('status')).toHaveText('Tyre S1 is picked. Tap an empty corner, or the tyre it changes places with.') // S4's corner is empty
  await where.getByRole('button', { name: 'Rear left: Tyre S3' }).click()
  await expect(where.getByRole('button', { name: 'Rear left: Tyre S1' })).toBeVisible()
  // S4 has no corner: a chip under the picture, tapped, then the empty corner.
  await where.getByRole('button', { name: 'Tyre S4, corner not recorded' }).click()
  await expectSoundLayout(page) // the chip 44 px to a finger on a phone
  await where.getByRole('button', { name: 'Put S4 at rear right' }).click()
  await expectSoundLayout(page)
  await on.getByRole('button', { name: 'Swap tyres' }).click()
  await expect(on).toBeHidden()
  await expect(summer.getByRole('button', { name: /^Rear left: tyre S1,/ })).toBeVisible()
  await expect(summer.getByRole('button', { name: /^Front left: tyre S3,/ })).toBeVisible()
  await expect(summer.getByRole('button', { name: /^Rear right: tyre S4,/ })).toBeVisible()
})

/** A vehicle with a fill-up 60 days ago and the summer set on the car since then (through GraphQL), its Tyres tab open. */
async function summerOnTheCar(page: Page) {
  const day = (daysAgo: number) => new Date(Date.now() - daysAgo * 86_400_000).toISOString().slice(0, 10)
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id, { date: day(60), odometer: 1000 })
  await graphql(page, 'mutation($i: AddTyreSetInput!) { addTyreSet(input: $i) { vehicleId } }', {
    i: { vehicleId: vehicle.id, size: null, madeWeek: null, madeYear: null, roughDistance: 0, replace: false, name: 'Michelin Primacy 4', season: 'SUMMER', since: day(400), fitted: true, day: day(60) },
  })
  await openVehicle(page, vehicle.id, 'tyres')
  return { vehicle, day }
}

test('a flat on one tyre of a whole set: tapped on the car, the set tracked tyre by tyre on save, the line in the tyre’s history (#289)', async ({ page }) => {
  await summerOnTheCar(page)
  const summer = page.getByRole('article', { name: 'Michelin Primacy 4' })
  await expect(summer).toBeVisible()

  await page.getByRole('button', { name: 'Add entry' }).click()
  const add = page.getByRole('dialog', { name: 'Add entry' })
  await add.getByRole('checkbox', { name: 'The whole set' }).click()
  await add.getByRole('button', { name: 'The rear left tyre' }).click()
  await expect(add.getByRole('button', { name: 'The rear left tyre' })).toHaveAttribute('aria-pressed', 'true')
  await expectSoundLayout(page)
  await add.getByRole('button', { name: 'Save' }).click()
  await expect(add).toBeHidden()
  await expect(page.getByText('Saved for S3.')).toBeVisible()

  await expect(summer).toContainText('Tracked tyre by tyre')
  await expect(page.getByRole('button', { name: /^Puncture repaired, Michelin Primacy 4 · S3,/ })).toBeVisible()
  await summer.getByRole('button', { name: /^Rear left: tyre S3,/ }).click()
  const tyre = page.getByRole('dialog', { name: 'Tyre S3' })
  await expect(tyre).toContainText('Puncture repaired')
  await expectSoundLayout(page)
})

test('a repair of two tyres with one amount is one row and one expense; one tyre’s day changed alone parts it from the expense (#289)', async ({ page }) => {
  const { vehicle, day } = await summerOnTheCar(page)
  const summer = page.getByRole('article', { name: 'Michelin Primacy 4' })
  await expect(summer).toBeVisible()

  await page.getByRole('button', { name: 'Add entry' }).click()
  const add = page.getByRole('dialog', { name: 'Add entry' })
  await add.getByRole('checkbox', { name: 'The whole set' }).click()
  await add.getByRole('button', { name: 'The rear left tyre' }).click()
  await add.getByRole('button', { name: 'The rear right tyre' }).click()
  await add.getByLabel(/^Paid/).fill('13000')
  await add.getByRole('button', { name: 'Save' }).click()
  await expect(add).toBeHidden()
  await expect(page.getByText('Saved for S3, S4, with one expense.')).toBeVisible()
  const row = page.getByRole('button', { name: /^Puncture repaired, Michelin Primacy 4 · S3, S4,/ })
  await expect(row).toBeVisible()
  await expectSoundLayout(page)

  // The row's bin asks which tyres' lines go; the ask on save of a shared amount emptied for one tyre. Both looked at, then cancelled.
  await page.getByRole('button', { name: /^Move to the trash: Puncture repaired,/ }).click()
  const bin = page.getByRole('alertdialog', { name: 'Move to the trash?' })
  await expect(bin.getByRole('checkbox', { name: 'Tyre S4' })).toBeChecked()
  await expectSoundLayout(page)
  await bin.getByRole('button', { name: 'Cancel' }).click()
  await expect(bin).toBeHidden()
  await row.click()
  const both = page.getByRole('dialog', { name: 'Edit entry' })
  await both.getByRole('checkbox', { name: 'Tyre S4' }).click()
  await both.getByLabel(/^Paid/).fill('')
  await both.getByRole('button', { name: 'Save' }).click()
  const shared = page.getByRole('dialog', { name: 'The amount is shared' })
  await expect(shared.getByRole('radio', { name: /Only S3 lets go of it/ })).toBeChecked()
  await expectSoundLayout(page)
  await shared.getByRole('button', { name: 'Cancel' }).click()
  await both.getByRole('button', { name: 'Cancel' }).click()
  await expect(both).toBeHidden()

  // S3's line alone a day earlier: it parts, without an amount; S4 keeps the expense.
  await summer.getByRole('button', { name: /^Rear left: tyre S3,/ }).click()
  const tyre = page.getByRole('dialog', { name: 'Tyre S3' })
  await expect(tyre).toContainText('shared with S4')
  await tyre.getByRole('button', { name: /^Edit entry: Puncture repaired/ }).click()
  const edit = page.getByRole('dialog', { name: 'Edit entry' })
  await expect(edit.getByRole('checkbox', { name: 'Tyre S4' })).not.toBeChecked()
  await expectSoundLayout(page)
  await typeDate(page, dateField(edit, /^Day/), day(1))
  await edit.getByRole('button', { name: 'Save' }).click()
  await expect(edit).toBeHidden()
  await tyre.getByRole('button', { name: 'Close' }).click()

  await expect(page.getByRole('button', { name: /^Puncture repaired, Michelin Primacy 4 · S4,/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /^Puncture repaired, Michelin Primacy 4 · S3,/ })).toBeVisible()
  type Lines = { vehicleTyres: { entries: { kind: string; day: string; amount: number | null; groupId: string | null }[] } }
  const { vehicleTyres } = await graphql<Lines>(page, 'query($v: UUID!) { vehicleTyres(vehicleId: $v) { entries { kind day amount groupId } } }', { v: vehicle.id })
  const lines = vehicleTyres.entries.filter((e) => e.kind === 'PUNCTURE_REPAIRED').map((e) => [e.day, e.amount])
  expect(lines).toHaveLength(2)
  expect(lines).toEqual(expect.arrayContaining([[day(1), null], [expect.any(String), 13_000]])) // S3 a day earlier without an amount, S4 with it
})
