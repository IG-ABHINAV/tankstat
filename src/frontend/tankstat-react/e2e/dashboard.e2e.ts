import { type Locator, type Page } from '@playwright/test'
import { expect, test } from './support/test.ts'
import { addExpense, addVehicle, graphql, logRefueling, openVehicle, pageLoaded, settled, signInAsNewUser } from './support/app.ts'
import { violations } from './support/axe.ts'
import { expectSoundLayout } from './support/layout.ts'

// Arranging a vehicle's dashboard (#211): a grid of tiles dragged, resized, pinned and taken off in place; the vehicle's, which everyone
// who sees it gets, and one's own, which wins for oneself alone.

// Reduced motion (#236): the grid's own media rule turns its transitions off, so a tile is where it is drawn the moment it is drawn and a
// drag by the pointer never starts beside a tile still moving into place.
test.use({ reducedMotion: 'reduce' })

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

/** The 15th of the month `n` months before this one, as `yyyy-MM-dd`: inside the charts' last 12 months whenever the tests run. */
const monthsAgo = (n: number) => {
  const day = new Date()
  day.setUTCDate(15)
  day.setUTCMonth(day.getUTCMonth() - n)
  return day.toISOString().slice(0, 10)
}

/** What sticks out of the chart tiles: a card body that scrolls, a legend or a note past its card, a picture under its least height. */
const chartTileProblems = (page: Page) =>
  page.evaluate(() =>
    [...document.querySelectorAll<HTMLElement>('[data-tile^="preset:"], [data-tile^="chart:"]')].flatMap((tile) => {
      const card = tile.querySelector<HTMLElement>('.MuiCard-root')!
      const body = card.lastElementChild as HTMLElement
      const box = card.getBoundingClientRect()
      const problems: string[] = []
      if (body.scrollHeight > body.clientHeight + 1) problems.push(`${tile.dataset.tile}: its body scrolls (${body.scrollHeight} > ${body.clientHeight})`)
      for (const part of tile.querySelectorAll<HTMLElement>('.MuiChartsLegend-root, figure > p')) {
        const r = part.getBoundingClientRect()
        if (r.top < box.top - 0.5 || r.bottom > box.bottom + 0.5) problems.push(`${tile.dataset.tile}: "${part.textContent?.slice(0, 40)}" outside its card`)
      }
      const picture = tile.querySelector<HTMLElement>('[role="img"]')
      if (picture && picture.getBoundingClientRect().height < 119.5) problems.push(`${tile.dataset.tile}: its picture is ${Math.round(picture.getBoundingClientRect().height)} px tall`)
      return problems
    }),
  )
/** The heights of a tile's chart surfaces (a donut per currency): its svgs but the legend's swatches, which are svgs too. */
const surfaces = (t: Locator) => t.evaluate((el) => [...el.querySelectorAll('[role="img"] svg')].map((svg) => svg.getBoundingClientRect().height).filter((h) => h > 40))
/** How many resize corners are displayed (not `display: none`). */
const cornersShown = (page: Page) => page.evaluate(() => [...document.querySelectorAll('.react-resizable-handle')].filter((h) => getComputedStyle(h).display !== 'none').length)

