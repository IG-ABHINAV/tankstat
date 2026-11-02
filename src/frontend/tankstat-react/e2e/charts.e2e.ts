import { expect, test, type Page } from '@playwright/test'
import { addVehicle, graphql, openVehicle, signInAsNewUser } from './support/app.ts'
import { expectSoundLayout } from './support/layout.ts'

// The dashboard's charts as they are drawn (jsdom lays nothing out): costs in two currencies, each on a scale of its own (#159).

/** The first of the month `back` months before this one, as `yyyy-MM-dd` (never in the future, which a log may not be). */
const monthStart = (back: number) => {
  const now = new Date()
  return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - back, 1)).toISOString().slice(0, 10)
}

/** What a chart's bars and period labels measure: each bar's box and colour, and the labels along the bottom. */
async function measureChart(page: Page, name: string) {
  // The chart's own figure, by its exact name ("By category" would also match the preset "Expenses by category").
  const figure = page.getByRole('img', { name: `${name}: chart`, exact: true }).locator('xpath=ancestor::figure[1]')
  return figure.evaluate((root) => {
    // The widest svg is the chart: the legend's swatches are svgs too.
    const svg = [...root.querySelectorAll('svg')].sort((a, b) => b.getBoundingClientRect().width - a.getBoundingClientRect().width)[0]
    const bars = [...svg.querySelectorAll('.MuiBarChart-element')].map((bar) => {
      const box = bar.getBoundingClientRect()
      return { x: box.x, width: box.width, height: box.height, fill: bar.getAttribute('fill') ?? '' }
    })
    const labels = [...svg.querySelectorAll('[class*="directionX"] text')].map((text) => text.textContent ?? '')
    return { bars, labels }
  })
}

test('draws a chart of costs in two currencies on a scale for each, with only the currencies a month has, and its months named', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  // Euros every month of the last year (54 to 274), forints in every third (about 38,000): one shared axis made the euros invisible.
  const euros = [155.19, 54, 274, 120, 98.5, 210, 66, 180, 140, 75, 233, 68.12]
  let odometer = 10_000
  for (let i = 0; i < 12; i++) {
    const date = monthStart(11 - i)
    odometer += 500
    await graphql(page, 'mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }', {
      i: { vehicleId: vehicle.id, date, volume: 40, totalCost: euros[i], currency: 'EUR', odometer, isFullTank: true },
    })
    if (i % 3 === 0) {
      odometer += 500
      await graphql(page, 'mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }', {
        i: { vehicleId: vehicle.id, date, volume: 40, totalCost: 38_000 - i * 100, currency: 'HUF', odometer, isFullTank: true },
      })
    }
  }
  await openVehicle(page, vehicle.id)
  await expect(page.getByText('Each currency is drawn on a scale of its own; the amounts are in the tooltip and the table.').first()).toBeVisible()

  await expect.poll(async () => (await measureChart(page, 'Monthly costs')).bars.length).toBe(16)
  const { bars, labels } = await measureChart(page, 'Monthly costs')
  const colours = [...new Set(bars.map((bar) => bar.fill))]
  expect(colours).toHaveLength(2)
  // A colour per currency: the forints in 4 months, the euros in all 12, each listed by month, left to right.
  const ofColour = (fill: string) => bars.filter((bar) => bar.fill === fill).sort((a, b) => a.x - b.x)
  const [forints, eurosDrawn] = colours.map(ofColour).sort((a, b) => a.length - b.length)
  expect([forints.length, eurosDrawn.length]).toEqual([4, 12])

  // Each currency on its own scale: the highest month of each reaches the same height.
  const tallest = (list: typeof bars) => Math.max(...list.map((bar) => bar.height))
  expect(Math.abs(tallest(forints) - tallest(eurosDrawn))).toBeLessThanOrEqual(2)

  // One width for every bar; a month with euros only has its bar in the middle, a month with both has them side by side around it.
  const width = eurosDrawn[0].width
  for (const bar of bars) expect(Math.abs(bar.width - width)).toBeLessThanOrEqual(1)
  const centre = (bar: (typeof bars)[number]) => bar.x + bar.width / 2
  const step = centre(eurosDrawn[2]) - centre(eurosDrawn[1]) // two months with euros only
  const middleOf = (month: number) => centre(eurosDrawn[1]) + (month - 1) * step
  for (const [k, month] of [0, 3, 6, 9].entries()) {
    expect(Math.abs((centre(forints[k]) + centre(eurosDrawn[month])) / 2 - middleOf(month))).toBeLessThanOrEqual(2)
    expect(centre(forints[k])).toBeLessThan(centre(eurosDrawn[month]))
  }
  for (const month of [1, 2, 4, 5, 7, 8, 10, 11]) expect(Math.abs(centre(eurosDrawn[month]) - middleOf(month))).toBeLessThanOrEqual(2)

  // Short month names fit: at least half of them, on a phone too (a dashboard card is narrow at both sizes), none cut short, and every
  // year the chart covers named under one of them, whichever months the axis leaves out.
  expect(labels.filter(Boolean).length).toBeGreaterThanOrEqual(6)
  expect(labels.filter((label) => label.includes('…'))).toEqual([])
  const years = [...new Set([0, 11].map((back) => monthStart(back).slice(0, 4)))]
  for (const year of years) expect(labels.join(' ')).toContain(year)
  await expectSoundLayout(page)
})

