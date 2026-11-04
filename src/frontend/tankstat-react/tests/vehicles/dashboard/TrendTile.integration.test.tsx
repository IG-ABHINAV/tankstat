import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { axe } from 'vitest-axe'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { connectivity } from '../../../src/offline/connectivity.ts'
import { answered, server } from '../../support/server.ts'
import { fakeDashboardBackend, fakeLogBackend, fakeTile, fakeVehicle, healthHandler, renderWithApollo, sessionHandler, stubViewport, user, type FakeLayout, type FakePeriod, type FakeTrend } from '../../support/mocks.tsx'
import { findDateField, typeDate } from '../../support/dates.ts'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

type State = ReturnType<typeof fakeDashboardBackend>['state']

/** Spending over a period of its own: HUF 120,000 against 100,000, whatever the period (the words name it). */
const spending = (): FakeTrend => ({
  recent: { start: '2026-09-08', end: '2026-11-03', fillUps: 3 },
  earlier: { start: '2025-09-08', end: '2025-11-03', fillUps: 2 },
  currencies: [{ currency: 'HUF', recent: 120000, earlier: 100000 }],
  consumption: null,
  distance: null,
})
const isDefault = (p: FakePeriod) => p.kind === 'LAST' && p.unit === 'MONTHS' && p.count === 12 && p.whole
const layout = (tiles: FakeLayout['tiles']): FakeLayout => ({ tiles, hidden: [] })
const shownTiles = () => [fakeTile('spendingTrend', 0, 0, 1, 4), fakeTile('odometer', 1, 0, 1, 3)]

function setup({ viewport = 'desktop', arranged }: { viewport?: 'desktop' | 'phone'; arranged?: (state: State) => void } = {}) {
  stubViewport(viewport)
  const vehicle = fakeVehicle()
  const dashboard = fakeDashboardBackend(vehicle, [])
  dashboard.state.dashboard.shared = layout(shownTiles())
  dashboard.state.trendFor = (figure, period) => (figure === 'SPENDING' && !isDefault(period) ? spending() : undefined)
  arranged?.(dashboard.state)
  server.use(sessionHandler('STANDALONE', () => user()), healthHandler, ...fakeLogBackend(vehicle, []).handlers, ...dashboard.handlers)
  renderWithApollo(<App />, '/vehicles/v1')
  return { ...dashboard, ui: userEvent.setup() }
}

const chip = (period: string) => screen.findByRole('button', { name: `Spending period: ${period}. Change it` })
async function openPeriod(ui: ReturnType<typeof userEvent.setup>, period = '12 full months') {
  await ui.click(await chip(period))
  return screen.findByRole('dialog', { name: 'Period of Spending' })
}
/** The last 8 weeks against the same weeks a year earlier, on the Last tab. */
async function lastEightWeeksAYearEarlier(ui: ReturnType<typeof userEvent.setup>, dialog: HTMLElement) {
  await ui.click(within(dialog).getByRole('tab', { name: 'Last' }))
  expect(within(dialog).getByRole('spinbutton', { name: 'Number' })).toHaveValue(8) // the tab starts from the last 8 weeks
  await ui.click(within(dialog).getByRole('combobox', { name: 'Compared with' }))
  await ui.click(await screen.findByRole('option', { name: 'The same dates a year earlier' }))
}
const saved = (state: State) => (state.calls.SaveDashboardLayout!.at(-1) as { input: { personal: boolean; tiles: { id: string; period?: unknown }[] } }).input
const eightWeeks = { kind: 'LAST', unit: 'WEEKS', count: 8, since: null, whole: false, against: 'YEAR_EARLIER' }

