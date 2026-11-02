import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { axe } from 'vitest-axe'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { server } from '../../support/server.ts'
import { fakeChart, fakeDashboardBackend, fakeLogBackend, fakeSummary, fakeVehicle, healthHandler, person, renderWithApollo, sessionHandler, stubViewport } from '../../support/mocks.tsx'
import { dateValue, findDateField, typeDate } from '../../support/dates.ts'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

function setup(vehicle = fakeVehicle(), charts = [fakeChart()], route = '/vehicles/v1') {
  stubViewport('desktop')
  const dashboard = fakeDashboardBackend(vehicle, charts)
  server.use(sessionHandler('NONE', () => null), healthHandler, ...fakeLogBackend(vehicle, []).handlers, ...dashboard.handlers)
  renderWithApollo(<App />, route)
  return { ...dashboard, ui: userEvent.setup() }
}

const choose = async (ui: ReturnType<typeof userEvent.setup>, control: string, option: string) => {
  await ui.click(screen.getByRole('combobox', { name: control }))
  await ui.click(await screen.findByRole('option', { name: option }))
}

const cardOf = async (title: string) => (await screen.findByRole('heading', { name: title })).closest('.MuiCard-root') as HTMLElement

// ---- the page --------------------------------------------------------------------------------------------

it('opens on the dashboard, with the vehicle picture as the banner', async () => {
  setup(fakeVehicle({ pictureUrl: '/media/car' }))

  expect(await screen.findByRole('tab', { name: /Dashboard/, selected: true })).toBeInTheDocument()
  const banner = (await screen.findByRole('heading', { name: 'Octavia', level: 1 })).closest('.hero') as HTMLElement
  expect((banner.querySelector('.cover-picture') as HTMLElement).style.backgroundImage).toBe('url("/media/car")')
  expect(within(banner).getByText('ABC-123')).toBeInTheDocument()
})

it('shows the key figures', async () => {
  setup(fakeVehicle({ summary: fakeSummary({ thisMonthSpend: 52000, lastMonthSpend: 40000, averageConsumption: 6.25, latestOdometer: 123456, lastFillUpDate: '2026-09-17', fillUpCount: 5, expenseCount: 2 }) }))

  await screen.findByText('Spent this month')

  expect(screen.getByText(/52,000/)).toBeInTheDocument()
  expect(screen.getByText(/Last month: .*40,000/)).toBeInTheDocument()
  expect(screen.getByText('6.25 L/100 km')).toBeInTheDocument()
  expect(screen.getByText(/123,456 km/)).toBeInTheDocument()
  expect(screen.getByText('Sep 17, 2026')).toBeInTheDocument()
  expect(screen.getByText('5 fill-ups, 2 expenses')).toBeInTheDocument()
})

it('shows what was spent in another currency next to the main one, not dropped or relabelled', async () => {
  setup(fakeVehicle({ summary: fakeSummary({ thisMonthSpend: 52000, lastMonthSpend: 40000, spending: [{ currency: 'HUF', thisMonth: 52000, lastMonth: 40000 }, { currency: 'EUR', thisMonth: 73.9, lastMonth: 0 }] }) }))

  await screen.findByText('Spent this month')

  expect(screen.getByText(/52,000.* · €73\.90/)).toBeInTheDocument()
  expect(screen.getByText(/Last month: .*40,000/)).not.toHaveTextContent('€') // nothing in euro last month
})

it('has the ready-made charts, and each loads its own data from the server', async () => {
  const { state } = setup()

  for (const title of ['Monthly costs', 'Consumption', 'Fuel price', 'Expenses by category', 'Distance per month']) await screen.findByRole('heading', { name: title })

  await waitFor(() => expect(state.chartRequests.length).toBeGreaterThanOrEqual(5))
  const metrics = state.chartRequests.map((r) => `${r.config.metric}/${r.config.grouping}/${r.config.range}`)
  expect(metrics).toEqual(expect.arrayContaining(['TOTAL_SPEND/MONTH/LAST12_MONTHS', 'AVERAGE_CONSUMPTION/MONTH/LAST12_MONTHS', 'EXPENSE_COST/CATEGORY/LAST12_MONTHS']))
  expect(state.chartRequests.find((r) => r.config.metric === 'TOTAL_SPEND')!.config).toMatchObject({ stacked: true, kind: 'BAR', from: null, to: null })
})