test('draws a moving average and a year earlier as lines over the bars, on the bars\' scale, the bars still one width and evenly placed', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  // A refuelling every month of the last two years, so the year earlier has a value in every month too.
  let odometer = 10_000
  for (let back = 23; back >= 0; back--) {
    odometer += 500
    await graphql(page, 'mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }', {
      i: { vehicleId: vehicle.id, date: monthStart(back), volume: 40, totalCost: 20_000 + (back % 5) * 2_000, currency: 'HUF', odometer, isFullTank: true },
    })
  }
  await graphql(page, 'mutation($i: SaveChartInput!) { saveVehicleChart(input: $i) { id } }', {
    i: {
      vehicleId: vehicle.id,
      title: 'Costs and trend',
      shared: false,
      config: { metric: 'TOTAL_SPEND', grouping: 'MONTH', kind: 'BAR', range: 'LAST12_MONTHS', stacked: false, trend: 'MOVING_AVERAGE', compareYearEarlier: true },
    },
  })
  await openVehicle(page, vehicle.id)

  await expect.poll(async () => (await measureChart(page, 'Costs and trend')).bars.length).toBe(12)
  const { bars } = await measureChart(page, 'Costs and trend')
  const width = bars[0].width
  for (const bar of bars) expect(Math.abs(bar.width - width)).toBeLessThanOrEqual(1)
  const centres = bars.map((bar) => bar.x + bar.width / 2).sort((a, b) => a - b)
  const step = centres[1] - centres[0]
  for (let i = 1; i < centres.length; i++) expect(Math.abs(centres[i] - centres[i - 1] - step)).toBeLessThanOrEqual(2)

  // The two lines: drawn (a path with a shape), dashed or dotted, and inside the chart's own box.
  const figure = page.getByRole('img', { name: 'Costs and trend: chart', exact: true }).locator('xpath=ancestor::figure[1]')
  const lines = await figure.evaluate((root) =>
    ['MOVING_AVERAGE', 'YEAR_EARLIER'].map((role) => {
      const path = root.querySelector(`path[data-series$="|${role}"]`)
      const box = path?.getBoundingClientRect()
      const svg = path?.closest('svg')?.getBoundingClientRect()
      return {
        role,
        drawn: Boolean(path?.getAttribute('d')),
        dashed: path ? getComputedStyle(path).strokeDasharray !== 'none' : false,
        inside: Boolean(box && svg && box.left >= svg.left - 1 && box.right <= svg.right + 1 && box.top >= svg.top - 1 && box.bottom <= svg.bottom + 1),
      }
    }),
  )
  expect(lines).toEqual([
    { role: 'MOVING_AVERAGE', drawn: true, dashed: true, inside: true },
    { role: 'YEAR_EARLIER', drawn: true, dashed: true, inside: true },
  ])
  await expectSoundLayout(page)
})

test('a saved chart of the fuel price before discounts adds the discount back, while the preset Fuel price stays on what was paid', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  await graphql(page, 'mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }', {
    i: { vehicleId: vehicle.id, date: monthStart(1), volume: 43, totalCost: 34744, discount: 430, currency: 'HUF', odometer: 1000, isFullTank: true },
  })
  await graphql(page, 'mutation($i: SaveChartInput!) { saveVehicleChart(input: $i) { id } }', {
    i: { vehicleId: vehicle.id, title: 'Pump price', shared: false, config: { metric: 'AVERAGE_PRICE_PER_UNIT_BEFORE_DISCOUNT', grouping: 'MONTH', kind: 'LINE', range: 'LAST12_MONTHS', stacked: false } },
  })
  await openVehicle(page, vehicle.id)

  const figure = page.getByRole('img', { name: 'Pump price: chart', exact: true }).locator('xpath=ancestor::figure[1]')
  await figure.getByRole('button', { name: 'Show the data as a table' }).click()
  const table = page.getByRole('table', { name: 'Pump price' })
  await expect(table).toContainText('818') // (34 744 + 430) / 43: the pump's price
  await expect(table).not.toContainText('808') // the price paid is the preset's
})

test('names a category of a bar chart on its axis, and the expenses without one as Uncategorised', async ({ page }) => {
  await signInAsNewUser(page)
  const vehicle = await addVehicle(page)
  for (const [category, amount] of [['Parking', 4000], [null, 2500]] as const) {
    await graphql(page, 'mutation($i: AddExpenseInput!) { addExpense(input: $i) { id } }', {
      i: { vehicleId: vehicle.id, date: monthStart(1), title: 'Fee', amount, currency: 'HUF', category },
    })
  }
  await graphql(page, 'mutation($i: SaveChartInput!) { saveVehicleChart(input: $i) { id } }', {
    i: { vehicleId: vehicle.id, title: 'By category', shared: false, config: { metric: 'EXPENSE_COST', grouping: 'CATEGORY', kind: 'BAR', range: 'LAST12_MONTHS', stacked: false } },
  })
  await openVehicle(page, vehicle.id)

  await expect.poll(async () => (await measureChart(page, 'By category')).labels.filter(Boolean).sort()).toEqual(['Parking', 'Uncategorised'])
})
