import { expect, test } from '@playwright/test'
import { addVehicle, graphql, logRefueling, openVehicle, pageLoaded, signIn, signInAsNewUser } from './support/app.ts'

// Working without the server: the device downloads a vehicle, the browser goes offline, a refuelling is kept on the device, and it
// reaches the server once the browser is back online. Only in-app navigation while offline (the address bar would need the server).

test('keeps a refuelling made offline and sends it when the server is back', async ({ page, context }) => {
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

  // Back online: the device tells the server as soon as it answers again (#236), whose session for these cookies then names nobody.
  await context.setOffline(false)
  await expect
    .poll(async () => (await graphql<{ session: { user: { id: string } | null } }>(page, '{ session { user { id } } }')).session.user, { timeout: 20_000 })
    .toBeNull()

  // Signed in again, the deleted change is not sent: the server never had it, and the device no longer has it.
  await signIn(page, user)
  await page.reload()
  await pageLoaded(page)
  const data = await graphql<{ refuelings: { volume: number }[] }>(page, 'query($v: UUID!) { refuelings(vehicleId: $v) { volume } }', { v: vehicle.id })
  expect(data.refuelings.map((r) => r.volume)).toEqual([35])
  await expect(page.getByText(/not synced/)).toHaveCount(0)
})