test('fits each chart in its tile, legend and notes inside and no scrollbar, and shows the resize corners only while arranging', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  // Fuel and an expense give Monthly costs a legend; a second currency a note under it (a scale per currency) and a second donut.
  await logRefueling(page, vehicle.id, { date: monthsAgo(2), odometer: 1000 })
  await logRefueling(page, vehicle.id, { date: monthsAgo(1), odometer: 1500, volume: 35, totalCost: 21000 })
  await addExpense(page, vehicle.id, { date: monthsAgo(1) })
  await addExpense(page, vehicle.id, { date: monthsAgo(1), amount: 40, currency: 'EUR' })
  await openVehicle(page, vehicle.id)
  const monthly = tile(page, 'preset:monthlyCosts')
  await expect(monthly.locator('.MuiChartsLegend-root')).toBeVisible()
  await expect(monthly.getByText('Each currency is drawn on a scale of its own; the amounts are in the tooltip and the table.')).toBeVisible()
  await expect.poll(() => surfaces(tile(page, 'preset:categories'))).toHaveLength(2)

  // The picture takes what the title and the notes leave: nothing scrolls, nothing is cut. The charts lay out after a frame: polled.
  await expect.poll(() => chartTileProblems(page)).toEqual([])
  expect(await page.locator('.react-resizable-handle').count()).toBeGreaterThan(0) // drawn by the library, and hidden
  expect(await cornersShown(page)).toBe(0)
  // An opened table scrolls inside the card, the picture keeping its least height.
  await monthly.getByRole('button', { name: 'Show the data as a table' }).click()
  await expect(monthly.getByRole('table', { name: 'Monthly costs' })).toBeVisible()
  await expect.poll(() => monthly.evaluate((t) => { const body = t.querySelector('.MuiCard-root')!.lastElementChild!; return body.scrollHeight > body.clientHeight })).toBe(true)
  expect((await monthly.locator('[role="img"]').boundingBox())!.height).toBeGreaterThanOrEqual(119.5)
  await monthly.getByRole('button', { name: 'Hide the table' }).click()

  // While arranging, every tile has its corner, and a taller tile gives its chart the room.
  await page.getByRole('button', { name: 'Customize' }).click()
  await expect(page.getByRole('button', { name: 'Save dashboard' })).toBeVisible()
  await expect.poll(() => cornersShown(page)).toBe(await page.locator('[data-tile]').count())
  const corner = (await monthly.locator('.react-resizable-handle').boundingBox())!
  expect([Math.round(corner.width), Math.round(corner.height)]).toEqual([44, 44])
  await expect.poll(() => chartTileProblems(page)).toEqual([])
  const before = Math.max(...(await surfaces(monthly)))
  await menu(page, 'Monthly costs', 'Taller')
  await expect.poll(async () => Math.max(...(await surfaces(monthly)))).toBeGreaterThan(before + 20)
  await page.getByRole('button', { name: 'Save dashboard' }).click()
  await expect(page.getByText('Saved.')).toBeVisible()
  await expect.poll(() => cornersShown(page)).toBe(0)
  await expect.poll(() => chartTileProblems(page)).toEqual([])
  await expectSoundLayout(page)
})

