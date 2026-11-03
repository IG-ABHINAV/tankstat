import { expect, test, type Page } from '@playwright/test'
import { addVehicle, graphql, logRefueling, openVehicle, pageLoaded, signInAsNewUser } from './support/app.ts'
import { violations } from './support/axe.ts'
import { expectSoundLayout } from './support/layout.ts'

// Arranging a vehicle's dashboard (#211): a grid of tiles dragged, resized, pinned and taken off in place; the vehicle's, which everyone
// who sees it gets, and one's own, which wins for oneself alone.

const region = (page: Page) => page.getByRole('region', { name: 'Dashboard' })
/** The charts' titles in the order the tiles come (the grid draws them in reading order). */
const chartTitles = (page: Page) => region(page).getByRole('heading', { level: 2 }).allTextContents()
const figureLabels = (page: Page) => region(page).locator('.MuiCard-root > p:first-child').allTextContents()
const tile = (page: Page, id: string) => page.locator(`[data-tile="${id}"]`)
const menu = async (page: Page, name: string, entry: string) => {
  await page.getByRole('button', { name: `Options for ${name}` }).click()
  await page.getByRole('menuitem', { name: entry }).click()
  await expect(page.getByRole('menu')).toBeHidden()
}
type Stored = { id: string; x: number; y: number; w: number; h: number; pinned: boolean }[]
const stored = async (page: Page, vehicleId: string) => {
  const data = await graphql<{ vehicle: { dashboard: { shared: { tiles: Stored } | null; personal: { tiles: Stored } | null } } }>(
    page,
    'query($id: UUID!) { vehicle(id: $id) { dashboard { shared { tiles { id x y w h pinned } } personal { tiles { id x y w h pinned } } } } }',
    { id: vehicleId },
  )
  return data.vehicle.dashboard
}

test('customizes the vehicle\'s dashboard by the tiles\' menus: a figure moved earlier and pinned, a chart taken off and one widened, kept after a reload', async ({ page, isMobile }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id)
  await openVehicle(page, vehicle.id)
  await expect.poll(() => chartTitles(page)).toEqual(['Monthly costs', 'Consumption', 'Fuel price', 'Expenses by category', 'Distance per month'])

  await page.getByRole('button', { name: 'Customize' }).click()
  await expect(page.getByRole('radio', { name: 'Everyone who sees this vehicle' })).toBeChecked()
  await expectSoundLayout(page)
  // Not over a button: Save dashboard takes Customize's place, and a hovered primary button falls short of the contrast (#235).
  await page.mouse.move(0, 0)
  expect(await violations(page)).toEqual([])
  await menu(page, 'Odometer', 'Move earlier')
  await expect(page.getByRole('status', { name: 'Last change to the arrangement' })).toHaveText('Odometer moved to position 2 of 9.')
  await menu(page, 'Odometer', 'Pin')
  await menu(page, 'Fuel price', 'Remove')
  // A phone's grid is one column: a width cannot be seen there, so neither Wider nor Narrower is offered, and the menu says so.
  await page.getByRole('button', { name: 'Options for Monthly costs' }).click()
  if (isMobile) await expect(page.getByRole('menuitem', { name: 'Wider' })).toHaveAttribute('aria-disabled', 'true')
  else await expect(page.getByRole('menuitem', { name: 'Wider' })).not.toHaveAttribute('aria-disabled', 'true')
  if (isMobile) await expect(page.getByRole('menuitem', { name: 'Narrower' })).toHaveAttribute('aria-disabled', 'true')
  if (isMobile) await page.keyboard.press('Escape')
  else await page.getByRole('menuitem', { name: 'Wider' }).click()
  await expect(page.getByRole('menu')).toBeHidden()
  await page.getByRole('button', { name: 'Save dashboard' }).click()
  await expect(page.getByText('Saved.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Save dashboard' })).toBeHidden()

  await page.reload()
  await pageLoaded(page)
  // Packed into the removed chart's place: Distance moves up under Monthly costs (beside Consumption, under a widened Monthly costs).
  await expect.poll(() => chartTitles(page)).toEqual(isMobile ? ['Monthly costs', 'Consumption', 'Distance per month', 'Expenses by category'] : ['Monthly costs', 'Distance per month', 'Consumption', 'Expenses by category'])
  expect(await figureLabels(page)).toEqual(['Spent this month', 'Odometer', 'Average consumption', 'Last fill-up'])
  const layout = (await stored(page, vehicle.id)).shared!.tiles
  expect(layout.find((t) => t.id === 'preset:monthlyCosts')).toMatchObject({ w: isMobile ? 2 : 3 })
  expect(layout.find((t) => t.id === 'odometer')).toMatchObject({ pinned: true })
  await expectSoundLayout(page)
})

