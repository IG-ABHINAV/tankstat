import { expect, test, newContext } from './support/test.ts'
import { addDueRecurring, addVehicle, createUser, openVehicle, pageLoaded, signIn, signInAsNewUser } from './support/app.ts'

// Sharing a vehicle's logs with someone else, what they then see and may do, and the notifications that tell people about it.

test("shares a vehicle's logs, and the other person logs a refuelling", async ({ page, browser }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  const friend = await createUser()

  await openVehicle(page, vehicle.id, 'sharing')
  await page.getByRole('combobox', { name: 'Person' }).click()
  await page.getByRole('option', { name: new RegExp(friend.name) }).click()
  await page.getByRole('button', { name: 'Give access' }).click()
  await expect(page.getByRole('combobox', { name: `Access of ${friend.name}` })).toBeVisible()

  const theirs = await newContext(browser)
  const them = await theirs.newPage()
  try {
    await signIn(them, friend)
    await them.goto('/')
    await pageLoaded(them)
    await expect(them.getByRole('link', { name: `Open ${vehicle.name}` })).toBeVisible()
    // The bell tells them about the new access.
    await expect(them.getByRole('button', { name: /^Notifications, \d+ unread$/ })).toBeVisible()

    await openVehicle(them, vehicle.id, 'refuelings')
    await them.getByRole('button', { name: 'Add refuelling' }).click()
    const dialog = them.getByRole('dialog', { name: 'Add refuelling' })
    await dialog.getByLabel(/^Volume/).fill('27.5')
    await dialog.getByLabel('Total cost').fill('16500')
    await dialog.getByLabel(/^Odometer/).fill('900')
    await dialog.getByRole('button', { name: 'Add refuelling' }).click()
    await expect(dialog).toBeHidden()
  } finally {
    await theirs.close()
  }

  // The owner sees it, logged by the friend.
  await openVehicle(page, vehicle.id, 'refuelings')
  await expect(page.getByRole('row').filter({ hasText: '27.5 L' })).toBeVisible()

  await openVehicle(page, vehicle.id, 'sharing')
  await page.getByRole('button', { name: `Remove access of ${friend.name}` }).click()
  await expect(page.getByText('Nobody has been given access to the logs yet.')).toBeVisible()
})

test('reads a due reminder in the notifications and marks it read', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await addDueRecurring(page, vehicle.id, 'Vignette')
  await page.goto('/')
  await pageLoaded(page)
  const text = `Vignette on ${vehicle.name} is due soon.`
  await page.getByRole('button', { name: /^Notifications, \d+ unread$/ }).click()
  const bell = page.getByRole('dialog', { name: 'Notifications' })
  await expect(bell.getByRole('list', { name: 'Latest notifications' }).getByText(text)).toBeVisible()
  await bell.getByRole('link', { name: 'Show all notifications' }).click()

  await expect(page.getByRole('heading', { level: 1, name: 'Notifications' })).toBeVisible()
  const reminder = page.getByRole('listitem').filter({ hasText: text })
  await reminder.getByRole('button', { name: 'Mark read' }).click()
  await expect(reminder.getByRole('button', { name: 'Mark read' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Notifications', exact: true })).toBeVisible()
})