it('shows the numbers of a chart as a table, for screen readers and for exact values', async () => {
  const { ui } = setup()
  const monthly = within(await cardOf('Monthly costs'))

  await ui.click(await monthly.findByRole('button', { name: 'Show the data as a table' }))

  const table = monthly.getByRole('table', { name: 'Monthly costs' })
  expect(within(table).getByRole('columnheader', { name: 'Fuel · HUF' })).toBeInTheDocument()
  expect(within(table).getByRole('columnheader', { name: 'Expenses · HUF' })).toBeInTheDocument()
  expect(within(table).getByRole('rowheader', { name: 'Aug 2026' })).toBeInTheDocument()
  expect(within(table).getAllByText(/1,000/).length).toBeGreaterThan(0)
  expect(monthly.getByRole('button', { name: 'Hide the table' })).toHaveAttribute('aria-expanded', 'true')
})

it('formats chart tables in the units of the vehicle, and lists categories (uncategorised included)', async () => {
  const { ui } = setup(fakeVehicle({ units: { distance: 'MILES', volume: 'US_GALLONS' } }))

  const distance = within(await cardOf('Distance per month'))
  await ui.click(await distance.findByRole('button', { name: 'Show the data as a table' }))
  expect(within(distance.getByRole('table')).getAllByText(/mi/).length).toBeGreaterThan(0)

  const categories = within(await cardOf('Expenses by category'))
  await ui.click(await categories.findByRole('button', { name: 'Show the data as a table' }))
  const table = categories.getByRole('table')
  expect(within(table).getByRole('columnheader', { name: 'Category' })).toBeInTheDocument()
  expect(within(table).getByRole('rowheader', { name: 'Uncategorised' })).toBeInTheDocument()
  expect(within(table).getByRole('rowheader', { name: 'Service' })).toBeInTheDocument()
})

it('says when there is no data to chart', async () => {
  setup()
  server.use(
    (await import('../../support/graphql.ts')).graphql.query('ChartData', () => (async () => (await import('msw')).HttpResponse.json({ data: { vehicleChartData: { unit: 'CURRENCY', series: [] } } }))()),
  )

  expect((await screen.findAllByText('No data for this period yet.')).length).toBeGreaterThan(0)
})

// ---- saved charts ------------------------------------------------------------------------------------------

it('lists the saved charts, marks shared ones, and lets the creator edit or delete only their own', async () => {
  setup(fakeVehicle(), [
    fakeChart({ id: 'c1', title: 'Fuel by month' }),
    fakeChart({ id: 'c2', title: 'Team chart', isShared: true, canEdit: false, createdBy: person('Bob') }),
  ])

  await screen.findByRole('heading', { name: 'Fuel by month' })
  const shared = within(await cardOf('Team chart'))

  expect(shared.getByText('Shared')).toBeInTheDocument()
  expect(shared.getByText('Bob')).toBeInTheDocument()
  expect(shared.queryByRole('button', { name: /^Edit the chart/ })).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Edit the chart Fuel by month' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Delete the chart Fuel by month' })).toBeInTheDocument()
})

it('invites the user to make charts when there are none', async () => {
  setup(fakeVehicle(), [])

  expect(await screen.findByText(/Compose your own charts/)).toBeInTheDocument()
})

it('deletes a chart after a confirmation', async () => {
  const { ui, state } = setup()
  await screen.findByRole('heading', { name: 'My chart' })

  await ui.click(screen.getByRole('button', { name: 'Delete the chart My chart' }))
  expect(state.calls.DeleteChart).toBeUndefined()
  await ui.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete chart' }))

  await waitFor(() => expect(screen.queryByRole('heading', { name: 'My chart' })).not.toBeInTheDocument())
  expect(state.calls.DeleteChart).toEqual([{ id: 'c1' }])
})

// ---- the builder ----------------------------------------------------------------------------------------------