it('sets a trend figure\'s period from its chip: the sentence and the timeline follow, Apply saves it into the vehicle\'s dashboard (#230)', async () => {
  const { ui, state } = setup()
  // Apply submits the popover's form: the app's to handle, never the browser's (a sent form loads the page again; jsdom only says so).
  const sent: boolean[] = []
  const onSubmit = (e: Event) => sent.push(e.defaultPrevented)
  document.addEventListener('submit', onSubmit)
  expect(await screen.findByText('HUF 60,000 (12%) more than the 12 months before')).toBeInTheDocument()
  const dialog = await openPeriod(ui)
  expect(within(dialog).getByRole('tab', { name: 'Quick', selected: true })).toBeInTheDocument() // the default is a quick pick
  expect(within(dialog).getByText('Apply saves it into the vehicle’s dashboard, for everyone who sees it.')).toBeInTheDocument()

  await lastEightWeeksAYearEarlier(ui, dialog)
  expect(await within(dialog).findByText('HUF 20,000 (20%) more than the same 8 weeks a year earlier')).toBeInTheDocument() // the server's answer for the draft
  expect(within(dialog).getByTestId('trend-timeline')).toHaveAttribute('aria-hidden', 'true')
  expect(within(dialog).getByRole('switch', { name: 'Whole weeks, Monday to Sunday' })).not.toBeChecked() // off by default: the window ends today
  await ui.click(within(dialog).getByRole('button', { name: 'Apply' }))

  expect(await screen.findByText('Period saved.')).toBeInTheDocument()
  document.removeEventListener('submit', onSubmit)
  expect(sent).toEqual([true])
  const input = saved(state)
  expect(input.personal).toBe(false)
  expect(input.tiles).toEqual([{ id: 'spendingTrend', x: 0, y: 0, w: 1, h: 4, pinned: false, period: eightWeeks }, { id: 'odometer', x: 1, y: 0, w: 1, h: 3, pinned: false }])
  expect(await chip('Last 8 weeks · vs a year earlier')).toBeInTheDocument() // read back after the refetch
  expect(screen.getByText('HUF 20,000 (20%) more than the same 8 weeks a year earlier')).toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
})

it('saves into one\'s own arrangement when there is one, and makes one from what a viewer sees', async () => {
  const own = setup({ arranged: (s) => (s.dashboard.personal = layout([fakeTile('odometer', 0, 0, 1, 3), fakeTile('spendingTrend', 1, 0, 1, 4)])) })
  let dialog = await openPeriod(own.ui)
  expect(within(dialog).getByText('Apply saves it into your own arrangement of this dashboard.')).toBeInTheDocument()
  await own.ui.click(within(dialog).getByRole('radio', { name: '6 full months' }))
  await own.ui.click(within(dialog).getByRole('button', { name: 'Apply' }))
  await waitFor(() => expect(own.state.calls.SaveDashboardLayout).toHaveLength(1))
  expect(saved(own.state)).toMatchObject({ personal: true, tiles: [{ id: 'odometer' }, { id: 'spendingTrend', period: { kind: 'LAST', unit: 'MONTHS', count: 6, whole: true, against: 'BEFORE' } }] })
  expect(await chip('6 full months')).toBeInTheDocument()
  own.state.calls = {}
  document.body.innerHTML = ''

  const viewer = setup({ arranged: (s) => (s.dashboard.canChangeShared = false) })
  dialog = await openPeriod(viewer.ui)
  expect(within(dialog).getByText('Apply saves it into an arrangement of your own, made from what you see.')).toBeInTheDocument()
  await lastEightWeeksAYearEarlier(viewer.ui, dialog)
  await viewer.ui.click(within(dialog).getByRole('button', { name: 'Apply' }))
  await waitFor(() => expect(viewer.state.calls.SaveDashboardLayout).toHaveLength(1))
  expect(saved(viewer.state)).toMatchObject({ personal: true, tiles: [{ id: 'spendingTrend', period: eightWeeks }, { id: 'odometer' }] }) // the vehicle's tiles, as shown
  expect(viewer.state.dashboard.shared!.tiles[0]).not.toHaveProperty('period', eightWeeks) // the vehicle's stays as it was
})

it('sets a period since a day, checks the number, and offers whole periods only for weeks, months and years', async () => {
  const { ui, state } = setup()
  const dialog = await openPeriod(ui)
  await ui.click(within(dialog).getByRole('tab', { name: 'Last' }))
  const count = within(dialog).getByRole('spinbutton', { name: 'Number' })
  await ui.clear(count)
  await ui.type(count, '0')
  expect(await within(dialog).findByText('A period is 1 to 1000 long.')).toBeInTheDocument()
  expect(within(dialog).getByRole('button', { name: 'Apply' })).toBeDisabled()
  await ui.click(within(dialog).getByRole('combobox', { name: 'Unit' }))
  await ui.click(await screen.findByRole('option', { name: 'Days' }))
  expect(within(dialog).queryByRole('switch', { name: /^Whole/ })).not.toBeInTheDocument()
  expect(within(dialog).getByText('The period ends today.')).toBeInTheDocument()

  await ui.click(within(dialog).getByRole('tab', { name: 'Since' }))
  await typeDate(ui, await findDateField('First day', dialog), '2026-03-15')
  expect(await within(dialog).findByText('HUF 20,000 (20%) more than the 57 days before')).toBeInTheDocument() // the earlier window's days
  await ui.click(within(dialog).getByRole('button', { name: 'Apply' }))
  await waitFor(() => expect(state.calls.SaveDashboardLayout).toHaveLength(1))
  expect(saved(state).tiles[0].period).toEqual({ kind: 'SINCE', unit: null, count: null, since: '2026-03-15', whole: false, against: 'BEFORE' })
})

