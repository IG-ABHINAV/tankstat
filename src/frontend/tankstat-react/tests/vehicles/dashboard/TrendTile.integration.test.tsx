import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { axe } from 'vitest-axe'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { server } from '../../support/server.ts'
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
/** Customize: the chips are shown only while the dashboard is arranged, on the tiles' strips (#260). */
async function customize(ui: ReturnType<typeof userEvent.setup>) {
  await ui.click(await screen.findByRole('button', { name: 'Customize' }))
  await screen.findByRole('button', { name: 'Save dashboard' })
}
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
type SavedTile = { id: string; recipe?: { figure?: { template?: string | null; period?: unknown } | null } | null }
const saved = (state: State) => (state.calls.SaveDashboardLayout!.at(-1) as { input: { personal: boolean; tiles: SavedTile[] } }).input
/** A saved figure tile's period, by the built-in figure it was made from (#274: its recipe holds it). */
const periodOf = (tiles: SavedTile[], template: string) => tiles.find((t) => t.recipe?.figure?.template === template)?.recipe?.figure?.period
const figureRecipe = (measure: string, template: string, period: unknown) => ({ widget: 'FIGURE', title: null, chart: null, figure: { measure, period, sparkline: false, template }, template: null })
const eightWeeks = { kind: 'LAST', unit: 'WEEKS', count: 8, since: null, whole: false, against: 'YEAR_EARLIER' }

it('shows the chip only while arranging, on the tile\'s strip; the sentence and the timeline follow, Apply keeps it, Save dashboard saves it (#230, #260)', async () => {
  const { ui, state } = setup()
  // Apply submits the popover's form: the app's to handle, never the browser's (a sent form loads the page again; jsdom only says so).
  const sent: boolean[] = []
  const onSubmit = (e: Event) => sent.push(e.defaultPrevented)
  document.addEventListener('submit', onSubmit)
  expect(await screen.findByText('HUF 60,000 (12%) more than the 12 months before')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /^Spending period: / })).not.toBeInTheDocument() // outside Customize no chip: the arrow says the period

  await customize(ui)
  const onStrip = await chip('12 full months')
  expect(onStrip.closest('[data-tile="spendingTrend"]')).not.toBeNull()
  expect(within(onStrip.closest('[data-tile="spendingTrend"]')!.querySelector('.MuiCard-root')!).queryByRole('button', { name: /period/ })).not.toBeInTheDocument() // on the strip, not in the card
  const dialog = await openPeriod(ui)
  expect(within(dialog).getByRole('tab', { name: 'Quick', selected: true })).toBeInTheDocument() // the default is a quick pick
  expect(within(dialog).getByText('Kept with the arrangement you are editing until you save it.')).toBeInTheDocument()

  await lastEightWeeksAYearEarlier(ui, dialog)
  expect(await within(dialog).findByText('HUF 20,000 (20%) more than the same 8 weeks a year earlier')).toBeInTheDocument() // the server's answer for the draft
  expect(within(dialog).getByTestId('trend-timeline')).toHaveAttribute('aria-hidden', 'true')
  expect(within(dialog).getByRole('switch', { name: 'Whole weeks, Monday to Sunday' })).not.toBeChecked() // off by default: the window ends today
  await ui.click(within(dialog).getByRole('button', { name: 'Apply' }))
  expect(await screen.findByRole('status', { name: 'Last change to the arrangement' })).toHaveTextContent(/^Spending now shows Last 8 weeks/)
  expect(state.calls.SaveDashboardLayout).toBeUndefined() // kept with the arrangement: nothing saves at once (#260)
  document.removeEventListener('submit', onSubmit)
  expect(sent).toEqual([true])

  await ui.click(screen.getByRole('button', { name: 'Save dashboard' }))
  await waitFor(() => expect(state.calls.SaveDashboardLayout).toHaveLength(1))
  const input = saved(state)
  expect(input.personal).toBe(false)
  // Each figure a tile of its own with its recipe (#274), the period chosen in it.
  expect(input.tiles).toEqual([
    { id: expect.stringMatching(/^tile:/), x: 0, y: 0, w: 1, h: 4, pinned: false, recipe: figureRecipe('TOTAL_SPEND', 'spendingTrend', eightWeeks) },
    { id: expect.stringMatching(/^tile:/), x: 1, y: 0, w: 1, h: 3, pinned: false, recipe: figureRecipe('ODOMETER', 'odometer', null) },
  ])
  expect(await screen.findByText('HUF 20,000 (20%) more than the same 8 weeks a year earlier')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /^Spending period: / })).not.toBeInTheDocument() // arranged no more: no chip
})

it('sets a period since a day, checks the number, and offers whole periods only for weeks, months and years', async () => {
  const { ui, state } = setup()
  await customize(ui)
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
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  await ui.click(screen.getByRole('button', { name: 'Save dashboard' }))
  await waitFor(() => expect(state.calls.SaveDashboardLayout).toHaveLength(1))
  expect(periodOf(saved(state).tiles, 'spendingTrend')).toEqual({ kind: 'SINCE', unit: null, count: null, since: '2026-03-15', whole: false, against: 'BEFORE' })
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

  // The Add widget dialog's builder has the same period form (#230, #275), its figure starting at its default.
  await ui.click(screen.getByRole('button', { name: 'Add widget' }))
  const add = await screen.findByRole('dialog', { name: 'Add widget' })
  await ui.click(within(add).getByRole('button', { name: 'Distance driven Key figure' }))
  expect(within(add).getByRole('tab', { name: 'Quick', selected: true })).toBeInTheDocument()
  expect(within(add).getByRole('radio', { name: 'This year' })).toBeChecked()
  await ui.click(within(add).getByRole('radio', { name: '12 full months' }))
  expect(within(add).getByRole('radio', { name: '12 full months' })).toBeChecked()
  await ui.click(within(add).getByRole('button', { name: 'Add to the dashboard' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  await ui.click(screen.getByRole('button', { name: 'Save dashboard' }))

  await waitFor(() => expect(state.calls.SaveDashboardLayout).toHaveLength(1))
  const tiles = saved(state).tiles
  expect(periodOf(tiles, 'spendingTrend')).toEqual({ kind: 'LAST', unit: 'MONTHS', count: 6, since: null, whole: true, against: 'BEFORE' })
  expect(periodOf(tiles, 'distanceTrend')).toEqual({ kind: 'LAST', unit: 'MONTHS', count: 12, since: null, whole: true, against: 'YEAR_EARLIER' }) // a quick pick sets the window, the comparison stays distance's own
})

it('opens on a phone, and the popover has no accessibility violations', { timeout: 30_000 }, async () => {
  const { ui } = setup({ viewport: 'phone' })
  await customize(ui)
  const dialog = await openPeriod(ui)
  await ui.click(within(dialog).getByRole('tab', { name: 'Last' }))
  expect(await within(dialog).findByText(/more than the 8 weeks before/)).toBeInTheDocument()

  const results = await axe(dialog, { rules: { 'color-contrast': { enabled: false } } }) // the real styles' contrast: axe.e2e.ts
  expect(results.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`)).toEqual([])
})