test('resizes a tile by its corner and drags one to the top', async ({ page, isMobile }) => {
  test.skip(isMobile, 'a pointer\'s way; a phone has the menus')
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id) // so the first figure has its sparkline
  await openVehicle(page, vehicle.id)
  await expect(page.getByRole('heading', { name: 'Distance per month' })).toBeVisible()
  await page.getByRole('button', { name: 'Customize' }).click()
  await expect(page.getByRole('button', { name: 'Save dashboard' })).toBeVisible()
  // `page.mouse` never scrolls: everything dragged here is in the first screen (the figures and the first two charts).
  const drag = async (fromX: number, fromY: number, toX: number, toY: number) => {
    await page.mouse.move(fromX, fromY)
    await page.mouse.down()
    for (let step = 1; step <= 12; step++) await page.mouse.move(fromX + ((toX - fromX) * step) / 12, fromY + ((toY - fromY) * step) / 12)
    await page.mouse.up()
  }

  // The strip over a tile's top (#228) takes nothing from a four-row figure: its sparkline still ends inside the card.
  await expect(tile(page, 'thisMonth').locator('.MuiCard-root svg').last()).toBeVisible()
  const card = (await tile(page, 'thisMonth').locator('.MuiCard-root').boundingBox())!
  const sparkline = (await tile(page, 'thisMonth').locator('.MuiCard-root svg').last().boundingBox())!
  expect(sparkline.y + sparkline.height).toBeLessThanOrEqual(card.y + card.height + 0.5)
  await expectSoundLayout(page)

  // Resize: the first figure's corner one column to the right makes it two columns wide.
  const grid = (await region(page).locator('.react-grid-layout').boundingBox())!
  const column = grid.width / 4
  const corner = (await tile(page, 'thisMonth').locator('.react-resizable-handle').boundingBox())!
  await drag(corner.x + corner.width - 12, corner.y + corner.height - 12, corner.x + corner.width - 12 + column, corner.y + corner.height - 12)
  await expect.poll(async () => (await tile(page, 'thisMonth').boundingBox())!.width).toBeGreaterThan(column * 1.5)

  // Drag: the Consumption chart's handle to the first figure's place; the others go round it.
  const handle = (await tile(page, 'preset:consumption').locator('.tk-tile-handle').boundingBox())!
  const target = (await tile(page, 'thisMonth').boundingBox())!
  await drag(handle.x + handle.width / 2, handle.y + handle.height / 2, target.x + 24, target.y + 24)
  await expect.poll(async () => (await chartTitles(page))[0]).toBe('Consumption')

  await page.getByRole('button', { name: 'Save dashboard' }).click()
  await expect(page.getByText('Saved.')).toBeVisible()
  const layout = (await stored(page, vehicle.id)).shared!.tiles
  expect(layout.find((t) => t.id === 'thisMonth')).toMatchObject({ w: 2 })
  expect(layout.find((t) => t.id === 'preset:consumption')).toMatchObject({ x: 0 })
})

