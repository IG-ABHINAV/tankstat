import { test } from './support/test.ts'
import { addVehicle, pageLoaded, signIn } from './support/app.ts'
import { expectSoundLayout } from './support/layout.ts'

test.beforeEach(async ({ page }) => signIn(page))

test('the home page lays out, with a vehicle card', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await page.goto('/')
  await pageLoaded(page)
  // Found by its name: the page lists a page of vehicles by name, and the other tests' vehicles may fill it.
  await page.getByLabel('Search vehicles').fill(vehicle.name)
  await page.getByRole('link', { name: vehicle.name }).waitFor()
  await expectSoundLayout(page)
})

for (const [name, path] of [
  ['the Overview', '/admin'],
  ['the Backups page', '/admin/backups'],
  ['the account page', '/account'],
  ['the People page', '/admin/people'],
] as const) {
  test(`${name} lays out`, async ({ page }) => {
    await page.goto(path)
    await pageLoaded(page)
    await expectSoundLayout(page)
  })
}
