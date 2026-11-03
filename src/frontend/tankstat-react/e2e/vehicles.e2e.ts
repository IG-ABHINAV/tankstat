import { expect, test } from './support/test.ts'
import { addVehicle, openVehicle, pageLoaded, picture, signInAsNewUser, tag } from './support/app.ts'

// A vehicle's own life: its details, its picture, the trash and back, the search on the home page. A user of the test's own each.

test.beforeEach(async ({ page }) => {
  await signInAsNewUser(page)
})

test('edits a vehicle', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await openVehicle(page, vehicle.id, 'details')
  await page.getByRole('button', { name: 'Edit vehicle' }).click()
  const dialog = page.getByRole('dialog', { name: 'Edit vehicle' })
  const name = `Renamed ${tag()}`
  await dialog.getByLabel('Name').fill(name)
  await dialog.getByLabel('License plate (optional)').fill('NEW-123')
  await dialog.getByRole('button', { name: 'Save changes' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
  await expect(page.getByRole('definition').filter({ hasText: 'NEW-123' })).toBeVisible()
})

test('gives a vehicle a picture', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await openVehicle(page, vehicle.id, 'details')
  const region = page.getByRole('region', { name: 'Picture' })
  const chooser = page.waitForEvent('filechooser')
  await region.getByRole('button', { name: 'Choose a picture' }).click()
  await (await chooser).setFiles(await picture(page))
  await expect(region.getByRole('img', { name: 'No picture' })).toHaveCount(0)
  await expect(region.getByRole('button', { name: 'Change picture' })).toBeVisible()
})

test('moves a vehicle to the trash, restores it, then empties the trash', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await openVehicle(page, vehicle.id, 'details')
  await page.getByRole('region', { name: 'Delete this vehicle' }).getByRole('button', { name: 'Move to trash' }).click()
  await page.getByRole('alertdialog', { name: `Move ${vehicle.name} to the trash?` }).getByRole('button', { name: 'Move to trash' }).click()
  await pageLoaded(page)
  await expect(page.getByRole('link', { name: `Open ${vehicle.name}` })).toHaveCount(0)

  await page.goto('/trash')
  await pageLoaded(page)
  await page.getByRole('button', { name: `Restore ${vehicle.name}` }).click()
  await expect(page.getByRole('row').filter({ hasText: vehicle.name })).toHaveCount(0)
  await page.goto('/')
  await pageLoaded(page)
  await expect(page.getByRole('link', { name: `Open ${vehicle.name}` })).toBeVisible()

  await openVehicle(page, vehicle.id, 'details')
  await page.getByRole('region', { name: 'Delete this vehicle' }).getByRole('button', { name: 'Move to trash' }).click()
  await page.getByRole('alertdialog', { name: `Move ${vehicle.name} to the trash?` }).getByRole('button', { name: 'Move to trash' }).click()
  await pageLoaded(page)
  await page.goto('/trash')
  await pageLoaded(page)
  await expect(page.getByRole('row').filter({ hasText: vehicle.name })).toBeVisible()
  await page.getByRole('button', { name: 'Empty trash' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: /Empty|Delete/ }).click()
  await expect(page.getByText('The trash is empty.')).toBeVisible()
})

test('finds a vehicle by its name or license plate', async ({ page }) => {
  const first = await addVehicle(page, `Alpha ${tag()}`)
  const second = await addVehicle(page, `Bravo ${tag()}`)
  await page.goto('/')
  await pageLoaded(page)
  const search = page.getByLabel('Search vehicles')
  await search.fill('Bravo')
  await expect(page.getByRole('link', { name: `Open ${second.name}` })).toBeVisible()
  await expect(page.getByRole('link', { name: `Open ${first.name}` })).toHaveCount(0)
  await search.fill('E2E-001')
  await expect(page.getByRole('link', { name: `Open ${first.name}` })).toBeVisible()
  await expect(page.getByRole('link', { name: `Open ${second.name}` })).toBeVisible()
})

test('arranges the vehicles on the home page', async ({ page }) => {
  const first = await addVehicle(page, `Alpha ${tag()}`)
  const second = await addVehicle(page, `Bravo ${tag()}`)
  await page.goto('/')
  await pageLoaded(page)
  const cards = page.getByRole('list', { name: /vehicles$/ }).getByRole('heading', { level: 2 })
  await expect(cards).toHaveText([`Open ${first.name}`, `Open ${second.name}`].map((n) => new RegExp(n.replace('Open ', ''))))

  await page.getByRole('button', { name: 'Arrange' }).click()
  const dialog = page.getByRole('dialog', { name: 'Arrange vehicles' })
  await dialog.getByRole('button', { name: `Move ${second.name} up` }).click()
  await dialog.getByRole('button', { name: 'Save order' }).click()
  await expect(dialog).toBeHidden()
  await expect(cards).toHaveText([new RegExp(second.name), new RegExp(first.name)])
  await page.reload()
  await pageLoaded(page)
  await expect(cards).toHaveText([new RegExp(second.name), new RegExp(first.name)])
})