it('builds a chart from a metric, grouping, type and a preset period, with a live preview', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)

  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add chart' })
  await ui.type(within(dialog).getByLabelText('Title'), 'Volume per quarter')
  await choose(ui, 'What to measure', 'Fuel volume')
  await choose(ui, 'Group by', 'Quarter')
  await choose(ui, 'Chart type', 'Line')
  await choose(ui, 'Period', 'Half a year')
  await waitFor(() => expect(state.chartRequests.at(-1)!.config).toMatchObject({ metric: 'FUEL_VOLUME', grouping: 'QUARTER', kind: 'LINE', range: 'LAST6_MONTHS' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save chart' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect(state.calls.SaveChart![0]).toEqual({
    input: { id: null, vehicleId: 'v1', title: 'Volume per quarter', shared: false, config: { metric: 'FUEL_VOLUME', grouping: 'QUARTER', kind: 'LINE', range: 'LAST6_MONTHS', stacked: false, from: null, to: null, trend: 'NONE', runningTotal: false, compareYearEarlier: false } },
  })
  expect(await screen.findByRole('heading', { name: 'Volume per quarter' })).toBeInTheDocument()
})

it('offers one month, three months, half a year, a year, and a custom from-to period', async () => {
  const { ui } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  await screen.findByRole('dialog', { name: 'Add chart' })

  await ui.click(screen.getByRole('combobox', { name: 'Period' }))

  const options = (await screen.findAllByRole('option')).map((o) => o.textContent)
  expect(options).toEqual(expect.arrayContaining(['1 month', '3 months', 'Half a year', 'One year', 'Custom (from – to)']))
})