test('resizes a tile by its corner, drags one to the top, and drags a widget in from Add widget', async ({ page, isMobile }) => {
  test.skip(isMobile, 'a pointer\'s way; a phone has the menus')
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await openVehicle(page, vehicle.id)
  await expect(page.getByRole('heading', { name: 'Distance per month' })).toBeVisible()
  await page.getByRole('button', { name: 'Customize' }).click()
  await expect(page.getByRole('button', { name: 'Save dashboard' })).toBeVisible()
  // `page.mouse` never scrolls, and a press past the viewport lands on nothing: the grid is brought up under the top bar first, so the
  // figures and the first two charts' handles are on screen. Below them, the edit rows' height and the fonts decide: on CI's runner the
  // Consumption chart's handle was at 914 px of a 900 px viewport, and the drag never started.
  await region(page).locator('.react-grid-layout').evaluate((grid) => window.scrollBy(0, grid.getBoundingClientRect().top - 120))
  const drag = async (fromX: number, fromY: number, toX: number, toY: number) => {
    await page.mouse.move(fromX, fromY)
    await page.mouse.down()
    for (let step = 1; step <= 12; step++) await page.mouse.move(fromX + ((toX - fromX) * step) / 12, fromY + ((toY - fromY) * step) / 12)
    await page.mouse.up()
  }

  // Resize: the first figure's corner one column to the right makes it two columns wide.
  await settled(tile(page, 'thisMonth'))
  const grid = (await region(page).locator('.react-grid-layout').boundingBox())!
  const column = grid.width / 4
  const corner = (await tile(page, 'thisMonth').locator('.react-resizable-handle').boundingBox())!
  await drag(corner.x + corner.width - 12, corner.y + corner.height - 12, corner.x + corner.width - 12 + column, corner.y + corner.height - 12)
  await expect.poll(async () => (await tile(page, 'thisMonth').boundingBox())!.width).toBeGreaterThan(column * 1.5)

  // Drag: the Consumption chart's handle to the first figure's place; the others go round it.
  await settled(tile(page, 'preset:consumption'))
  await settled(tile(page, 'thisMonth'))
  const handle = (await tile(page, 'preset:consumption').locator('.tk-tile-handle').boundingBox())!
  const target = (await tile(page, 'thisMonth').boundingBox())!
  await drag(handle.x + handle.width / 2, handle.y + handle.height / 2, target.x + 24, target.y + 24)
  await expect.poll(async () => (await chartTitles(page))[0]).toBe('Consumption')

  // Drag in: a chart taken off, dragged back from the Add widget panel onto the grid's top left.
  await menu(page, 'Fuel price', 'Remove')
  await page.getByRole('button', { name: 'Add widget' }).click()
  await settled(region(page).locator('.react-grid-layout'))
  await page.getByRole('region', { name: 'Add widget' }).getByRole('button', { name: /Fuel price/ }).dragTo(region(page).locator('.react-grid-layout'), { targetPosition: { x: 40, y: 40 }, force: true }) // no "stable" wait: the grid above it settles late
  await expect(page.getByRole('status', { name: 'Last change to the arrangement' })).toHaveText('Fuel price placed.')
  await expect.poll(() => chartTitles(page)).toContain('Fuel price')

  await page.getByRole('button', { name: 'Save dashboard' }).click()
  await expect(page.getByText('Saved.')).toBeVisible()
  const layout = (await stored(page, vehicle.id)).shared!.tiles
  expect(layout.find((t) => t.id === 'thisMonth')).toMatchObject({ w: 2 })
  expect(layout.find((t) => t.id === 'preset:consumption')).toMatchObject({ x: 0 })
  expect(layout.some((t) => t.id === 'preset:fuelPrice')).toBe(true)
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

test('starts from the Usage preset, then copies a ready-made chart as one\'s own', async ({ page }) => {
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

  await page.getByRole('button', { name: 'Copy Distance per month as my chart' }).click()
  const dialog = page.getByRole('dialog', { name: 'Copy as my chart' })
  await expect(dialog.getByLabel('Title')).toHaveValue('Distance per month')
  await dialog.getByLabel('Title').fill('My distance')
  await dialog.getByRole('button', { name: 'Save chart' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByRole('heading', { name: 'My distance' })).toBeVisible() // at the first free spot, the original still there
  await expect(page.getByRole('heading', { name: 'Distance per month' })).toBeVisible()
})

// ---- a dragged tile follows the pointer (#227) ------------------------------------------------------------------

/** Pressed at the middle of `part` and moved in `steps`: after each step `part` is back under the pointer, a frame or two later, polled briefly. */
const followsThePointer = async (page: Page, part: Locator, steps: number, by: { x: number; y: number }) => {
  // In the middle of the screen first: the grid scrolls the page while a dragged pointer is within 50 px of its scroll container's edge
  // (react-grid-layout's edge scroll, `EDGE_THRESHOLD`), which moves the tile away from a pointer that stays put. Where the part starts
  // depends on the layout above it (CI's fonts put Monthly costs' handle at 866 of 900 px, and the pointer reached the edge at step 17).
  await part.evaluate((element) => element.scrollIntoView({ block: 'center', inline: 'center' }))
  // Measured once it holds still (`settled`): the tiles move to their places after a drop, and grow into the edit rows.
  await settled(part)
  const box = (await part.boundingBox())!
  const start = { x: box.x + box.width / 2, y: box.y + box.height / 2 }
  await page.mouse.move(start.x, start.y)
  await page.mouse.down()
  for (let step = 1; step <= steps; step++) {
    const pointer = { x: start.x + step * by.x, y: start.y + step * by.y }
    await page.mouse.move(pointer.x, pointer.y)
    const apart = async () => {
      const now = (await part.boundingBox())!
      return Math.round(Math.hypot(now.x + now.width / 2 - pointer.x, now.y + now.height / 2 - pointer.y))
    }
    await expect.poll(apart, { message: `step ${step}: the distance from the pointer`, timeout: 1000, intervals: [16, 16, 32, 64, 128] }).toBeLessThanOrEqual(4)
  }
}

test('keeps a dragged tile under the pointer at every step, above the spot it will drop on, and a resized one\'s corner too', async ({ page, isMobile }) => {
  test.skip(isMobile, 'a pointer\'s way; a phone has the menus')
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await logRefueling(page, vehicle.id)
  await openVehicle(page, vehicle.id)
  await expect(page.getByRole('heading', { name: 'Distance per month' })).toBeVisible()
  await page.getByRole('button', { name: 'Customize' }).click()
  await expect(page.getByRole('button', { name: 'Save dashboard' })).toBeVisible()

  // Monthly costs by its handle, 20 steps to the right and a little down (`page.mouse` never scrolls: the handle is in the first screen).
  // After each step the tile is drawn where the pointer is (React draws a document event's update in a task of its own). A tile frozen
  // until it crosses into another cell (#227) never gets there.
  const moved = tile(page, 'preset:monthlyCosts')
  await followsThePointer(page, moved.locator('.tk-tile-handle'), 20, { x: 14, y: 2 })
  // The grid's own drop spot shows where it will land, under the tile.
  const layers = await page.evaluate(() => ({
    tile: getComputedStyle(document.querySelector('[data-tile="preset:monthlyCosts"]')!).zIndex,
    spot: getComputedStyle(document.querySelector('.react-grid-placeholder')!).zIndex,
  }))
  await page.mouse.up()
  expect(Number(layers.tile)).toBeGreaterThan(Number(layers.spot))
  await expect(page.locator('.react-grid-placeholder')).toHaveCount(0)
  await expect(moved).not.toHaveClass(/react-draggable-dragging/)

  // The first figure by its corner, 10 steps to the right and a little down, within its column's room: the corner stays under the pointer
  // between the grid's steps too (the library draws a resized tile's size only when it renders, as a dragged one's place).
  const resized = tile(page, 'thisMonth')
  await followsThePointer(page, resized.locator('.react-resizable-handle'), 10, { x: 8, y: 2 })
  await page.mouse.up()
  await expect(page.locator('.react-grid-placeholder')).toHaveCount(0)
  await expect(resized).not.toHaveClass(/resizing/)
})

// ---- a figure's rows (#229) -------------------------------------------------------------------------------

/** Today as `yyyy-MM-dd` in UTC: the server's "this month" is UTC's (`StatsService.Today`), so a local date could fall in another month. */
const today = () => new Date().toISOString().slice(0, 10)
/** The figure cards whose content is cut: a card is `overflow: hidden`, so only its scroll height tells. */
const clippedFigures = (page: Page) =>
  page.evaluate(() =>
    [...document.querySelectorAll<HTMLElement>('[data-tile]')]
      .filter((t) => !t.dataset.tile!.includes(':'))
      .map((t) => ({ id: t.dataset.tile!, card: t.querySelector<HTMLElement>('.MuiCard-root')! }))
      .filter(({ card }) => card.scrollHeight > card.clientHeight + 1)
      .map(({ id, card }) => `${id}: ${card.scrollHeight} > ${card.clientHeight}`),
  )
const figureHeights = async (page: Page, vehicleId: string) => Object.fromEntries((await stored(page, vehicleId)).shared!.tiles.filter((t) => !t.id.includes(':')).map((t) => [t.id, t.h]))

test('makes figures shorter: each shows what its rows hold, down to what it needs, and nothing is cut', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  // Spent this month in two currencies: its value wraps in a desktop column, which is why it stops at three rows.
  await logRefueling(page, vehicle.id, { date: today() })
  await addExpense(page, vehicle.id, { date: today(), amount: 40, currency: 'EUR' })
  await openVehicle(page, vehicle.id)
  await expect(tile(page, 'thisMonth').getByText(/€40\.00/)).toBeVisible()
  await expect(tile(page, 'thisMonth').getByRole('img', { name: /^Spending in the last six months/ })).toBeVisible()
  await expect.poll(() => clippedFigures(page)).toEqual([])

  await page.getByRole('button', { name: 'Customize' }).click()
  await expect(page.getByRole('button', { name: 'Save dashboard' })).toBeVisible()
  await menu(page, 'Odometer', 'Shorter')
  await menu(page, 'Odometer', 'Shorter')
  await expect(page.getByRole('status', { name: 'Last change to the arrangement' })).toHaveText('Odometer is now 1 wide and 2 tall.')
  await page.getByRole('button', { name: 'Options for Odometer' }).click()
  await expect(page.getByRole('menuitem', { name: 'Shorter' })).toHaveAttribute('aria-disabled', 'true')
  await page.keyboard.press('Escape')
  await expect(page.getByRole('menu')).toBeHidden()
  await menu(page, 'Average consumption', 'Shorter')
  await menu(page, 'Average consumption', 'Shorter')
  await menu(page, 'Spent this month', 'Shorter')
  await expect(page.getByRole('status', { name: 'Last change to the arrangement' })).toHaveText('Spent this month is now 1 wide and 3 tall.')
  await page.getByRole('button', { name: 'Options for Spent this month' }).click()
  await expect(page.getByRole('menuitem', { name: 'Shorter' })).toHaveAttribute('aria-disabled', 'true')
  await page.keyboard.press('Escape')
  await expect(page.getByRole('menu')).toBeHidden()
  // While arranging, the rows are taller by the handle's room: a two-row figure keeps its content whole.
  await expect.poll(() => clippedFigures(page)).toEqual([])
  await page.getByRole('button', { name: 'Save dashboard' }).click()
  await expect(page.getByText('Saved.')).toBeVisible()

  await page.reload()
  await pageLoaded(page)
  await expect(tile(page, 'odometer').getByText(/km$/)).toBeVisible()
  expect(await figureHeights(page, vehicle.id)).toEqual({ thisMonth: 3, consumption: 2, odometer: 2, lastFillUp: 4 })
  // Two rows: the label and the value, no note. Three: the note, no sparkline. Four: everything.
  await expect(tile(page, 'consumption').getByText('last 10 full fill-ups')).toBeHidden()
  await expect(tile(page, 'thisMonth').getByText(/^Last month:/)).toBeVisible()
  await expect(tile(page, 'thisMonth').getByRole('img', { name: /^Spending in the last six months/ })).toHaveCount(0)
  await expect(tile(page, 'lastFillUp').getByText('1 fill-ups, 1 expenses')).toBeVisible()
  await expect.poll(() => clippedFigures(page)).toEqual([])
  await expectSoundLayout(page)
})
