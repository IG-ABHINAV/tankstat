import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { expect, it } from 'vitest'
import { ChartView } from '../../../src/dashboard/ChartView.tsx'
import type { ChartData, ChartRecipe, Series } from '../../../src/dashboard/chartFormat.ts'
import { ThemeRoot } from '../../../src/theme/ThemeRoot.tsx'

const units = { distance: 'KILOMETERS', volume: 'LITERS' } as const

/** A test chart's data, its series written without their measure: the first measure's, in the chart's unit, as the server stamps them. */
type Loose = { unit: ChartData['unit']; series: (Omit<Series, 'metric' | 'unit'> & Partial<Pick<Series, 'metric' | 'unit'>>)[] }
const typed = (data: Loose): ChartData => ({ ...data, series: data.series.map((s) => ({ metric: 'TOTAL_SPEND' as const, unit: data.unit, ...s })) })

const money: Loose = {
  unit: 'CURRENCY',
  series: [
    { role: 'VALUE', slope: null, kind: 'fuel', currency: 'HUF', points: [{ key: '2026-08', value: 12000 }, { key: '2026-09', value: 15500 }] },
    { role: 'VALUE', slope: null, kind: 'expenses', currency: 'HUF', points: [{ key: '2026-08', value: 3000 }, { key: '2026-09', value: null }] },
  ],
}
const recipe = (kind: ChartRecipe['kind']): Pick<ChartRecipe, 'kind' | 'metric' | 'grouping' | 'stacked'> => ({ kind, metric: 'TOTAL_SPEND', grouping: 'MONTH', stacked: kind === 'BAR' })

const view = (data: Loose, kind: ChartRecipe['kind']) =>
  render(
    <ThemeRoot instant>
      <ChartView data={typed(data)} recipe={recipe(kind)} units={units} title="Monthly costs" />
    </ThemeRoot>,
  )

it.each(['BAR', 'LINE', 'AREA', 'DONUT'] as const)('draws a %s chart as a picture hidden from assistive technology, named once as an image', async (kind) => {
  view(money, kind)

  const image = screen.getByRole('img', { name: 'Monthly costs: chart' })
  expect(image.querySelector('svg')).not.toBeNull()
  expect(image.querySelector('[aria-hidden="true"]')).not.toBeNull() // the drawing says nothing itself
  expect(within(image).queryAllByRole('img', { hidden: false })).toHaveLength(0)
  expect(screen.queryByRole('table')).not.toBeInTheDocument() // the numbers are behind the toggle
})

it('lists the same numbers as a table behind a toggle, with a dash for a missing value', async () => {
  const ui = userEvent.setup()
  view(money, 'BAR')

  await ui.click(screen.getByRole('button', { name: 'Show the data as a table' }))

  const table = screen.getByRole('table', { name: 'Monthly costs' })
  expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Period', 'Fuel · HUF', 'Expenses · HUF'])
  expect(within(table).getByRole('rowheader', { name: 'Sep 2026' }).closest('tr')).toHaveTextContent(/15,500.*–/)
  await ui.click(screen.getByRole('button', { name: 'Hide the table' }))
  expect(screen.queryByRole('table')).not.toBeInTheDocument()
})

it('says so instead of drawing an empty chart', () => {
  view({ unit: 'CURRENCY', series: [{ role: 'VALUE', slope: null, kind: 'total', currency: 'HUF', points: [{ key: '2026-08', value: 0 }] }] }, 'LINE')

  expect(screen.getByRole('status')).toHaveTextContent('No data for this period yet.')
  expect(screen.queryByRole('img')).not.toBeInTheDocument()
})

const tableOf = async (data: Loose) => {
  const ui = userEvent.setup()
  view(data, 'LINE')
  await ui.click(screen.getByRole('button', { name: 'Show the data as a table' }))
  return screen.getByRole('table', { name: 'Monthly costs' })
}

