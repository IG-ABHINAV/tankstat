import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { delay } from 'msw'
import { axe } from 'vitest-axe'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { graphql } from '../../support/graphql.ts'
import { server } from '../../support/server.ts'
import { fakeChart, fakeDashboardBackend, fakeLogBackend, fakeSummary, fakeTile, fakeVehicle, healthHandler, person, renderWithApollo, sessionHandler, stubViewport, user, type FakeLayout } from '../../support/mocks.tsx'
import { dateValue, findDateField, typeDate } from '../../support/dates.ts'

/** A key figure's value read whole: its unit is written apart in small type (#259), so the words span two elements of one paragraph. */
const whole = (text: string) => (_: string, element: Element | null) => element?.tagName === 'P' && element.textContent?.replace(/\s+/g, ' ').trim() === text

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

function setup(vehicle = fakeVehicle(), charts = [fakeChart()], route = '/vehicles/v1', { signedIn = false, arranged }: { signedIn?: boolean; arranged?: (state: ReturnType<typeof fakeDashboardBackend>['state']) => void } = {}) {
  stubViewport('desktop')
  const dashboard = fakeDashboardBackend(vehicle, charts)
  arranged?.(dashboard.state)
  server.use(sessionHandler(signedIn ? 'STANDALONE' : 'NONE', () => (signedIn ? user() : null)), healthHandler, ...fakeLogBackend(vehicle, []).handlers, ...dashboard.handlers)
  renderWithApollo(<App />, route)
  return { ...dashboard, ui: userEvent.setup() }
}

/** The charts' titles in the order the dashboard's tiles come (reading order: the grid draws them in that order). */
const chartTitles = async () => within(await screen.findByRole('region', { name: 'Dashboard' })).getAllByRole('heading', { level: 2 }).map((h) => h.textContent)
const figureLabels = () => [...within(screen.getByRole('region', { name: 'Dashboard' })).getAllByText(/./, { selector: '.MuiCard-root > div:first-child > p:first-child' })].map((p) => p.textContent)
const layout = (over: Partial<FakeLayout>): FakeLayout => ({ tiles: [], hidden: [], ...over })
/** Opens a tile's menu and picks an entry. */
const tileMenu = async (ui: ReturnType<typeof userEvent.setup>, name: string, entry: string) => {
  await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument()) // the last menu is still leaving
  await ui.click(screen.getByRole('button', { name: `Options for ${name}` }))
  const item = await screen.findByRole('menuitem', { name: entry })
  await waitFor(() => expect(getComputedStyle(item).pointerEvents, `${entry} for ${name} takes no pointer (disabled?)`).not.toBe('none')) // the menu is still coming in
  await ui.click(item)
}
const said = () => screen.getByRole('status', { name: 'Last change to the arrangement' })
const at = (id: string, x: number, y: number, w: number, h: number) => expect.objectContaining({ id, x, y, w, h })

const choose = async (ui: ReturnType<typeof userEvent.setup>, control: string, option: string) => {
  await ui.click(screen.getByRole('combobox', { name: control }))
  await ui.click(await screen.findByRole('option', { name: option }))
}
/** A toggle of the builder's rows (grouping, chart type, period, trend line, how the second measure is drawn): a radio in its group. */
const radio = (group: string, option: string) => within(screen.getByRole('radiogroup', { name: group })).getByRole('radio', { name: option })
const pick = async (ui: ReturnType<typeof userEvent.setup>, group: string, option: string) => ui.click(radio(group, option))
/** One of the builder's extras (a running total, a year earlier, the split): a checkbox in the Also group. */
const extra = (name: string) => within(screen.getByRole('group', { name: 'Also' })).getByRole('checkbox', { name })