test('arranges a dashboard of one\'s own, then goes back to the vehicle\'s', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await openVehicle(page, vehicle.id)
  await expect(page.getByRole('heading', { name: 'Monthly costs' })).toBeVisible()

  await page.getByRole('button', { name: 'Customize' }).click()
  // The segment's label, as a person taps it (the radio inside is visually hidden).
  await page.locator('label', { hasText: 'Just me' }).click()
  await expect(page.getByRole('radio', { name: 'Just me' })).toBeChecked()
  for (const name of ['Monthly costs', 'Consumption', 'Fuel price', 'Expenses by category']) await menu(page, name, 'Remove')
  await page.getByRole('button', { name: 'Save dashboard' }).click()
  await expect(page.getByText('Saved.')).toBeVisible()

  await expect(page.getByText('You see your own arrangement of this dashboard.')).toBeVisible()
  await expect.poll(() => chartTitles(page)).toEqual(['Distance per month'])
  expect((await stored(page, vehicle.id)).personal!.tiles.map((t) => t.id)).toContain('preset:distance')
  await page.getByRole('button', { name: "Use the vehicle's dashboard" }).click()
  await expect(page.getByText('You see your own arrangement of this dashboard.')).toBeHidden()
  await expect.poll(() => chartTitles(page)).toEqual(['Monthly costs', 'Consumption', 'Fuel price', 'Expenses by category', 'Distance per month'])
})

test('starts from the Usage preset, then adds a figure as it is and a ready-made chart customized into one\'s own through Add widget', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id)
  await openVehicle(page, vehicle.id)
  await expect(page.getByRole('heading', { name: 'Monthly costs' })).toBeVisible()

  await page.getByRole('button', { name: 'Customize' }).click()
  await page.getByRole('combobox', { name: 'Start from a preset' }).click()
  await page.getByRole('option', { name: 'Usage' }).click()
  await expect(page.getByRole('status', { name: 'Last change to the arrangement' })).toHaveText('Started from Usage.')
  await page.getByRole('button', { name: 'Save dashboard' }).click()
  await expect(page.getByText('Saved.')).toBeVisible()
  await expect.poll(() => chartTitles(page)).toEqual(['Distance, this year and last', 'Consumption trend', 'Distance and consumption', 'Distance per month'])
  expect(await figureLabels(page)).toEqual(['Odometer', 'Distance this year', 'Average consumption', 'Consumption, latest fill-ups'])
  await expectSoundLayout(page)

  // Add widget (#228): a figure as it is, then a ready-made chart customized into a chart of one's own; both land at the bottom.
  await page.getByRole('button', { name: 'Customize' }).click()
  await page.getByRole('button', { name: 'Add widget' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add widget' })
  await dialog.getByRole('button', { name: 'Last fill-up' }).click()
  await expectSoundLayout(page)
  await dialog.getByRole('button', { name: 'Add to the dashboard' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByRole('status', { name: 'Last change to the arrangement' })).toHaveText('Last fill-up added.')

  await page.getByRole('button', { name: 'Add widget' }).click()
  await dialog.getByRole('button', { name: 'Monthly costs' }).click()
  await dialog.getByRole('button', { name: 'Customize this chart' }).click()
  await expect(dialog.getByLabel('Title')).toHaveValue('Monthly costs')
  await dialog.getByLabel('Title').fill('My costs')
  await expectSoundLayout(page)
  await dialog.getByRole('button', { name: 'Save and add' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByRole('status', { name: 'Last change to the arrangement' })).toHaveText('My costs added.')
  await page.getByRole('button', { name: 'Save dashboard' }).click()
  await expect(page.getByText('Saved.')).toBeVisible()
  await expect.poll(() => chartTitles(page)).toEqual(['Distance, this year and last', 'Consumption trend', 'Distance and consumption', 'Distance per month', 'My costs'])
  expect(await figureLabels(page)).toEqual(['Odometer', 'Distance this year', 'Average consumption', 'Consumption, latest fill-ups', 'Last fill-up'])
  // Outside Customize a tile shows only its content: no Copy, no Edit, the table a button away (on a chart with something to list).
  await expect(page.getByRole('button', { name: /^Copy |^Edit the chart|^Delete the chart/ })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'My costs as a table' })).toBeVisible()
  // The ready-made chart stays available, the saved copy is on the dashboard.
  await page.getByRole('button', { name: 'Customize' }).click()
  await page.getByRole('button', { name: 'Add widget' }).click()
  await expect(dialog.getByRole('button', { name: 'Monthly costs' })).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'My costs' })).toHaveCount(0)
})