it.each([
  ['VOLUME', 42.5, '42.5 L'],
  ['DISTANCE', 1234, '1,234 km'],
  ['CONSUMPTION', 6.75, '6.75 L/100 km'],
  ['COUNT', 3, '3'],
] as const)('writes %s values in the vehicle\'s units in the table', async (unit, value, text) => {
  const table = await tableOf({ unit, series: [{ role: 'VALUE', slope: null, kind: 'total', currency: null, points: [{ key: '2026-09', value }] }] })

  expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Period', 'Total'])
  expect(within(table).getByRole('rowheader', { name: 'Sep 2026' }).closest('tr')).toHaveTextContent(`Sep 2026${text}`)
})

it('lists a chart by category with the expenses that have none as Uncategorised', async () => {
  const ui = userEvent.setup()
  render(
    <ThemeRoot instant>
      <ChartView
        data={typed({ unit: 'CURRENCY', series: [{ role: 'VALUE', slope: null, kind: 'expenses', currency: 'HUF', points: [{ key: 'Service', value: 35000 }, { key: '', value: 1500 }] }] })}
        recipe={{ kind: 'DONUT', metric: 'EXPENSE_COST', grouping: 'CATEGORY', stacked: false }}
        units={units}
        title="Expenses by category"
      />
    </ThemeRoot>,
  )

  await ui.click(screen.getByRole('button', { name: 'Show the data as a table' }))

  const table = screen.getByRole('table', { name: 'Expenses by category' })
  expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Category', 'Expenses · HUF'])
  expect(within(table).getAllByRole('rowheader').map((h) => h.textContent)).toEqual(['Service', 'Uncategorised'])
})

const twoCurrencies: Loose = {
  unit: 'CURRENCY',
  series: [
    { role: 'VALUE', slope: null, kind: 'fuel', currency: 'HUF', points: [{ key: '2026-08', value: 38000 }, { key: '2026-09', value: 0 }] },
    { role: 'VALUE', slope: null, kind: 'fuel', currency: 'EUR', points: [{ key: '2026-08', value: 54 }, { key: '2026-09', value: 155.19 }] },
  ],
}

it('with several currencies says each has a scale of its own, and lists a period without spending in one with a dash, not as 0', async () => {
  const ui = userEvent.setup()
  view(twoCurrencies, 'BAR')

  const note = screen.getByText('Each currency is drawn on a scale of its own; the amounts are in the tooltip and the table.')
  // A note under the chart, not the figure's caption: the chart's title names the figure (a caption in its middle would, and is invalid).
  expect(note.tagName).toBe('P')
  expect(note.closest('figure')?.querySelector('figcaption')).toBeNull()
  await ui.click(screen.getByRole('button', { name: 'Show the data as a table' }))
  const table = screen.getByRole('table', { name: 'Monthly costs' })
  expect(within(table).getByRole('rowheader', { name: 'Sep 2026' }).closest('tr')).toHaveTextContent(/^Sep 2026–€155\.19$/)
  expect(within(table).getByRole('rowheader', { name: 'Aug 2026' }).closest('tr')).toHaveTextContent(/^Aug 2026HUF\s38,000€54\.00$/)
})

it.each(['LINE', 'AREA'] as const)('a %s chart of several currencies says so too', (kind) => {
  view(twoCurrencies, kind)
  expect(screen.getByText(/scale of its own/)).toBeInTheDocument()
})

it('with one currency there is one scale, read by its axis, and nothing to say about it', () => {
  view(money, 'BAR')
  expect(screen.queryByText(/scale of its own/)).not.toBeInTheDocument()
})

it('names a series of a kind it does not know plainly, and writes an amount without a currency as a bare number', async () => {
  const table = await tableOf({ unit: 'CURRENCY', series: [{ role: 'VALUE', slope: null, kind: 'price', currency: null, points: [{ key: '2026-09', value: 12000 }] }] })

  expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Period', 'Value'])
  expect(within(table).getByRole('rowheader', { name: 'Sep 2026' }).closest('tr')).toHaveTextContent('Sep 202612,000.00')
})

// ---- lines drawn from the values -------------------------------------------------------------------------------

