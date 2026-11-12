import { expect, test } from '@playwright/test'
import { addVehicle, graphql, logRefueling, openVehicle, pageLoaded, signIn, signInAsNewUser } from './support/app.ts'
import { expectSoundLayout } from './support/layout.ts'

// Working without the server: the device downloads a vehicle, the browser goes offline, a refuelling is kept on the device, and it
// reaches the server once the browser is back online. Only in-app navigation while offline (the address bar would need the server).

test('keeps a refuelling made offline and sends it when the server is back', async ({ page, context }) => {
  // The download and the poll below may take 30 s each on a busy runner: more than the default for the whole test.
  test.setTimeout(90_000)
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  const today = new Date().toISOString().slice(0, 10)
  await logRefueling(page, vehicle.id, { date: today, volume: 35, odometer: 1000 })

  // Download what the device keeps, now rather than whenever the app gets to it.
  await page.goto('/account')
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Download now' }).click()
  await expect(page.getByText('Downloaded.')).toBeVisible({ timeout: 30_000 })

  await openVehicle(page, vehicle.id, 'refuelings')
  await expect(page.getByRole('row').filter({ hasText: '35 L' })).toBeVisible()

  await context.setOffline(true)
  await page.getByRole('button', { name: 'Add refuelling' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add refuelling' })
  await dialog.getByLabel(/^Volume/).fill('22.5')
  await dialog.getByLabel('Total cost').fill('13500')
  await dialog.getByLabel(/^Odometer/).fill('1400')
  await dialog.getByRole('button', { name: 'Add refuelling' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByText('Saved on this device · syncs when online.')).toBeVisible()
  await expect(page.getByRole('row').filter({ hasText: '22.5 L' })).toBeVisible()

  await context.setOffline(false)
  // Once the server answers again the device sends what it kept: the server then has the refuelling.
  await expect
    .poll(
      async () => {
        const data = await graphql<{ refuelings: { volume: number }[] }>(
          page,
          'query($v: UUID!) { refuelings(vehicleId: $v) { volume } }',
          { v: vehicle.id },
        )
        return data.refuelings.map((r) => r.volume).sort((a, b) => a - b)
      },
      { timeout: 60_000 },
    )
    .toEqual([22.5, 35])
})

test('signs out while offline: warns of the change not sent, deletes it, and the server ends the session once it is back', async ({ page, context, isMobile }) => {
  // The download alone may take 30 s on a busy runner (below): more than the default for the whole test.
  test.setTimeout(90_000)
  const user = await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  // A log on the server first: offline, the dialog takes its currency from the vehicle's last log.
  await logRefueling(page, vehicle.id, { date: new Date().toISOString().slice(0, 10), volume: 35, odometer: 800 })
  await page.goto('/account')
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Download now' }).click()
  await expect(page.getByText('Downloaded.')).toBeVisible({ timeout: 30_000 })
  await openVehicle(page, vehicle.id, 'refuelings')

  // A change only this device holds.
  await context.setOffline(true)
  await page.getByRole('button', { name: 'Add refuelling' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add refuelling' })
  await dialog.getByLabel(/^Volume/).fill('18')
  await dialog.getByLabel('Total cost').fill('11000')
  await dialog.getByLabel(/^Odometer/).fill('900')
  await dialog.getByRole('button', { name: 'Add refuelling' }).click()
  await expect(page.getByText('Saved on this device · syncs when online.')).toBeVisible()

  if (isMobile) await page.getByRole('button', { name: 'Show menu' }).click()
  await page.getByRole('button', { name: 'Sign out', exact: true }).click()
  const warning = page.getByRole('alertdialog', { name: 'Sign out and delete what was not sent?' })
  await expect(warning).toContainText('1 change made on this device has not reached the server yet.')
  await warning.getByRole('button', { name: 'Sign out and delete' }).click()
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible() // offline too: nothing waits for the server

  // Back online: the device tells the server as soon as it answers again (#236), whose session for these cookies then names nobody. Up to
  // 40 s: a tell made on the return that fails is told again only after `RETRY_MS` (30 s), never in a loop with the probes.
  await context.setOffline(false)
  await expect
    .poll(async () => (await graphql<{ session: { user: { id: string } | null } }>(page, '{ session { user { id } } }')).session.user, { timeout: 40_000 })
    .toBeNull()

  // Signed in again, the deleted change is not sent: the server never had it, and the device no longer has it.
  await signIn(page, user)
  await page.reload()
  await pageLoaded(page)
  const data = await graphql<{ refuelings: { volume: number }[] }>(page, 'query($v: UUID!) { refuelings(vehicleId: $v) { volume } }', { v: vehicle.id })
  expect(data.refuelings.map((r) => r.volume)).toEqual([35])
  await expect(page.getByText(/not synced/)).toHaveCount(0)
})


test('keeps tyre changes made offline (a swap, a trash, a retire, a reading, an amount), shows them done, and sends them in order when the server is back (#287)', async ({ page, context }) => {
  // The download and the poll below may take 30 s each on a busy runner: more than the default for the whole test.
  test.setTimeout(120_000)
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  const day = (daysAgo: number) => new Date(Date.now() - daysAgo * 86_400_000).toISOString().slice(0, 10)
  await logRefueling(page, vehicle.id, { date: day(60), volume: 35, odometer: 1000 })
  const addSet = 'mutation($i: AddTyreSetInput!) { addTyreSet(input: $i) { vehicleId } }'
  const set = { vehicleId: vehicle.id, size: null, madeWeek: null, madeYear: null, roughDistance: 0, replace: false }
  await graphql(page, addSet, { i: { ...set, name: 'Michelin Primacy 4', season: 'SUMMER', since: day(400), fitted: true, day: day(60) } })
  await graphql(page, addSet, { i: { ...set, name: 'Continental WinterContact', season: 'WINTER', since: day(400), fitted: false, day: day(0) } })

  await page.goto('/account')
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Download now' }).click()
  await expect(page.getByText('Downloaded.')).toBeVisible({ timeout: 30_000 })
  await openVehicle(page, vehicle.id, 'tyres')
  const summer = page.getByRole('article', { name: 'Michelin Primacy 4' })
  const winter = page.getByRole('article', { name: 'Continental WinterContact' })
  await expect(winter).toBeVisible()

  await context.setOffline(true)
  // A swap today with the day's reading: the winter set goes on, shown done and marked.
  await summer.getByRole('button', { name: 'Swap tyres' }).click()
  const swap = page.getByRole('dialog', { name: 'Swap tyres' })
  await expect(swap.getByText(/^You are offline\./)).toBeVisible()
  await swap.getByLabel(/^Odometer/).fill('1500')
  await expectSoundLayout(page)
  await swap.getByRole('button', { name: 'Swap tyres' }).click()
  await expect(swap).toBeHidden()
  await expect(page.getByText('Saved on this device · syncs when online.')).toBeVisible()
  await expect(winter.getByText('Changed · not synced')).toBeVisible()
  await expect(winter.getByRole('button', { name: 'Swap tyres' })).toBeVisible() // on the car now
  await expectSoundLayout(page) // the tab with its marks

  // The swap the waiting one cut, moved to the trash with its line: made on what a waiting change changed.
  await summer.getByRole('button', { name: 'History' }).click()
  const history = page.getByRole('dialog', { name: 'Michelin Primacy 4' })
  await history.getByRole('button', { name: /^Move to the trash: Tyre swap/ }).click()
  await page.getByRole('alertdialog', { name: 'Move to the trash?' }).getByRole('button', { name: 'Move to the trash' }).click()
  await expect(history.getByRole('listitem')).toHaveCount(0)
  await history.getByRole('button', { name: 'Cancel' }).click()

  // The set that came off, retired: a change with its day.
  await summer.getByRole('button', { name: 'Retire' }).click()
  await page.getByRole('alertdialog', { name: 'Retire this set?' }).getByRole('button', { name: 'Retire set' }).click()
  await expect(page.getByRole('heading', { name: 'Retired' })).toBeVisible()

  // The waiting swap's reading corrected on its line of the tyre log.
  await page.getByRole('region', { name: 'Tyre log' }).getByRole('button', { name: /^Tyre swap, Continental WinterContact/ }).click()
  const reading = page.getByRole('dialog', { name: 'Edit entry' })
  await reading.getByLabel(/^Odometer/).fill('1550')
  await reading.getByRole('button', { name: 'Save' }).click()
  await expect(reading).toBeHidden()

  // A line with an amount (#288): shown with it in the tyre log at once; its expense is made once the change reaches the server.
  await page.getByRole('button', { name: 'Add entry' }).click()
  const paid = page.getByRole('dialog', { name: 'Add entry' })
  await paid.getByLabel(/^Paid/).fill('9000')
  await paid.getByRole('button', { name: 'Save' }).click()
  await expect(paid).toBeHidden()
  await expect(page.getByRole('region', { name: 'Tyre log' }).getByRole('button', { name: /^Puncture repaired, .*9,000/ })).toBeVisible()
  // On the Expenses tab, the expense it will make: new, not synced, with its chip.
  await page.getByRole('tab', { name: 'Expenses' }).click()
  const waitingExpense = page.getByRole('row').filter({ hasText: /^.*Puncture repaired/ })
  await expect(waitingExpense).toContainText('Tyre log')
  await expect(waitingExpense.getByRole('button', { name: /New · not synced/ })).toBeVisible()
  await expectSoundLayout(page)
  await page.getByRole('tab', { name: 'Tyres' }).click()

  await context.setOffline(false)
  // Once the server answers again the device sends the four changes in order: every one applied, none parked.
  type Tyres = { vehicleTyres: { sets: { onTheCar: boolean; set: { name: string; isRetired: boolean }; installations: { installation: { from: string } }[] }[]; entries: { kind: string; day: string; odometer: number | null; amount: number | null }[] } }
  await expect
    .poll(
      async () => {
        const { vehicleTyres } = await graphql<Tyres>(page, 'query($v: UUID!) { vehicleTyres(vehicleId: $v) { sets { onTheCar set { name isRetired } installations { installation { from } } } entries { kind day odometer amount } } }', { v: vehicle.id })
        return { sets: vehicleTyres.sets.map((s) => [s.set.name, s.onTheCar, s.set.isRetired, s.installations.length]), entries: vehicleTyres.entries }
      },
      { timeout: 60_000 },
    )
    .toEqual({
      sets: [['Continental WinterContact', true, false, 1], ['Michelin Primacy 4', false, true, 0]],
      entries: [{ kind: 'SWAP', day: day(0), odometer: 1550, amount: null }, { kind: 'PUNCTURE_REPAIRED', day: day(0), odometer: null, amount: 9000 }],
    })
  expect((await graphql<{ parkedChanges: unknown[] }>(page, '{ parkedChanges { id } }')).parkedChanges).toEqual([])
  await expect(winter.getByText('Changed · not synced')).toBeHidden()
})