it('a custom period needs both dates, is sent as chosen, and is previewed once it is complete', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add chart' })
  await ui.type(within(dialog).getByLabelText('Title'), 'Spring')
  expect(within(dialog).queryByLabelText('From')).not.toBeInTheDocument()

  await choose(ui, 'Period', 'Custom (from – to)')
  expect(within(dialog).getByText('Choose the first and the last day of the period.')).toBeInTheDocument()
  expect(within(dialog).getByRole('button', { name: 'Save chart' })).toBeDisabled()
  const before = state.chartRequests.length
  await typeDate(ui, await findDateField('From', dialog), '2026-03-01')
  expect(state.chartRequests.length).toBe(before) // not previewed until both dates are there
  await typeDate(ui, await findDateField('To', dialog), '2026-05-31')
  await waitFor(() => expect(state.chartRequests.at(-1)!.config).toMatchObject({ range: 'CUSTOM', from: '2026-03-01', to: '2026-05-31' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save chart' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect((state.calls.SaveChart![0] as { input: { config: unknown } }).input.config).toMatchObject({ range: 'CUSTOM', from: '2026-03-01', to: '2026-05-31' })
})

it('does not send dates for a preset period, even after a custom one was chosen first', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add chart' })
  await ui.type(within(dialog).getByLabelText('Title'), 'Back to preset')
  await choose(ui, 'Period', 'Custom (from – to)')
  await typeDate(ui, await findDateField('From', dialog), '2026-03-01')
  await typeDate(ui, await findDateField('To', dialog), '2026-05-31')

  await choose(ui, 'Period', '3 months')
  await ui.click(within(dialog).getByRole('button', { name: 'Save chart' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect((state.calls.SaveChart![0] as { input: { config: unknown } }).input.config).toMatchObject({ range: 'LAST3_MONTHS', from: null, to: null })
})

it('offers a trend line, a running total and a year earlier for a chart over time, never a trend with a running total, and sends them', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add chart' })
  await ui.type(within(dialog).getByLabelText('Title'), 'Where it goes')
  const runningTotal = () => within(dialog).queryByRole('switch', { name: 'Running total' })

  await choose(ui, 'Trend line', 'Linear trend')
  await ui.click(runningTotal()!)
  expect(screen.getByRole('combobox', { name: 'Trend line' })).toHaveTextContent('None') // a running total has no trend line
  await choose(ui, 'Trend line', 'Moving average (3 periods)')
  expect(runningTotal()).not.toBeChecked() // and the other way round
  await ui.click(within(dialog).getByRole('switch', { name: 'Compare with a year earlier' }))

  await choose(ui, 'What to measure', 'Average consumption')
  expect(runningTotal()).not.toBeInTheDocument() // an average does not add up
  await choose(ui, 'What to measure', 'Expense cost')
  await choose(ui, 'Group by', 'Expense category')
  expect(screen.queryByRole('combobox', { name: 'Trend line' })).not.toBeInTheDocument() // not over time
  expect(within(dialog).queryByRole('switch', { name: 'Compare with a year earlier' })).not.toBeInTheDocument()

  await choose(ui, 'Group by', 'Month')
  await choose(ui, 'Trend line', 'Moving average (3 periods)')
  await ui.click(within(dialog).getByRole('switch', { name: 'Compare with a year earlier' }))
  await waitFor(() => expect(state.chartRequests.at(-1)!.config).toMatchObject({ metric: 'EXPENSE_COST', trend: 'MOVING_AVERAGE', compareYearEarlier: true }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save chart' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect((state.calls.SaveChart![0] as { input: { config: unknown } }).input.config).toMatchObject({ metric: 'EXPENSE_COST', grouping: 'MONTH', trend: 'MOVING_AVERAGE', runningTotal: false, compareYearEarlier: true })
})

it('offers the cost per distance among what to measure', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add chart' })

  await choose(ui, 'What to measure', 'Fuel cost per distance')
  await waitFor(() => expect(state.chartRequests.at(-1)!.config).toMatchObject({ metric: 'FUEL_COST_PER_DISTANCE' }))
  await ui.click(await within(dialog).findByRole('button', { name: 'Show the data as a table' }))

  expect(within(dialog).getByRole('rowheader', { name: 'Aug 2026' }).closest('tr')).toHaveTextContent('HUF 1,000 / 100 km') // the preview's table in its unit
})

it('only offers combinations that make sense', async () => {
  const { ui } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add chart' })
  const optionsOf = async (control: string) => {
    await ui.click(screen.getByRole('combobox', { name: control }))
    const names = (await screen.findAllByRole('option')).map((o) => o.textContent)
    await ui.keyboard('{Escape}')
    return names
  }

  expect(await optionsOf('Group by')).not.toContain('Expense category') // only for expense costs
  expect(within(dialog).getByRole('switch', { name: 'Split into fuel and expenses' })).toBeInTheDocument() // total spend (the default)

  await choose(ui, 'What to measure', 'Average consumption')
  expect(await optionsOf('Chart type')).not.toContain('Donut') // an average cannot be cut into parts
  expect(within(dialog).queryByRole('switch', { name: 'Split into fuel and expenses' })).not.toBeInTheDocument()

  await choose(ui, 'What to measure', 'Expense cost')
  expect(await optionsOf('Group by')).toContain('Expense category')
  await choose(ui, 'Group by', 'Expense category')
  await choose(ui, 'Chart type', 'Donut')

  await choose(ui, 'What to measure', 'Fuel cost') // the category grouping and the donut stay valid or are reset
  expect(screen.getByRole('combobox', { name: 'Group by' })).toHaveTextContent('Month')
})

it('splits total spend into fuel and expenses when asked', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add chart' })
  await ui.type(within(dialog).getByLabelText('Title'), 'Split')

  await ui.click(within(dialog).getByRole('switch', { name: 'Split into fuel and expenses' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save chart' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect((state.calls.SaveChart![0] as { input: { config: unknown } }).input.config).toMatchObject({ metric: 'TOTAL_SPEND', stacked: true })
})

it('shares a chart with everyone who sees the vehicle, when the user may', async () => {
  const { ui, state } = setup(fakeVehicle({ logAccess: 'EDIT' }), [])
  await screen.findByText(/Compose your own charts/)
  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add chart' })
  await ui.type(within(dialog).getByLabelText('Title'), 'For all')

  await ui.click(within(dialog).getByRole('switch', { name: 'Share with everyone who can see this vehicle' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save chart' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect(state.calls.SaveChart![0]).toMatchObject({ input: { shared: true } })
  expect(await screen.findByText('Shared')).toBeInTheDocument()
})

it('viewers can make their own charts but are not offered sharing', async () => {
  const { ui } = setup(fakeVehicle({ canEdit: false, logAccess: 'VIEW' }), [])
  await screen.findByText(/Compose your own charts/)

  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add chart' })

  expect(within(dialog).queryByRole('switch', { name: /Share with everyone/ })).not.toBeInTheDocument()
})

it('edits a saved chart, starting from its recipe', async () => {
  const { ui, state } = setup(fakeVehicle(), [
    fakeChart({ id: 'c1', title: 'Old', metric: 'FUEL_VOLUME', grouping: 'YEAR', kind: 'AREA', range: 'CUSTOM', rangeFrom: '2025-01-01', rangeTo: '2025-12-31', trend: 'LINEAR', compareYearEarlier: true }),
  ])
  await screen.findByRole('heading', { name: 'Old' })

  await ui.click(screen.getByRole('button', { name: 'Edit the chart Old' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit chart' })
  expect(within(dialog).getByLabelText('Title')).toHaveValue('Old')
  expect(screen.getByRole('combobox', { name: 'What to measure' })).toHaveTextContent('Fuel volume')
  expect(screen.getByRole('combobox', { name: 'Group by' })).toHaveTextContent('Year')
  expect(screen.getByRole('combobox', { name: 'Chart type' })).toHaveTextContent('Area')
  expect(screen.getByRole('combobox', { name: 'Trend line' })).toHaveTextContent('Linear trend')
  expect(within(dialog).getByRole('switch', { name: 'Compare with a year earlier' })).toBeChecked()
  expect(dateValue(await findDateField('From', dialog))).toBe('2025-01-01')
  await ui.clear(within(dialog).getByLabelText('Title'))
  await ui.type(within(dialog).getByLabelText('Title'), 'New name')
  await ui.click(within(dialog).getByRole('button', { name: 'Save chart' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect(state.calls.SaveChart![0]).toMatchObject({ input: { id: 'c1', title: 'New name', config: { range: 'CUSTOM', from: '2025-01-01', to: '2025-12-31', trend: 'LINEAR', compareYearEarlier: true } } })
  expect(await screen.findByRole('heading', { name: 'New name' })).toBeInTheDocument()
})

it('shows the rule from the server when saving fails, and keeps the dialog open', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  state.failWith = { message: 'limit', key: 'chart.limit', args: { max: 30 } }
  await screen.findByText(/Compose your own charts/)
  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add chart' })
  await ui.type(within(dialog).getByLabelText('Title'), 'One too many')

  await ui.click(within(dialog).getByRole('button', { name: 'Save chart' }))

  expect(await within(dialog).findByRole('alert')).toHaveTextContent('You can have at most 30 charts for a vehicle.')
  expect(screen.getByRole('dialog')).toBeInTheDocument()
})

it('blocks a chart without a title', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add chart' })

  await ui.click(within(dialog).getByRole('button', { name: 'Save chart' }))

  expect(await within(dialog).findByText('Title is required')).toBeInTheDocument()
  expect(state.calls.SaveChart).toBeUndefined()
})

// ---- accessibility ----------------------------------------------------------------------------------------------

it('has no accessibility violations: the dashboard, an opened table and the builder', async () => {
  const { ui } = setup(fakeVehicle(), [fakeChart(), fakeChart({ id: 'c2', title: 'Shared one', isShared: true, canEdit: false, createdBy: person('Bob') })])
  const check = async (node: HTMLElement) => {
    const results = await axe(node, { rules: { 'color-contrast': { enabled: false } } })
    expect(results.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`)).toEqual([])
  }
  await screen.findByRole('heading', { name: 'Shared one' })
  await check(document.body)

  await ui.click(within(await cardOf('Monthly costs')).getByRole('button', { name: 'Show the data as a table' }))
  await check(document.body)

  await ui.click(screen.getByRole('button', { name: 'Add chart' }))
  await screen.findByRole('dialog', { name: 'Add chart' })
  await check(document.body)
})