/** A chart's tile by its heading (the card's, or the strip's while customizing, #262, where its buttons are too); a card outside a tile. */
const cardOf = async (title: string) => {
  const heading = await screen.findByRole('heading', { name: title })
  return (heading.closest('[data-tile]') ?? heading.closest('.MuiCard-root')) as HTMLElement
}
/** Enters Customize (where a chart is added, edited or deleted). */
const customize = async (ui: ReturnType<typeof userEvent.setup>) => {
  await ui.click(await screen.findByRole('button', { name: 'Customize' }))
  await screen.findByRole('button', { name: 'Save dashboard' })
}
/** Opens the Add widget dialog from Customize and picks a starting point; gives the dialog. */
const addWidget = async (ui: ReturnType<typeof userEvent.setup>, pick = 'Blank chart') => {
  await customize(ui)
  await ui.click(screen.getByRole('button', { name: 'Add widget' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add widget' })
  await ui.click(within(dialog).getByRole('button', { name: pick }))
  return dialog
}

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
  expect(screen.getByText(whole('6.25 L/100 km'))).toBeInTheDocument()
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

/** Opens a chart's table from the button in its card's header (a dialog named after the chart) and gives the table. */
const tableOf = async (ui: ReturnType<typeof userEvent.setup>, title: string) => {
  const card = within(await cardOf(title))
  await ui.click(await card.findByRole('button', { name: `${title} as a table` }))
  const dialog = await screen.findByRole('dialog', { name: title })
  return within(dialog).getByRole('table', { name: title })
}

it('shows the numbers of a chart as a table in a dialog, for screen readers and for exact values', async () => {
  const { ui } = setup()

  const table = await tableOf(ui, 'Monthly costs')

  expect(within(table).getByRole('columnheader', { name: 'Fuel · HUF' })).toBeInTheDocument()
  expect(within(table).getByRole('columnheader', { name: 'Expenses · HUF' })).toBeInTheDocument()
  expect(within(table).getByRole('rowheader', { name: 'Aug 2026' })).toBeInTheDocument()
  expect(within(table).getAllByText(/1,000/).length).toBeGreaterThan(0)
  await ui.click(screen.getByRole('button', { name: 'Close' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(screen.queryByRole('table')).not.toBeInTheDocument() // nothing under the picture: the tile holds only the chart
})

it('formats chart tables in the units of the vehicle, and lists categories (uncategorised included)', async () => {
  const { ui } = setup(fakeVehicle({ units: { distance: 'MILES', volume: 'US_GALLONS' } }))

  const distance = await tableOf(ui, 'Distance per month')
  expect(within(distance).getAllByText(/mi/).length).toBeGreaterThan(0)
  await ui.keyboard('{Escape}')
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

  const table = await tableOf(ui, 'Expenses by category')
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

it('lists the saved charts, marks shared ones, and offers Edit and Delete each by its own right, only while customizing', async () => {
  const { ui } = setup(fakeVehicle(), [
    fakeChart({ id: 'c1', title: 'Fuel by month' }),
    fakeChart({ id: 'c2', title: 'Team chart', isShared: true, canEdit: false, canDelete: false, createdBy: person('Bob') }),
    // Someone else's shared chart seen with Delete access to the logs (#210): it can be removed, never changed.
    fakeChart({ id: 'c3', title: 'Removable chart', isShared: true, canEdit: false, canDelete: true, createdBy: person('Bob') }),
  ])

  await screen.findByRole('heading', { name: 'Fuel by month' })
  const shared = within(await cardOf('Team chart'))
  const removable = within(await cardOf('Removable chart'))

  expect(shared.getByText('Shared')).toBeInTheDocument()
  expect(shared.getByText('Bob')).toBeInTheDocument()
  // Outside Customize a tile shows only its content: the table button, nothing that changes the chart.
  expect(screen.queryByRole('button', { name: /^Edit the chart|^Delete the chart|^Copy / })).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Fuel by month as a table' })).toBeInTheDocument()

  await customize(ui)
  expect(shared.queryByRole('button', { name: /^Edit the chart|^Delete the chart/ })).not.toBeInTheDocument()
  expect(removable.queryByRole('button', { name: /^Edit the chart/ })).not.toBeInTheDocument()
  expect(removable.getByRole('button', { name: 'Delete the chart Removable chart' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Edit the chart Fuel by month' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Delete the chart Fuel by month' })).toBeInTheDocument()
})

it('invites the user to make charts when there are none', async () => {
  setup(fakeVehicle(), [])

  expect(await screen.findByText(/Compose your own charts/)).toBeInTheDocument()
})

it('deletes a chart after a confirmation, while customizing', async () => {
  const { ui, state } = setup()
  await screen.findByRole('heading', { name: 'My chart' })
  await customize(ui)

  await ui.click(screen.getByRole('button', { name: 'Delete the chart My chart' }))
  expect(state.calls.DeleteChart).toBeUndefined()
  await ui.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete chart' }))

  await waitFor(() => expect(screen.queryByRole('heading', { name: 'My chart' })).not.toBeInTheDocument())
  expect(state.calls.DeleteChart).toEqual([{ id: 'c1' }])
})

it('makes a chart tile\'s strip its header while customizing: its one heading and its buttons there, the edit dialog\'s preview its own (#262)', async () => {
  const { ui } = setup()
  const heading = await screen.findByRole('heading', { level: 2, name: 'My chart' })
  const tile = heading.closest('[data-tile]') as HTMLElement
  expect(heading.closest('.MuiCard-root')).not.toBeNull() // in view mode the card's own header
  await within(tile).findByRole('button', { name: 'My chart as a table' })

  await customize(ui)
  const strip = await waitFor(() => {
    const titles = within(tile).getAllByRole('heading', { level: 2, name: 'My chart' })
    expect(titles).toHaveLength(1) // the strip's, the card's own title not drawn meanwhile
    expect(titles[0].closest('.MuiCard-root')).toBeNull()
    return titles[0].closest('.tk-tile-handle')!.parentElement as HTMLElement
  })
  // The table, edit and delete buttons in the strip, in its order, before the tile's menu; the card keeps the byline only.
  await within(strip).findByRole('button', { name: 'My chart as a table' })
  expect(within(strip).getAllByRole('button').map((b) => b.getAttribute('aria-label') ?? b.textContent)).toEqual([
    'My chart as a table',
    'Edit the chart My chart',
    'Delete the chart My chart',
    'Options for My chart',
  ])
  expect(within(tile.querySelector<HTMLElement>('.MuiCard-root')!).queryAllByRole('button', { name: /My chart/ })).toEqual([])

  // The edit dialog, opened from the strip, draws its preview as a card of its own, with its own heading.
  await ui.click(within(strip).getByRole('button', { name: 'Edit the chart My chart' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit chart' })
  expect(within(dialog).getByRole('heading', { level: 2, name: 'My chart' }).closest('.MuiCard-root')).not.toBeNull()
  expect(within(tile).getAllByRole('heading', { level: 2, name: 'My chart', hidden: true })).toHaveLength(1)
})

/** A chart card's loading outline, once it is there (`waitFor` retries only on a throw, not on a null). */
const outline = (root: HTMLElement) =>
  waitFor(() => {
    const skeleton = root.querySelector<HTMLElement>('.MuiSkeleton-root')
    expect(skeleton).not.toBeNull()
    return skeleton!
  })

it('gives a tile\'s chart the room its tile has, never a height of its own, and the builder\'s preview its 240 px (#227)', async () => {
  const { ui } = setup()
  // The charts' data never comes, so their loading outlines stay: jsdom lays nothing out, and this is the size the picture is given.
  server.use(graphql.query('ChartData', () => delay('infinite')))

  const tile = (await screen.findByRole('heading', { name: 'Monthly costs' })).closest('[data-tile]') as HTMLElement
  expect((await outline(tile)).style.height).toBe('') // sized by the browser: what the card has under its title

  // The builder's preview, reached through Add widget's blank chart (#228).
  const dialog = await addWidget(ui)
  expect((await outline(dialog)).style.height).toBe('240px')
})

// ---- the arrangement ----------------------------------------------------------------------------------------

it('shows the Standard preset until someone arranges the dashboard: the four figures, the five ready-made charts, then the saved ones', async () => {
  setup(fakeVehicle(), [fakeChart()])

  await screen.findByRole('heading', { name: 'My chart' })
  expect(await chartTitles()).toEqual(['Monthly costs', 'Consumption', 'Fuel price', 'Expenses by category', 'Distance per month', 'My chart'])
  expect(figureLabels()).toEqual(['Spent this month', 'Average consumption', 'Odometer', 'Last fill-up'])
})

it('follows the vehicle\'s arrangement: its tiles in reading order, a saved chart it does not mention in the first free spot, one taken off left out', async () => {
  setup(fakeVehicle(), [fakeChart(), fakeChart({ id: 'c2', title: 'Second', isShared: true }), fakeChart({ id: 'c3', title: 'Taken off', isShared: true })], undefined, {
    arranged: (s) => (s.dashboard.shared = layout({ tiles: [fakeTile('odometer', 3, 0, 1, 3), fakeTile('chart:c2', 0, 0), fakeTile('preset:distance', 2, 3)], hidden: ['chart:c3'] })),
  })

  await screen.findByRole('heading', { name: 'Second' })
  expect(await chartTitles()).toEqual(['Second', 'Distance per month', 'My chart'])
  expect(figureLabels()).toEqual(['Odometer'])
  expect(screen.queryByText(/your own arrangement/)).not.toBeInTheDocument()
})

it('shows a person\'s own arrangement over the vehicle\'s, says so, and goes back to the vehicle\'s', async () => {
  const { ui, state } = setup(fakeVehicle(), [], undefined, {
    signedIn: true,
    arranged: (s) => {
      s.dashboard.shared = layout({ tiles: [fakeTile('odometer', 0, 0, 1, 3), fakeTile('preset:distance', 0, 3)] })
      s.dashboard.personal = layout({ tiles: [fakeTile('lastFillUp', 0, 0, 1, 3), fakeTile('preset:fuelPrice', 0, 3)] })
    },
  })

  expect(await screen.findByText('You see your own arrangement of this dashboard.')).toBeInTheDocument()
  expect(await chartTitles()).toEqual(['Fuel price'])
  await ui.click(screen.getByRole('button', { name: "Use the vehicle's dashboard" }))

  await waitFor(() => expect(state.calls.ResetDashboardLayout).toEqual([{ vehicleId: 'v1', personal: true }]))
  await waitFor(async () => expect(await chartTitles()).toEqual(['Distance per month']))
  expect(screen.queryByText('You see your own arrangement of this dashboard.')).not.toBeInTheDocument()
})

it('customizes the vehicle\'s dashboard in place: a figure moved earlier, a ready-made chart and a saved one taken off by their menus, saved for everyone', async () => {
  const { ui, state } = setup(fakeVehicle(), [fakeChart({ isShared: true })], undefined, { signedIn: true })
  await screen.findByRole('heading', { name: 'My chart' })

  await ui.click(screen.getByRole('button', { name: 'Customize' }))
  expect(screen.getByRole('radio', { name: 'Everyone who sees this vehicle' })).toBeChecked()
  await tileMenu(ui, 'Odometer', 'Move earlier')
  expect(said()).toHaveTextContent('Odometer moved to position 2 of 10.')
  await tileMenu(ui, 'Consumption', 'Remove')
  await tileMenu(ui, 'My chart', 'Remove')
  await ui.click(screen.getByRole('button', { name: 'Add widget' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add widget' })
  const offered = within(within(dialog).getByRole('list', { name: 'What to add' })).getAllByRole('button').map((b) => b.textContent)
  expect(offered).toEqual(expect.arrayContaining(['Consumption', 'My chart', 'Blank chart', 'Monthly costsOn the dashboard']))
  // The trend figures, every ready-made chart (#247: the four still on it too, marked; Consumption, taken off, not), the saved one, a blank chart.
  expect(offered).toHaveLength(4 + 10 + 1 + 1)
  expect(offered.filter((o) => o?.endsWith('On the dashboard'))).toHaveLength(4)
  await ui.keyboard('{Escape}')
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  await ui.click(screen.getByRole('button', { name: 'Save dashboard' }))

  await waitFor(() => expect(state.calls.SaveDashboardLayout).toHaveLength(1))
  expect(state.calls.SaveDashboardLayout![0]).toEqual({
    input: {
      vehicleId: 'v1',
      personal: false,
      tiles: [
        at('thisMonth', 0, 0, 1, 4),
        at('odometer', 1, 0, 1, 4),
        at('consumption', 2, 0, 1, 4),
        at('lastFillUp', 3, 0, 1, 4),
        at('preset:monthlyCosts', 0, 4, 2, 8),
        at('preset:categories', 2, 4, 2, 8), // packed up into the removed chart's place
        at('preset:fuelPrice', 0, 12, 2, 8),
        at('preset:distance', 0, 20, 2, 8),
      ],
      hidden: ['chart:c1'],
    },
  })
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Save dashboard' })).not.toBeInTheDocument())
  expect(await chartTitles()).toEqual(['Monthly costs', 'Expenses by category', 'Fuel price', 'Distance per month'])
  expect(figureLabels()).toEqual(['Spent this month', 'Odometer', 'Average consumption', 'Last fill-up'])
})

it('resizes and pins a tile by its menu, within what it needs', async () => {
  const { ui, state } = setup(fakeVehicle(), [], undefined, { signedIn: true })
  await screen.findByRole('heading', { name: 'Monthly costs' })
  await ui.click(screen.getByRole('button', { name: 'Customize' }))

  await tileMenu(ui, 'Monthly costs', 'Wider')
  expect(said()).toHaveTextContent('Monthly costs is now 3 wide and 8 tall.')
  await tileMenu(ui, 'Monthly costs', 'Taller')
  await tileMenu(ui, 'Odometer', 'Pin')
  expect(said()).toHaveTextContent('Odometer pinned.')
  await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument())
  await ui.click(screen.getByRole('button', { name: 'Options for Odometer' }))
  expect(screen.getByRole('menuitem', { name: 'Narrower' })).toHaveAttribute('aria-disabled', 'true') // a figure is one column at least
  expect(screen.getByRole('menuitem', { name: 'Unpin' })).toBeInTheDocument()
  await ui.keyboard('{Escape}')
  await ui.click(screen.getByRole('button', { name: 'Save dashboard' }))

  await waitFor(() => expect(state.calls.SaveDashboardLayout).toHaveLength(1))
  const tiles = (state.calls.SaveDashboardLayout![0] as { input: { tiles: { id: string; w: number; h: number; pinned: boolean }[] } }).input.tiles
  expect(tiles.find((t) => t.id === 'preset:monthlyCosts')).toMatchObject({ w: 3, h: 9 })
  expect(tiles.find((t) => t.id === 'odometer')).toMatchObject({ pinned: true })
})

it('shows what a figure\'s rows hold: label and value at two, the comparison or the note at three, the sparkline at four (#229)', async () => {
  setup(fakeVehicle({ summary: fakeSummary({ trends: { spending: [{ currency: 'HUF', recent: 560000, earlier: 500000 }, { currency: 'EUR', recent: 200, earlier: 150 }], costPerDistance: [], consumption: null, distance: { recent: 12500, earlier: 11000 } } }) }), [], undefined, {
    signedIn: true,
    arranged: (s) => {
      s.dashboard.shared = layout({
        tiles: [fakeTile('odometer', 0, 0, 1, 2), fakeTile('consumption', 1, 0, 1, 2), fakeTile('thisMonth', 2, 0, 1, 3), fakeTile('lastFillUp', 3, 0, 1, 3), fakeTile('distanceTrend', 0, 3, 1, 3), fakeTile('spendingTrend', 1, 3, 1, 4)],
      })
    },
  })

  const region = await screen.findByRole('region', { name: 'Dashboard' })
  expect(await within(region).findByText('12,000 km')).toBeInTheDocument() // two rows: the value
  expect(within(region).getByText(whole('6.5 L/100 km'))).toBeInTheDocument()
  expect(within(region).queryByText('last 10 full fill-ups')).not.toBeInTheDocument() // but not the note
  expect(within(region).getByText('5 fill-ups, 2 expenses')).toBeInTheDocument() // three rows: the note
  expect(within(region).getByText(/Last month: .*40,000/)).toBeInTheDocument()
  expect(await within(region).findByText('1,500 km (14%) more than last year to this day')).toBeInTheDocument() // three rows: the comparison (each trend tile loads its own)
  expect(await within(region).findByText('HUF 60,000 (12%) more than the 12 months before')).toBeInTheDocument() // four rows: the comparison and the note
  expect(within(region).getByText('€200.00')).toBeInTheDocument()
  expect(within(region).queryByRole('img', { name: /^Spending in the last six months/ })).not.toBeInTheDocument() // the sparkline needs four rows
  expect(figureLabels()).toEqual(['Odometer', 'Average consumption', 'Spent this month', 'Last fill-up', 'Distance driven', 'Spending'])
})

it('makes a figure shorter down to two rows, its label and value, and saves the rows (#229, #259)', async () => {
  const { ui, state } = setup(fakeVehicle(), [], undefined, { signedIn: true })
  await screen.findByRole('heading', { name: 'Monthly costs' })
  await ui.click(screen.getByRole('button', { name: 'Customize' }))

  await tileMenu(ui, 'Odometer', 'Shorter')
  expect(said()).toHaveTextContent('Odometer is now 1 wide and 3 tall.')
  await tileMenu(ui, 'Odometer', 'Shorter')
  expect(said()).toHaveTextContent('Odometer is now 1 wide and 2 tall.')
  await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument())
  await ui.click(screen.getByRole('button', { name: 'Options for Odometer' }))
  expect(screen.getByRole('menuitem', { name: 'Shorter' })).toHaveAttribute('aria-disabled', 'true') // two rows hold its label and value, no fewer
  await ui.keyboard('{Escape}')
  await tileMenu(ui, 'Spent this month', 'Shorter')
  expect(said()).toHaveTextContent('Spent this month is now 1 wide and 3 tall.')
  await tileMenu(ui, 'Spent this month', 'Shorter')
  expect(said()).toHaveTextContent('Spent this month is now 1 wide and 2 tall.') // its card fits several currencies to two rows (#259)
  await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument())
  await ui.click(screen.getByRole('button', { name: 'Options for Spent this month' }))
  expect(screen.getByRole('menuitem', { name: 'Shorter' })).toHaveAttribute('aria-disabled', 'true')
  await ui.keyboard('{Escape}')
  await ui.click(screen.getByRole('button', { name: 'Save dashboard' }))

  await waitFor(() => expect(state.calls.SaveDashboardLayout).toHaveLength(1))
  const tiles = (state.calls.SaveDashboardLayout![0] as { input: { tiles: { id: string; h: number }[] } }).input.tiles
  expect(tiles.find((t) => t.id === 'odometer')).toMatchObject({ h: 2 })
  expect(tiles.find((t) => t.id === 'thisMonth')).toMatchObject({ h: 2 })
  expect(tiles.find((t) => t.id === 'consumption')).toMatchObject({ h: 4 })
})

it('adds back what was taken off, starts afresh from a preset, and arranges one\'s own from what one sees', async () => {
  const { ui, state } = setup(fakeVehicle(), [fakeChart()], undefined, {
    signedIn: true,
    arranged: (s) => (s.dashboard.shared = layout({ tiles: [fakeTile('odometer', 0, 0, 1, 3), fakeTile('preset:distance', 0, 3)], hidden: ['chart:c1'] })),
  })
  await screen.findByRole('heading', { name: 'Distance per month' })
  await ui.click(screen.getByRole('button', { name: 'Customize' }))

  await ui.click(screen.getByRole('radio', { name: 'Just me' }))
  for (const name of ['My chart', 'Last fill-up']) {
    await ui.click(screen.getByRole('button', { name: 'Add widget' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add widget' })
    await ui.click(within(dialog).getByRole('button', { name }))
    await ui.click(within(dialog).getByRole('button', { name: 'Add to the dashboard' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(said()).toHaveTextContent(`${name} added.`)
  }
  await ui.click(screen.getByRole('button', { name: 'Save dashboard' }))
  await waitFor(() => expect(state.calls.SaveDashboardLayout).toHaveLength(1))
  expect(state.calls.SaveDashboardLayout![0]).toEqual({
    input: { vehicleId: 'v1', personal: true, tiles: [at('odometer', 0, 0, 1, 3), at('preset:distance', 0, 3, 2, 8), at('chart:c1', 0, 11, 2, 8), at('lastFillUp', 0, 19, 1, 3)], hidden: [] }, // the figure stored three rows high keeps them, the one added gets the three it shows in full
  })

  await waitFor(() => expect(screen.queryByRole('button', { name: 'Save dashboard' })).not.toBeInTheDocument())
  await ui.click(screen.getByRole('button', { name: 'Customize' }))
  expect(screen.getByRole('radio', { name: 'Just me' })).toBeChecked() // one's own, now that there is one
  await choose(ui, 'Start from a preset', 'Standard')
  expect(said()).toHaveTextContent('Started from Standard.')
  expect(screen.getAllByRole('button', { name: /^Options for / })).toHaveLength(4 + 5 + 1) // every figure, the five ready-made charts and the saved one
})

it('offers a viewer only an arrangement of their own, and nobody one of their own with sign-in off', async () => {
  const viewer = setup(fakeVehicle({ canEdit: false, logAccess: 'VIEW' }), [], undefined, { signedIn: true, arranged: (s) => (s.dashboard.canChangeShared = false) })
  await viewer.ui.click(await screen.findByRole('button', { name: 'Customize' }))
  expect(screen.queryByRole('radiogroup')).not.toBeInTheDocument()
  await viewer.ui.click(screen.getByRole('button', { name: 'Save dashboard' }))
  await waitFor(() => expect(viewer.state.calls.SaveDashboardLayout).toHaveLength(1))
  expect((viewer.state.calls.SaveDashboardLayout![0] as { input: { personal: boolean } }).input.personal).toBe(true)
})

it('with sign-in off, everyone is the same visitor: the vehicle\'s dashboard only', async () => {
  const { ui } = setup(fakeVehicle(), [])
  await ui.click(await screen.findByRole('button', { name: 'Customize' }))

  expect(screen.queryByRole('radio', { name: 'Just me' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Back to Standard' })).not.toBeInTheDocument() // nothing arranged yet
  await ui.click(screen.getByRole('button', { name: 'Cancel' }))
  expect(screen.getByRole('button', { name: 'Customize' })).toBeInTheDocument()
})

// ---- the trend figures, the presets, a copied chart ------------------------------------------------------------

it('words the trend figures with an arrow and a change, never by colour alone', async () => {
  setup(fakeVehicle(), [], undefined, {
    signedIn: true,
    arranged: (s) => (s.dashboard.shared = layout({ tiles: [fakeTile('spendingTrend', 0, 0, 1, 3), fakeTile('costPerDistance', 1, 0, 1, 4), fakeTile('consumptionTrend', 2, 0, 1, 4), fakeTile('distanceTrend', 3, 0, 1, 3)] })),
  })

  const region = await screen.findByRole('region', { name: 'Dashboard' })
  expect(await within(region).findByText('HUF 560,000')).toBeInTheDocument()
  expect(within(region).getByText('HUF 60,000 (12%) more than the 12 months before')).toBeInTheDocument()
  expect(within(region).getByText(whole('HUF 4,480 / 100 km'))).toBeInTheDocument()
  expect(within(region).getByText('HUF 480 / 100 km (12%) more than the 12 months before')).toBeInTheDocument()
  expect(within(region).getByText(whole('6.5 L/100 km'))).toBeInTheDocument()
  expect(within(region).getByText('last 10 fill-ups')).toBeInTheDocument()
  expect(within(region).getByText('uses 7% less fuel than the 10 fill-ups before')).toBeInTheDocument()
  expect(within(region).getByText('12,500 km')).toBeInTheDocument()
  expect(within(region).getByText('1,500 km (14%) more than last year to this day')).toBeInTheDocument()
  // The arrow's colour follows the meaning (#228): spending and fuel up are bad, down good; distance is neutral either way.
  const toneOf = (text: string) => within(region).getByText(text).closest('p')!.querySelector('[data-tone]')!.getAttribute('data-tone')
  expect(toneOf('HUF 60,000 (12%) more than the 12 months before')).toBe('bad')
  expect(toneOf('uses 7% less fuel than the 10 fill-ups before')).toBe('good')
  expect(toneOf('1,500 km (14%) more than last year to this day')).toBe('neutral')
})

it('shows a trend figure over its tile\'s own period: the chip names it, the words follow it, the server is asked for it (#230)', async () => {
  const weeks = { kind: 'LAST', unit: 'WEEKS', count: 8, since: null, whole: false, against: 'YEAR_EARLIER' }
  const since = { kind: 'SINCE', unit: null, count: null, since: '2026-03-15', whole: false, against: 'BEFORE' }
  const { state } = setup(fakeVehicle(), [], undefined, {
    signedIn: true,
    arranged: (s) => {
      s.dashboard.shared = layout({ tiles: [{ ...fakeTile('spendingTrend', 0, 0, 1, 4), period: weeks }, { ...fakeTile('consumptionTrend', 1, 0, 1, 4), period: since }, fakeTile('distanceTrend', 2, 0, 1, 4)] })
      s.trendFor = (figure, period) =>
        figure === 'SPENDING' ? { recent: { start: '2026-09-08', end: '2026-11-03', fillUps: 5 }, earlier: { start: '2025-09-08', end: '2025-11-03', fillUps: 4 }, currencies: [{ currency: 'HUF', recent: 120000, earlier: 100000 }], consumption: null, distance: null }
        : figure === 'CONSUMPTION' && period.kind === 'SINCE' ? { recent: { start: '2026-03-15', end: '2026-11-03', fillUps: 6 }, earlier: { start: '2025-08-27', end: '2026-03-14', fillUps: 9 }, currencies: [], consumption: { recent: 6, earlier: 6.4, recentFillUps: 6, earlierFillUps: 9 }, distance: null }
        : undefined
    },
  })

  const region = await screen.findByRole('region', { name: 'Dashboard' })
  expect(await within(region).findByText('HUF 20,000 (20%) more than the same 8 weeks a year earlier')).toBeInTheDocument()
  expect(within(region).getByText('Last 8 weeks · vs a year earlier')).toBeInTheDocument() // the chip; a year earlier is not spending's own comparison
  expect(await within(region).findByText('uses 6% less fuel than the 200 days before (6 fill-ups against 9)')).toBeInTheDocument() // a window by dates names the fill-ups
  expect(within(region).getByText('6 fill-ups in the period')).toBeInTheDocument()
  expect(within(region).getByText(/^Since /)).toBeInTheDocument()
  expect(await within(region).findByText('1,500 km (14%) more than last year to this day')).toBeInTheDocument() // no period of its own: its default
  expect(within(region).getByText('This year')).toBeInTheDocument()
  expect(state.trendRequests).toEqual(
    expect.arrayContaining([
      { vehicleId: 'v1', figure: 'SPENDING', period: weeks },
      { vehicleId: 'v1', figure: 'CONSUMPTION', period: since },
      { vehicleId: 'v1', figure: 'DISTANCE', period: { kind: 'THIS_YEAR', unit: null, count: null, since: null, whole: false, against: 'YEAR_EARLIER' } },
    ]),
  )
})

it('says when a trend figure has nothing to compare with', async () => {
  setup(fakeVehicle({ summary: fakeSummary({ trends: { spending: [], costPerDistance: [], consumption: null, distance: { recent: 0, earlier: 0 } } }) }), [], undefined, {
    signedIn: true,
    arranged: (s) => (s.dashboard.shared = layout({ tiles: [fakeTile('spendingTrend', 0, 0, 1, 3), fakeTile('consumptionTrend', 1, 0, 1, 3), fakeTile('distanceTrend', 2, 0, 1, 3)] })),
  })

  const region = await screen.findByRole('region', { name: 'Dashboard' })
  expect(await within(region).findByText('needs 3 earlier fill-ups to compare with')).toBeInTheDocument()
  await waitFor(() => expect(within(region).getAllByText('–')).toHaveLength(2)) // spending and consumption have no figure
  expect(await within(region).findByText('nothing to compare with yet')).toBeInTheDocument()
})

it('words a consumption trend with few fill-ups before, and a cost per distance with nothing driven lately', async () => {
  setup(fakeVehicle({ summary: fakeSummary({ trends: { spending: [], costPerDistance: [{ currency: 'HUF', recent: null, earlier: 1500 }], consumption: { recent: 7.5, earlier: null, recentFillUps: 4, earlierFillUps: 0 }, distance: null } }) }), [], undefined, {
    signedIn: true,
    arranged: (s) => (s.dashboard.shared = layout({ tiles: [fakeTile('costPerDistance', 0, 0, 1, 4), fakeTile('consumptionTrend', 1, 0, 1, 4), fakeTile('distanceTrend', 2, 0, 1, 4)] })),
  })

  const region = await screen.findByRole('region', { name: 'Dashboard' })
  expect(await within(region).findByText(whole('7.5 L/100 km'))).toBeInTheDocument() // the latest mean shows as soon as there is one
  expect(within(region).getByText('last 4 fill-ups')).toBeInTheDocument()
  expect(within(region).getByText('needs 3 earlier fill-ups to compare with')).toBeInTheDocument()
  expect(await within(region).findByText('nothing driven in this period')).toBeInTheDocument() // the recent window is the empty one
  await waitFor(() => expect(within(region).getAllByText('–')).toHaveLength(2)) // cost per distance and distance (no reading at all)
})

it('starts from the Usage preset: its figures and charts, saved as tiles', async () => {
  const { ui, state } = setup(fakeVehicle(), [], undefined, { signedIn: true })
  await screen.findByRole('heading', { name: 'Monthly costs' })
  await ui.click(screen.getByRole('button', { name: 'Customize' }))

  await choose(ui, 'Start from a preset', 'Usage')
  expect(said()).toHaveTextContent('Started from Usage.')
  await ui.click(screen.getByRole('button', { name: 'Save dashboard' }))

  await waitFor(() => expect(state.calls.SaveDashboardLayout).toHaveLength(1))
  expect((state.calls.SaveDashboardLayout![0] as { input: { tiles: { id: string }[] } }).input.tiles.map((t) => t.id)).toEqual([
    'odometer', 'distanceTrend', 'consumption', 'consumptionTrend',
    'preset:distanceYearEarlier', 'preset:consumptionTrend', 'preset:distanceAndConsumption', 'preset:distance',
  ])
  await waitFor(async () => expect(await chartTitles()).toEqual(['Distance, this year and last', 'Consumption trend', 'Distance and consumption', 'Distance per month']))
  expect(figureLabels()).toEqual(['Odometer', 'Distance driven', 'Average consumption', 'Consumption change'])
})

it('adds a ready-made chart as it is from Add widget, or customizes it into a chart of one\'s own: saved and added, the original still offered', async () => {
  const { ui, state } = setup(fakeVehicle(), [], undefined, { arranged: (s) => (s.dashboard.shared = layout({ tiles: [fakeTile('odometer', 0, 0, 1, 4)] })) })
  await screen.findByText('Odometer')

  // As it is: a built-in tile, nothing saved.
  let dialog = await addWidget(ui, 'Monthly costs')
  expect(within(dialog).getByText(/Total spend by month/)).toBeInTheDocument()
  expect(within(dialog).queryByLabelText('Title')).not.toBeInTheDocument() // folded until Customize this chart
  await ui.click(within(dialog).getByRole('button', { name: 'Add to the dashboard' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(said()).toHaveTextContent('Monthly costs added.')
  expect(state.calls.SaveChart).toBeUndefined()
  expect(await screen.findByRole('heading', { name: 'Monthly costs' })).toBeInTheDocument()

  // Customized: the builder's fields start from its recipe and title; once anything differs it is saved as one's own and added.
  await ui.click(screen.getByRole('button', { name: 'Add widget' }))
  dialog = await screen.findByRole('dialog', { name: 'Add widget' })
  expect(within(dialog).getByRole('button', { name: /^Monthly costs\s*On the dashboard$/ })).toBeInTheDocument() // on the dashboard now: marked so (#247)
  await ui.click(within(dialog).getByRole('button', { name: 'Consumption' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Customize this chart' }))
  expect(within(dialog).getByLabelText('Title')).toHaveValue('Consumption')
  expect(screen.getByRole('combobox', { name: 'What to measure' })).toHaveTextContent('Average consumption')
  expect(within(dialog).getByRole('button', { name: 'Add to the dashboard' })).toBeInTheDocument() // nothing changed yet
  await ui.clear(within(dialog).getByLabelText('Title'))
  await ui.type(within(dialog).getByLabelText('Title'), 'Consumption, my way')
  await ui.click(within(dialog).getByRole('button', { name: 'Save and add' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect(state.calls.SaveChart![0]).toMatchObject({ input: { id: null, title: 'Consumption, my way', config: { metric: 'AVERAGE_CONSUMPTION', kind: 'LINE', range: 'LAST12_MONTHS' } } })
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(await screen.findByRole('heading', { name: 'Consumption, my way' })).toBeInTheDocument()
  expect(said()).toHaveTextContent('Consumption, my way added.')
  await ui.click(screen.getByRole('button', { name: 'Add widget' }))
  expect(within(await screen.findByRole('dialog', { name: 'Add widget' })).getByRole('button', { name: 'Consumption' })).toBeInTheDocument() // the ready-made one stays available
})

// ---- the builder ----------------------------------------------------------------------------------------------

it('builds a chart from a metric, grouping, type and a preset period, with a live preview', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  const dialog = await addWidget(ui)
  await ui.type(within(dialog).getByLabelText('Title'), 'Volume per quarter')
  await choose(ui, 'What to measure', 'Fuel volume')
  await pick(ui, 'Group by', 'Quarter')
  await pick(ui, 'Chart type', 'Line')
  await pick(ui, 'Period', 'Half a year')
  await waitFor(() => expect(state.chartRequests.at(-1)!.config).toMatchObject({ metric: 'FUEL_VOLUME', grouping: 'QUARTER', kind: 'LINE', range: 'LAST6_MONTHS' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save and add' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect(state.calls.SaveChart![0]).toEqual({
    input: { id: null, vehicleId: 'v1', title: 'Volume per quarter', shared: true, config: { metric: 'FUEL_VOLUME', grouping: 'QUARTER', kind: 'LINE', range: 'LAST6_MONTHS', stacked: false, from: null, to: null, trend: 'NONE', runningTotal: false, compareYearEarlier: false, secondMetric: null, secondKind: null } },
  })
  expect(await screen.findByRole('heading', { name: 'Volume per quarter' })).toBeInTheDocument() // added to the dashboard being arranged
  expect(said()).toHaveTextContent('Volume per quarter added.')
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
})

it('offers one month, three months, half a year, a year, and a custom from-to period', async () => {
  const { ui } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  await addWidget(ui)

  const options = within(screen.getByRole('radiogroup', { name: 'Period' })).getAllByRole('radio').map((o) => o.getAttribute('aria-label') ?? o.closest('label')?.textContent)
  expect(options).toEqual(['1 month', '3 months', 'Half a year', 'One year', 'This year', 'Last year', 'All time', 'Custom (from – to)'])
})

it('a custom period needs both dates, is sent as chosen, and is previewed once it is complete', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  const dialog = await addWidget(ui)
  await ui.type(within(dialog).getByLabelText('Title'), 'Spring')
  expect(within(dialog).queryByLabelText('From')).not.toBeInTheDocument()

  await pick(ui, 'Period', 'Custom (from – to)')
  expect(within(dialog).getByText('Choose the first and the last day of the period.')).toBeInTheDocument()
  expect(within(dialog).getByRole('button', { name: 'Save and add' })).toBeDisabled()
  const before = state.chartRequests.length
  await typeDate(ui, await findDateField('From', dialog), '2026-03-01')
  expect(state.chartRequests.length).toBe(before) // not previewed until both dates are there
  await typeDate(ui, await findDateField('To', dialog), '2026-05-31')
  await waitFor(() => expect(state.chartRequests.at(-1)!.config).toMatchObject({ range: 'CUSTOM', from: '2026-03-01', to: '2026-05-31' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save and add' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect((state.calls.SaveChart![0] as { input: { config: unknown } }).input.config).toMatchObject({ range: 'CUSTOM', from: '2026-03-01', to: '2026-05-31' })
})

it('does not send dates for a preset period, even after a custom one was chosen first', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  const dialog = await addWidget(ui)
  await ui.type(within(dialog).getByLabelText('Title'), 'Back to preset')
  await pick(ui, 'Period', 'Custom (from – to)')
  await typeDate(ui, await findDateField('From', dialog), '2026-03-01')
  await typeDate(ui, await findDateField('To', dialog), '2026-05-31')

  await pick(ui, 'Period', '3 months')
  await ui.click(within(dialog).getByRole('button', { name: 'Save and add' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect((state.calls.SaveChart![0] as { input: { config: unknown } }).input.config).toMatchObject({ range: 'LAST3_MONTHS', from: null, to: null })
})

it('offers a trend line, a running total and a year earlier for a chart over time, never a trend with a running total, and sends them', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  const dialog = await addWidget(ui)
  await ui.type(within(dialog).getByLabelText('Title'), 'Where it goes')
  const runningTotal = () => extra('Running total')

  await pick(ui, 'Trend line', 'Linear trend')
  await ui.click(runningTotal()!)
  expect(radio('Trend line', 'None')).toBeChecked() // a running total has no trend line
  await pick(ui, 'Trend line', 'Moving average (3 periods)')
  expect(runningTotal()).not.toBeChecked() // and the other way round
  await ui.click(extra('Compare with a year earlier'))

  await choose(ui, 'What to measure', 'Average consumption')
  expect(runningTotal()).toBeDisabled() // an average does not add up: dimmed, never hidden
  await choose(ui, 'What to measure', 'Expense cost')
  await pick(ui, 'Group by', 'Expense category')
  expect(radio('Trend line', 'Linear trend')).toBeDisabled() // not over time
  expect(extra('Compare with a year earlier')).toBeDisabled()

  await pick(ui, 'Group by', 'Month')
  await pick(ui, 'Trend line', 'Moving average (3 periods)')
  await ui.click(extra('Compare with a year earlier'))
  await waitFor(() => expect(state.chartRequests.at(-1)!.config).toMatchObject({ metric: 'EXPENSE_COST', trend: 'MOVING_AVERAGE', compareYearEarlier: true }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save and add' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect((state.calls.SaveChart![0] as { input: { config: unknown } }).input.config).toMatchObject({ metric: 'EXPENSE_COST', grouping: 'MONTH', trend: 'MOVING_AVERAGE', runningTotal: false, compareYearEarlier: true })
})

it('adds a second measure to a chart over time, never the first one again, and sends it with how it is drawn', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  const dialog = await addWidget(ui)
  await ui.type(within(dialog).getByLabelText('Title'), 'Distance and consumption')
  await choose(ui, 'What to measure', 'Distance driven')
  expect(screen.queryByRole('radiogroup', { name: 'Second measure drawn as' })).not.toBeInTheDocument()

  await ui.click(screen.getByRole('combobox', { name: 'Second measure' }))
  const options = (await screen.findAllByRole('option')).map((o) => o.textContent)
  expect(options[0]).toBe('None')
  expect(options).not.toContain('Distance driven') // the first measure
  await ui.click(screen.getByRole('option', { name: 'Average consumption' }))
  expect(radio('Second measure drawn as', 'Line')).toBeChecked()
  await waitFor(() => expect(state.chartRequests.at(-1)!.config).toMatchObject({ metric: 'DISTANCE', secondMetric: 'AVERAGE_CONSUMPTION', secondKind: 'LINE' }))

  // The preview's table (a dialog over the builder's) writes each measure in its own unit, the second named by its metric.
  await ui.click(await within(dialog).findByRole('button', { name: 'Distance and consumption as a table' }))
  const table = within(await screen.findByRole('dialog', { name: 'Distance and consumption' })).getByRole('table')
  expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Period', 'Total', 'Average consumption'])
  expect(within(table).getByRole('rowheader', { name: 'Aug 2026' }).closest('tr')).toHaveTextContent(/1,000 km.*5 L\/100 km/)
  await ui.keyboard('{Escape}') // the table's dialog, back to the builder's
  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Distance and consumption' })).not.toBeInTheDocument())
  await ui.click(within(dialog).getByRole('button', { name: 'Save and add' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect((state.calls.SaveChart![0] as { input: { config: unknown } }).input.config).toMatchObject({ metric: 'DISTANCE', secondMetric: 'AVERAGE_CONSUMPTION', secondKind: 'LINE' })
})

it('offers the cost per distance among what to measure', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  const dialog = await addWidget(ui)

  await choose(ui, 'What to measure', 'Fuel cost per distance')
  await waitFor(() => expect(state.chartRequests.at(-1)!.config).toMatchObject({ metric: 'FUEL_COST_PER_DISTANCE' }))
  await ui.click(await within(dialog).findByRole('button', { name: 'Preview as a table' })) // the preview is titled so until a title is typed

  expect(within(await screen.findByRole('dialog', { name: 'Preview' })).getByRole('rowheader', { name: 'Aug 2026' }).closest('tr')).toHaveTextContent('HUF 1,000 / 100 km') // the preview's table in its unit
})

it('only offers combinations that make sense', async () => {
  const { ui } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  const dialog = await addWidget(ui)
  expect(dialog).toBeInTheDocument()
  // What a choice rules out is dimmed, never hidden, so the rows keep their shape; the tooltip says why.
  expect(radio('Group by', 'Expense category')).toBeDisabled() // only for expense costs
  expect(extra('Split into fuel and expenses')).toBeEnabled() // total spend (the default)

  await choose(ui, 'What to measure', 'Average consumption')
  expect(radio('Chart type', 'Donut')).toBeDisabled() // an average cannot be cut into parts
  expect(extra('Split into fuel and expenses')).toBeDisabled()

  await choose(ui, 'What to measure', 'Expense cost')
  expect(radio('Group by', 'Expense category')).toBeEnabled()
  await pick(ui, 'Group by', 'Expense category')
  await pick(ui, 'Chart type', 'Donut')

  await choose(ui, 'What to measure', 'Fuel cost') // the category grouping and the donut stay valid or are reset
  expect(radio('Group by', 'Month')).toBeChecked()
})

it('splits total spend into fuel and expenses when asked', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  const dialog = await addWidget(ui)
  await ui.type(within(dialog).getByLabelText('Title'), 'Split')

  await ui.click(extra('Split into fuel and expenses'))
  await ui.click(within(dialog).getByRole('button', { name: 'Save and add' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect((state.calls.SaveChart![0] as { input: { config: unknown } }).input.config).toMatchObject({ metric: 'TOTAL_SPEND', stacked: true })
})

it('a chart made on everyone\'s dashboard is shared, said so; on one\'s own it is a free choice', async () => {
  const { ui, state } = setup(fakeVehicle({ logAccess: 'EDIT' }), [], undefined, { signedIn: true })
  await screen.findByText(/Compose your own charts/)
  let dialog = await addWidget(ui)
  await ui.type(within(dialog).getByLabelText('Title'), 'For all')
  const share = () => within(dialog).getByRole('switch', { name: 'Share with everyone who can see this vehicle' })
  expect(share()).toBeChecked()
  expect(share()).toBeDisabled()
  expect(within(dialog).getByText(/On everyone’s dashboard a chart has to be shared/)).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Save and add' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect(state.calls.SaveChart![0]).toMatchObject({ input: { shared: true } })
  expect(await screen.findByText('Shared')).toBeInTheDocument()

  await ui.click(screen.getByRole('radio', { name: 'Just me' }))
  await ui.click(screen.getByRole('button', { name: 'Add widget' }))
  dialog = await screen.findByRole('dialog', { name: 'Add widget' })
  await ui.click(within(dialog).getByRole('button', { name: 'Blank chart' }))
  expect(share()).not.toBeChecked()
  expect(share()).toBeEnabled()
})

it('viewers can make their own charts but are not offered sharing', async () => {
  const { ui } = setup(fakeVehicle({ canEdit: false, logAccess: 'VIEW' }), [])
  await screen.findByText(/Compose your own charts/)
  const dialog = await addWidget(ui)

  expect(within(dialog).queryByRole('switch', { name: /Share with everyone/ })).not.toBeInTheDocument()
})

it('edits a saved chart, starting from its recipe', async () => {
  const { ui, state } = setup(fakeVehicle(), [
    fakeChart({ id: 'c1', title: 'Old', metric: 'FUEL_VOLUME', grouping: 'YEAR', kind: 'AREA', range: 'CUSTOM', rangeFrom: '2025-01-01', rangeTo: '2025-12-31', trend: 'LINEAR', compareYearEarlier: true }),
  ])
  await screen.findByRole('heading', { name: 'Old' })
  await customize(ui)

  await ui.click(screen.getByRole('button', { name: 'Edit the chart Old' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit chart' })
  expect(within(dialog).getByLabelText('Title')).toHaveValue('Old')
  expect(screen.getByRole('combobox', { name: 'What to measure' })).toHaveTextContent('Fuel volume')
  expect(radio('Group by', 'Year')).toBeChecked()
  expect(radio('Chart type', 'Area')).toBeChecked()
  expect(radio('Trend line', 'Linear trend')).toBeChecked()
  expect(extra('Compare with a year earlier')).toBeChecked()
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
  const dialog = await addWidget(ui)
  await ui.type(within(dialog).getByLabelText('Title'), 'One too many')

  await ui.click(within(dialog).getByRole('button', { name: 'Save and add' }))

  expect(await within(dialog).findByRole('alert')).toHaveTextContent('You can have at most 30 charts for a vehicle.')
  expect(screen.getByRole('dialog', { name: 'Add widget' })).toBeInTheDocument()
})

it('blocks a chart without a title', async () => {
  const { ui, state } = setup(fakeVehicle(), [])
  await screen.findByText(/Compose your own charts/)
  const dialog = await addWidget(ui)

  await ui.click(within(dialog).getByRole('button', { name: 'Save and add' }))

  expect(await within(dialog).findByText('Title is required')).toBeInTheDocument()
  expect(state.calls.SaveChart).toBeUndefined()
})

// ---- accessibility ----------------------------------------------------------------------------------------------

// The dashboard's axe scans, a few per test (CLAUDE.md): a scan of the whole page takes seconds with coverage on, so each test gets the
// 30 s the accessibility files give theirs, and a slow runner names the element rather than a timeout.
// ---- a ready-made chart customized from its tile (#247) --------------------------------------------------------------------

/** The Usage preset applied while customizing: its charts are ready-made tiles. */
const usagePreset = async (ui: ReturnType<typeof userEvent.setup>) => {
  await screen.findByRole('heading', { name: 'Monthly costs' })
  await customize(ui)
  await choose(ui, 'Start from a preset', 'Usage')
  await screen.findByRole('heading', { name: 'Consumption trend' })
}
const saveAsMine = () => within(screen.getByRole('dialog', { name: 'Customize chart' })).getByRole('button', { name: 'Save as my chart' })

it("customizes a ready-made chart from its tile's menu into a chart of one's own, in the tile's place, size and pin (#247)", async () => {
  const { ui, state } = setup(fakeVehicle(), [], undefined, { signedIn: true })
  await usagePreset(ui)
  await tileMenu(ui, 'Consumption trend', 'Pin')

  // Only a ready-made chart's menu offers it.
  await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument())
  await ui.click(screen.getByRole('button', { name: 'Options for Odometer' }))
  expect(await screen.findByRole('menuitem', { name: 'Move later' })).toBeInTheDocument()
  expect(screen.queryByRole('menuitem', { name: 'Customize as my chart…' })).not.toBeInTheDocument()
  await ui.keyboard('{Escape}')

  await tileMenu(ui, 'Consumption trend', 'Customize as my chart…')
  const dialog = await screen.findByRole('dialog', { name: 'Customize chart' })
  expect(dialog).toHaveAccessibleDescription(/Consumption trend is a ready-made chart\. Saved, it becomes a chart of your own and takes this tile's place and size/)
  // Its fields open, from the ready-made chart's title and recipe.
  expect(within(dialog).getByLabelText('Title')).toHaveValue('Consumption trend')
  expect(screen.getByRole('combobox', { name: 'What to measure' })).toHaveTextContent('Average consumption')
  expect(radio('Group by', 'Quarter')).toBeChecked()
  expect(radio('Period', 'All time')).toBeChecked()
  expect(radio('Trend line', 'Linear trend')).toBeChecked()
  // Everyone's dashboard: shared, and it must be.
  expect(within(dialog).getByRole('switch', { name: /Share with everyone/ })).toBeChecked()
  expect(within(dialog).getByRole('switch', { name: /Share with everyone/ })).toBeDisabled()
  // Nothing differs yet: Save is off and says why; a change turns it on, undoing it off again.
  expect(saveAsMine()).toBeDisabled()
  expect(within(dialog).getByText('Change something to save it as your own chart.')).toBeInTheDocument()
  await pick(ui, 'Group by', 'Year')
  expect(saveAsMine()).toBeEnabled()
  expect(within(dialog).queryByText('Change something to save it as your own chart.')).not.toBeInTheDocument()
  await pick(ui, 'Group by', 'Quarter')
  expect(saveAsMine()).toBeDisabled()
  await pick(ui, 'Group by', 'Year')
  await ui.clear(within(dialog).getByLabelText('Title'))
  await ui.type(within(dialog).getByLabelText('Title'), 'Consumption by year')
  await ui.click(saveAsMine())

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect(state.calls.SaveChart![0]).toMatchObject({ input: { id: null, title: 'Consumption by year', shared: true, config: { metric: 'AVERAGE_CONSUMPTION', grouping: 'YEAR', range: 'ALL', trend: 'LINEAR' } } })
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(said()).toHaveTextContent('Consumption trend is now your own chart, Consumption by year, in the same place.')
  // The focus is on the new tile's menu button: the one that opened the dialog went with the ready-made tile.
  await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Options for Consumption by year' })))
  expect(await chartTitles()).toEqual(['Distance, this year and last', 'Consumption by year', 'Distance and consumption', 'Distance per month'])
  expect(within(await cardOf('Consumption by year')).getByRole('button', { name: 'Edit the chart Consumption by year' })).toBeInTheDocument()

  // The ready-made one is off the dashboard now, so Add widget offers it to add as it is again.
  await ui.click(screen.getByRole('button', { name: 'Add widget' }))
  const adding = await screen.findByRole('dialog', { name: 'Add widget' })
  expect(within(adding).getByRole('button', { name: 'Consumption trend' })).toBeInTheDocument()
  await ui.keyboard('{Escape}')
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

  await ui.click(screen.getByRole('button', { name: 'Save dashboard' }))
  await waitFor(() => expect(state.calls.SaveDashboardLayout).toHaveLength(1))
  const tiles = (state.calls.SaveDashboardLayout![0] as { input: { tiles: { id: string; pinned: boolean }[] } }).input.tiles
  expect(tiles).toContainEqual(expect.objectContaining({ id: expect.stringMatching(/^chart:/), x: 2, y: 4, w: 2, h: 8, pinned: true }))
  expect(tiles.map((x) => x.id)).not.toContain('preset:consumptionTrend')
})

it("customizes a ready-made chart from its tile on one's own dashboard into a private chart; Cancel leaves the tile and its focus (#247)", async () => {
  const { ui, state } = setup(fakeVehicle(), [], undefined, { signedIn: true })
  await screen.findByRole('heading', { name: 'Monthly costs' })
  await customize(ui)
  await ui.click(screen.getByRole('radio', { name: 'Just me' }))

  await tileMenu(ui, 'Fuel price', 'Customize as my chart…')
  let dialog = await screen.findByRole('dialog', { name: 'Customize chart' })
  await ui.click(within(dialog).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Options for Fuel price' })))
  expect(state.calls.SaveChart).toBeUndefined()

  await tileMenu(ui, 'Fuel price', 'Customize as my chart…')
  dialog = await screen.findByRole('dialog', { name: 'Customize chart' })
  const share = within(dialog).getByRole('switch', { name: /Share with everyone/ })
  expect(share).not.toBeChecked()
  expect(share).toBeEnabled()
  await pick(ui, 'Period', 'This year')
  await ui.click(saveAsMine())
  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect(state.calls.SaveChart![0]).toMatchObject({ input: { title: 'Fuel price', shared: false, config: { metric: 'AVERAGE_PRICE_PER_UNIT', range: 'THIS_YEAR' } } })
  await waitFor(() => expect(said()).toHaveTextContent('Fuel price is now your own chart, in the same place.'))
  await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Options for Fuel price' })))
  expect(within(await cardOf('Fuel price')).getByRole('button', { name: 'Edit the chart Fuel price' })).toBeInTheDocument()
})

it('offers a ready-made chart already on the dashboard in Add widget too, to customize only: saved once it differs, added beside it (#247)', async () => {
  const { ui, state } = setup(fakeVehicle(), [], undefined, { signedIn: true })
  await usagePreset(ui)
  await ui.click(screen.getByRole('button', { name: 'Add widget' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add widget' })
  expect(within(dialog).getByRole('heading', { name: 'Spent this month' })).toBeInTheDocument() // it opens on something to add as it is
  await ui.click(within(dialog).getByRole('button', { name: /^Consumption trend\s*On the dashboard$/ }))

  expect(within(dialog).getByText(/so here it can only be customized/)).toBeInTheDocument()
  expect(within(dialog).queryByRole('button', { name: 'Add to the dashboard' })).not.toBeInTheDocument()
  expect(within(dialog).queryByRole('button', { name: 'Customize this chart' })).not.toBeInTheDocument() // its fields are open
  expect(within(dialog).getByLabelText('Title')).toHaveValue('Consumption trend')
  expect(within(dialog).getByRole('button', { name: 'Save and add' })).toBeDisabled()
  expect(within(dialog).getByText('Change something to save it as your own chart.')).toBeInTheDocument()
  await pick(ui, 'Trend line', 'Moving average (3 periods)')
  await ui.click(within(dialog).getByRole('button', { name: 'Save and add' }))

  await waitFor(() => expect(state.calls.SaveChart).toHaveLength(1))
  expect(state.calls.SaveChart![0]).toMatchObject({ input: { id: null, title: 'Consumption trend', shared: true, config: { trend: 'MOVING_AVERAGE' } } })
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  // The copy at the bottom, the ready-made one where it was.
  await waitFor(async () => expect(await chartTitles()).toEqual(['Distance, this year and last', 'Consumption trend', 'Distance and consumption', 'Distance per month', 'Consumption trend']))
})

const a11ySetup = () => setup(fakeVehicle(), [fakeChart(), fakeChart({ id: 'c2', title: 'Shared one', isShared: true, canEdit: false, canDelete: false, createdBy: person('Bob') })])
const check = async (node: HTMLElement) => {
  const results = await axe(node, { rules: { 'color-contrast': { enabled: false } } })
  expect(results.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`)).toEqual([])
}

it("has no accessibility violations: the dashboard, and a chart's table in its dialog", { timeout: 30_000 }, async () => {
  const { ui } = a11ySetup()
  await screen.findByRole('heading', { name: 'Shared one' })
  await check(document.body)

  await ui.click(within(await cardOf('Monthly costs')).getByRole('button', { name: 'Monthly costs as a table' }))
  await screen.findByRole('dialog', { name: 'Monthly costs' })
  await check(document.body)
})

it('has no accessibility violations: the dashboard being arranged, its strips over the tiles', { timeout: 30_000 }, async () => {
  const { ui } = a11ySetup()
  await screen.findByRole('heading', { name: 'Shared one' })
  await customize(ui)
  await check(document.body)
})

it('has no accessibility violations: the Add widget dialog, its list and the blank chart', { timeout: 30_000 }, async () => {
  const { ui } = a11ySetup()
  await screen.findByRole('heading', { name: 'Shared one' })
  await customize(ui)
  await ui.click(screen.getByRole('button', { name: 'Add widget' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add widget' })
  await within(dialog).findByRole('heading', { name: 'Spending' }) // the first starting point, picked
  await check(document.body)
  await ui.click(within(dialog).getByRole('button', { name: 'Blank chart' }))
  await within(dialog).findByLabelText('Title')
  await check(document.body)
})

it("has no accessibility violations: a ready-made chart's Customize dialog, opened from its tile's menu (#247)", { timeout: 30_000 }, async () => {
  const { ui } = a11ySetup()
  await screen.findByRole('heading', { name: 'Shared one' })
  await customize(ui)
  await tileMenu(ui, 'Monthly costs', 'Customize as my chart…')
  await screen.findByRole('dialog', { name: 'Customize chart' })
  await check(document.body)
})