it('keeps a period with the tiles being arranged until the dashboard is saved, and adds a trend figure at the period chosen in Add widget', async () => {
  const { ui, state } = setup()
  await ui.click(await screen.findByRole('button', { name: 'Customize' }))
  // While arranging the chip is in the tile's strip, beside its name, not in the card under it (#262); the popover opens from there.
  const inStrip = await chip('12 full months')
  expect(inStrip.closest('.MuiCard-root')).toBeNull()
  expect(inStrip.closest('[data-tile="spendingTrend"]')).not.toBeNull()
  const dialog = await openPeriod(ui)
  expect(within(dialog).getByText('Kept with the arrangement you are editing until you save it.')).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('radio', { name: '6 full months' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Apply' }))
  expect(await screen.findByRole('status', { name: 'Last change to the arrangement' })).toHaveTextContent('Spending now shows 6 full months.')
  expect(state.calls.SaveDashboardLayout).toBeUndefined()
  expect(await chip('6 full months')).toBeInTheDocument()

  // The Add widget dialog's figure pane has the same controls (#230, the owner's choice).
  await ui.click(screen.getByRole('button', { name: 'Add widget' }))
  const add = await screen.findByRole('dialog', { name: 'Add widget' })
  await ui.click(within(add).getByRole('button', { name: 'Distance driven' }))
  expect(within(add).getByRole('tab', { name: 'Quick', selected: true })).toBeInTheDocument()
  expect(await within(add).findByText('This year', { selector: '.MuiChip-label' })).toBeInTheDocument() // its preview starts at its default
  await ui.click(within(add).getByRole('radio', { name: '12 full months' }))
  expect(await within(add).findByText('12 full months', { selector: '.MuiChip-label' })).toBeInTheDocument() // the preview follows
  await ui.click(within(add).getByRole('button', { name: 'Add to the dashboard' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  await ui.click(screen.getByRole('button', { name: 'Save dashboard' }))

  await waitFor(() => expect(state.calls.SaveDashboardLayout).toHaveLength(1))
  const tiles = saved(state).tiles
  expect(tiles.find((t) => t.id === 'spendingTrend')?.period).toEqual({ kind: 'LAST', unit: 'MONTHS', count: 6, since: null, whole: true, against: 'BEFORE' })
  expect(tiles.find((t) => t.id === 'distanceTrend')?.period).toEqual({ kind: 'LAST', unit: 'MONTHS', count: 12, since: null, whole: true, against: 'YEAR_EARLIER' }) // a quick pick sets the window, the comparison stays distance's own
})

it('cannot apply a period while the server is out of reach, and says so', async () => {
  const { ui, state } = setup()
  expect(await screen.findByText('HUF 60,000 (12%) more than the 12 months before')).toBeInTheDocument()
  await answered()
  act(() => connectivity.failed())
  const dialog = await openPeriod(ui)

  expect(within(dialog).getByText('Changing the period needs the server, which is out of reach right now.')).toBeInTheDocument()
  expect(within(dialog).getByRole('button', { name: 'Apply' })).toBeDisabled()
  await ui.click(within(dialog).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(state.calls.SaveDashboardLayout).toBeUndefined()
})

it('opens on a phone, and the popover has no accessibility violations', { timeout: 30_000 }, async () => {
  const { ui } = setup({ viewport: 'phone' })
  const dialog = await openPeriod(ui)
  await ui.click(within(dialog).getByRole('tab', { name: 'Last' }))
  expect(await within(dialog).findByText(/more than the 8 weeks before/)).toBeInTheDocument()

  const results = await axe(dialog, { rules: { 'color-contrast': { enabled: false } } }) // the real styles' contrast: axe.e2e.ts
  expect(results.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`)).toEqual([])
})
