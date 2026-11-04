import { expect, test } from './support/test.ts'
import { addVehicle, createUser, graphql, openVehicle, pageLoaded, signIn, signInAsNewUser } from './support/app.ts'
import { violations } from './support/axe.ts'

// axe in a real browser: what jsdom cannot judge (the colour contrast of the real styles, what is hidden after layout). The jsdom scans
// in tests/app/Accessibility*.integration.test.tsx stay; these add the pages as they are drawn, at both sizes.

test.beforeEach(async ({ page }) => signIn(page))

for (const [name, path] of [
  ['the home page', '/'],
  ['the Overview', '/admin'],
  ['the Server settings', '/admin/settings'],
  ['the Backups page', '/admin/backups'],
  ['the account page', '/account'],
  ['the People page', '/admin/people'],
] as const) {
  test(`${name} has no axe violations`, async ({ page }) => {
    await page.goto(path)
    await pageLoaded(page)
    expect(await violations(page)).toEqual([])
  })
}

test('a vehicle page and its add refuelling dialog have no axe violations', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await page.goto(`/vehicles/${vehicle.id}?tab=refuelings`)
  await pageLoaded(page)
  expect(await violations(page)).toEqual([])
  await page.getByRole('button', { name: 'Add refuelling' }).first().click()
  const dialog = page.getByRole('dialog', { name: 'Add refuelling' })
  await expect(dialog).toBeVisible()
  expect(await violations(page, '[role="dialog"]')).toEqual([])
})

test('a vehicle\'s dashboard, shown and being arranged, has no axe violations', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await page.goto(`/vehicles/${vehicle.id}`)
  await pageLoaded(page)
  await expect(page.getByRole('heading', { name: 'Monthly costs' })).toBeVisible()
  expect(await violations(page)).toEqual([])
  await page.getByRole('button', { name: 'Customize' }).click()
  await expect(page.getByRole('button', { name: 'Save dashboard' })).toBeVisible()
  expect(await violations(page)).toEqual([])
  await page.getByRole('button', { name: 'Add widget' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add widget' })
  await expect(dialog).toBeVisible()
  await expect(page.locator('.MuiDialog-container')).toHaveCSS('opacity', '1') // faded in: axe reads the colours as they are drawn
  expect(await violations(page, '[role="dialog"]')).toEqual([])
  await dialog.getByRole('button', { name: 'Blank chart' }).click()
  await expect(dialog.getByLabel('Title')).toBeVisible()
  expect(await violations(page, '[role="dialog"]')).toEqual([])
})

// The solid fills (#235): a primary button under the pointer, and a red confirmation at rest and under it, in both schemes; a hover is
// a pointer's, so on a desktop only. Each scan waits for the fill to be the hover's (MUI's 250 ms transition), as it is drawn.
for (const scheme of ['dark', 'light'] as const) {
  test(`a hovered primary button, a red confirmation and the bell's badge reach 4.5:1, ${scheme}`, async ({ page, request, isMobile }) => {
    test.skip(isMobile, 'a hover is a pointer\'s')
    if (scheme === 'light') await page.addInitScript(() => localStorage.setItem('tankstat.colorMode', 'light'))
    // A user of its own, whom someone else grants access to their vehicle: the bell then has an unread count.
    const me = await signInAsNewUser(page)
    const owner = await createUser()
    await signIn(request, owner)
    const theirs = await addVehicle(request)
    await graphql(request, 'mutation($i: SetLogAccessInput!) { setVehicleLogAccess(input: $i) }', { i: { vehicleId: theirs.id, userId: me.id, level: 'EDIT' } })
    const vehicle = await addVehicle(page)
    await page.goto('/')
    await pageLoaded(page)
    // The fills are the same in both schemes, so only the class tells that the scheme asked for is the one drawn.
    await expect(page.locator('html')).toHaveClass(scheme === 'light' ? /\blight\b/ : /\bdark\b/)
    // The badge is aria-hidden, so axe skips it: its fill is checked as drawn, the red's solid one (white on it is 4.88:1).
    await expect(page.getByRole('button', { name: /^Notifications, \d+ unread$/ })).toBeVisible()
    await expect(page.locator('.MuiBadge-badge')).toHaveCSS('background-color', 'rgb(202, 63, 68)')
    // The add vehicle dialog's submit is a contained primary button (the toolbar's own is a soft one).
    await page.getByRole('button', { name: 'Add vehicle' }).first().click()
    const dialog = page.getByRole('dialog', { name: 'Add vehicle' })
    await dialog.getByLabel('Name').fill('Hovered')
    const add = dialog.getByRole('button', { name: 'Add vehicle' })
    await add.hover()
    await expect(add).toHaveCSS('background-color', 'rgb(56, 89, 199)') // the primary's solid hover, #3859c7
    expect(await violations(page, '[role="dialog"]')).toEqual([])
    await dialog.getByRole('button', { name: 'Cancel' }).click()
    await expect(dialog).toBeHidden()

    await openVehicle(page, vehicle.id, 'details')
    await page.getByRole('region', { name: 'Delete this vehicle' }).getByRole('button', { name: 'Move to trash' }).click()
    const confirm = page.getByRole('alertdialog', { name: `Move ${vehicle.name} to the trash?` }).getByRole('button', { name: 'Move to trash' })
    await page.mouse.move(0, 0)
    await expect(confirm).toHaveCSS('background-color', 'rgb(202, 63, 68)') // the red's solid fill, #ca3f44
    expect(await violations(page, '[role="alertdialog"]')).toEqual([])
    await confirm.hover()
    await expect(confirm).toHaveCSS('background-color', 'rgb(183, 58, 62)') // and its hover, #b73a3e
    expect(await violations(page, '[role="alertdialog"]')).toEqual([])
  })
}

test('a trend tile\'s period popover has no axe violations (#230)', async ({ page }) => {
  const vehicle = await addVehicle(page)
  await graphql(page, 'mutation($i: SaveDashboardLayoutInput!) { saveDashboardLayout(input: $i) }', {
    i: { vehicleId: vehicle.id, personal: false, tiles: [{ id: 'spendingTrend', x: 0, y: 0, w: 1, h: 4, pinned: false }], hidden: [] },
  })
  await page.goto(`/vehicles/${vehicle.id}`)
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Spending period: 12 full months. Change it' }).click()
  const popover = page.getByRole('dialog', { name: 'Period of Spending' })
  await expect(popover.getByTestId('trend-timeline')).toBeVisible()
  expect(await violations(page, '[role="dialog"]')).toEqual([])
  await popover.getByRole('tab', { name: 'Last' }).click()
  await expect(popover.getByRole('switch', { name: 'Whole weeks, Monday to Sunday' })).toBeVisible()
  expect(await violations(page, '[role="dialog"]')).toEqual([])
})

test('the add vehicle dialog has no axe violations', async ({ page }) => {
  await page.goto('/')
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Add vehicle' }).first().click()
  await expect(page.getByRole('dialog', { name: 'Add vehicle' })).toBeVisible()
  expect(await violations(page, '[role="dialog"]')).toEqual([])
})