const line = (role: Series['role'], values: (number | null)[], slope: number | null = null): Loose['series'][number] => ({
  kind: 'total',
  currency: 'HUF',
  role,
  slope,
  points: values.map((value, i) => ({ key: `2026-0${i + 7}`, value })),
})

const drawn = (data: Loose, over: Partial<ChartRecipe> = {}, viewUnits: typeof units | { distance: 'MILES'; volume: 'US_GALLONS' } = units) =>
  render(
    <ThemeRoot instant>
      <ChartView data={typed(data)} recipe={{ ...recipe('BAR'), stacked: false, ...over }} units={viewUnits} title="Costs" />
    </ThemeRoot>,
  )

it('lists a trend and a year earlier as columns of their own, named after their series', async () => {
  const ui = userEvent.setup()
  drawn({ unit: 'CURRENCY', series: [line('VALUE', [1000, 2000, 3000]), line('YEAR_EARLIER', [500, 0, 700]), line('MOVING_AVERAGE', [null, null, 2000])] }, { trend: 'MOVING_AVERAGE', compareYearEarlier: true })

  await ui.click(screen.getByRole('button', { name: 'Show the data as a table' }))

  const table = screen.getByRole('table', { name: 'Costs' })
  expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Period', 'Total · HUF', 'Total · HUF · a year earlier', 'Total · HUF · moving average'])
  expect(within(table).getByRole('rowheader', { name: 'Jul 2026' }).closest('tr')).toHaveTextContent(/^Jul 2026HUF\s1,000HUF\s500–$/)
  expect(screen.queryByText(/Not enough periods|Nothing was logged/)).not.toBeInTheDocument()
})

it('says in words where a linear trend goes, in the chart\'s own units', () => {
  drawn({ unit: 'CURRENCY', series: [line('VALUE', [1000, 2000, 3000]), line('LINEAR', [1000, 2000, 3000], 1000)] }, { trend: 'LINEAR' })
  expect(screen.getByText('Total · HUF: the trend line goes from HUF 1,000 to HUF 3,000.').tagName).toBe('P')
})

it('words a trend of consumption in miles per gallon where the vehicle uses them', () => {
  drawn({ unit: 'CONSUMPTION', series: [{ ...line('VALUE', [6, 7, 8]), currency: null }, { ...line('LINEAR', [6, 7, 8], 1), currency: null }] }, { kind: 'LINE', metric: 'AVERAGE_CONSUMPTION', trend: 'LINEAR' }, { distance: 'MILES', volume: 'US_GALLONS' })
  expect(screen.getByText('Total: the trend line goes from 16.7 mpg (US) to 12.5 mpg (US).')).toBeInTheDocument()
})

it('says why a trend line or a year earlier that was asked for is not there', () => {
  drawn({ unit: 'CURRENCY', series: [line('VALUE', [1000, 2000, 3000])] }, { trend: 'LINEAR', compareYearEarlier: true })

  expect(screen.getByText('Not enough periods for a trend line yet.')).toBeInTheDocument()
  expect(screen.getByText('Nothing was logged in these periods a year earlier.')).toBeInTheDocument()
})

it('writes a cost per distance with its distance unit, a scale per currency, and a dash where nothing was spent', async () => {
  const ui = userEvent.setup()
  drawn({ unit: 'COST_PER_DISTANCE', series: [line('VALUE', [4000, null, 0]), { ...line('VALUE', [10, 12, 9]), currency: 'EUR' }] }, { kind: 'LINE', metric: 'COST_PER_DISTANCE' })

  await ui.click(screen.getByRole('button', { name: 'Show the data as a table' }))

  const table = screen.getByRole('table', { name: 'Costs' })
  expect(within(table).getByRole('rowheader', { name: 'Jul 2026' }).closest('tr')).toHaveTextContent(/^Jul 2026HUF\s4,000 \/ 100 km€10\.00 \/ 100 km$/)
  expect(within(table).getByRole('rowheader', { name: 'Sep 2026' }).closest('tr')).toHaveTextContent(/^Sep 2026–€9\.00 \/ 100 km$/)
  expect(screen.getByText(/scale of its own/)).toBeInTheDocument()
})
