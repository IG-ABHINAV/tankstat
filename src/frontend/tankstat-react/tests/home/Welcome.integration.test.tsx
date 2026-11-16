import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse } from 'msw'
import { graphql } from '../support/graphql.ts'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../src/App.tsx'
import { server } from '../support/server.ts'
import { fakeTyres, tyreSummer, tyreWinter } from '../support/tyres.ts'
import {
  adminSession,
  fakeExpenseBackend,
  fakeLogBackend,
  fakeRecurring,
  fakeRecurringBackend,
  fakeSummary,
  fakeVehicle,
  fakeVehicleBackend,
  gqlError,
  healthHandler,
  person,
  renderWithApollo,
  sessionHandler,
  silenceConsoleError,
  stubViewport,
} from '../support/mocks.tsx'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

/** The home page with the backends its quick actions talk to, for the first vehicle (the logs backend answers LogDefaults first). */
function setup(vehicles = [fakeVehicle()], device: 'desktop' | 'phone' = 'desktop', admin = false) {
  stubViewport(device)
  const backend = fakeVehicleBackend(vehicles)
  const first = backend.state.vehicles[0] ?? fakeVehicle() // the backend's own object: what the other fakes change on it is what the card is served
  const logs = fakeLogBackend(first, [])
  const expenses = fakeExpenseBackend(first, [])
  const recurring = fakeRecurringBackend(first.recurring, first)
  server.use(admin ? adminSession() : sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...logs.handlers, ...expenses.handlers, ...recurring.handlers)
  renderWithApollo(<App />, '/')
  return { ...backend, logs, expenses, recurring, ui: userEvent.setup() }
}

const tyres = () => fakeRecurring({ id: 'rc2', title: 'Tyres', kind: 'ODOMETER', intervalMonths: null, status: { state: 'OVERDUE', limit: 'ODOMETER', dueDate: null, dueOdometer: 60000, daysLeft: null, distanceLeft: -300 } })

const card = async (name: string) => (await screen.findByRole('link', { name: `Open ${name}` })).closest('li')!

it('shows the outlines of the cards while the first ones load, and says so to a screen reader', async () => {
  let answer!: () => void
  const held = new Promise<void>((resolve) => (answer = resolve))
  setup()
  server.use(graphql.query('Welcome', async () => void (await held))) // ahead of the vehicle backend, which answers once it is let go
  await screen.findByRole('heading', { name: 'Your vehicles' })

  expect(screen.getByText('Loading…')).toHaveAttribute('role', 'status')
  expect(document.querySelectorAll('.MuiSkeleton-root')).toHaveLength(4)
  answer()

  await card('Octavia')
  expect(screen.queryByText('Loading…')).not.toBeInTheDocument()
  expect(document.querySelector('.MuiSkeleton-root')).toBeNull()
})

it('shows a card per vehicle with the key figures', async () => {
  setup([fakeVehicle({ summary: fakeSummary({ latestOdometer: 123456, averageConsumption: 6.25, lastFillUpDate: '2026-09-17', thisMonthSpend: 52000 }) })])

  const c = within(await card('Octavia'))

  expect(c.getByText('ABC-123')).toBeInTheDocument()
  expect(c.getByText('Diesel')).toBeInTheDocument()
  expect(c.getByText(/123,456 km/)).toBeInTheDocument()
  expect(c.getByText('6.25 L/100 km')).toBeInTheDocument()
  expect(c.getByText('Sep 17, 2026')).toBeInTheDocument()
  expect(c.getByText(/52,000/)).toBeInTheDocument()
  expect(await c.findByRole('img', { name: /Spending in the last six months/ })).toBeInTheDocument() // the chart library loads on demand
})

it("a card shows this month's spending in every currency, the main one first", async () => {
  setup([fakeVehicle({ summary: fakeSummary({ thisMonthSpend: 52000, spending: [{ currency: 'HUF', thisMonth: 52000, lastMonth: 0 }, { currency: 'EUR', thisMonth: 73.9, lastMonth: 0 }] }) })])

  const c = within(await card('Octavia'))

  expect(c.getByText(/52,000.* · €73\.90/)).toBeInTheDocument()
})

it('uses the uploaded picture as the card background, and a gradient without one', async () => {
  setup([fakeVehicle({ id: 'a', name: 'With picture', pictureUrl: '/media/abc' }), fakeVehicle({ id: 'b', name: 'Plain' })])

  const withPicture = (await card('With picture')).querySelector('.vehicle-card')!
  const plain = (await card('Plain')).querySelector('.vehicle-card')!

  expect((withPicture.querySelector('.cover-picture') as HTMLElement).style.backgroundImage).toBe('url("/media/abc")') // a CSS background: cropped to fill, never stretched
  expect(withPicture.querySelector('img')).toBeNull()
  expect(withPicture.querySelector('.scrim')).toBeInTheDocument() // keeps the white text readable
  expect(plain.querySelector('.cover-picture')).toBeNull()
  expect((plain.querySelector('.cover') as HTMLElement).style.background).toContain('linear-gradient')
})

it('says what is missing for a new vehicle instead of showing empty numbers', async () => {
  setup([fakeVehicle({ summary: fakeSummary({ lastFillUpDate: null, latestOdometer: null, averageConsumption: null, currency: null, thisMonthSpend: 0, fillUpCount: 0, expenseCount: 0 }) })])

  const c = within(await card('Octavia'))

  expect(c.getByText('No fill-ups yet')).toBeInTheDocument()
  expect(c.getAllByText('No data yet').length).toBeGreaterThanOrEqual(2)
  expect(c.queryByRole('img', { name: /Spending/ })).not.toBeInTheDocument()
})

it('shows miles per gallon for vehicles in miles and gallons', async () => {
  setup([fakeVehicle({ units: { distance: 'MILES', volume: 'US_GALLONS' }, summary: fakeSummary({ averageConsumption: 8 }) })])

  expect(within(await card('Octavia')).getByText('12.5 mpg (US)')).toBeInTheDocument()
})

it('marks shared vehicles with the level of access and the owner', async () => {
  setup([fakeVehicle({ canEdit: false, logAccess: 'EDIT', owner: person('Bob') })])

  const c = within(await card('Octavia'))

  expect(c.getByText('Logs: view and edit')).toBeInTheDocument()
  expect(c.getByText('Bob')).toBeInTheDocument()
})

it('offers the next steps when there are no vehicles', async () => {
  setup([])

  await screen.findByText(/You have no vehicles yet/)

  expect(screen.getAllByRole('link', { name: 'Import logs' }).length).toBeGreaterThan(0)
  expect(screen.getByRole('button', { name: 'Add vehicle' })).toBeInTheDocument()
})

it('adds a vehicle from the home page, where everyone manages their own cars', async () => {
  const { ui, state } = setup([])
  await screen.findByText(/You have no vehicles yet/)

  await ui.click(screen.getByRole('button', { name: 'Add vehicle' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add vehicle' })
  await ui.type(within(dialog).getByLabelText('Name'), 'Golf')
  await ui.click(within(dialog).getByRole('button', { name: 'Add vehicle' }))

  expect(await screen.findByRole('link', { name: 'Open Golf' })).toBeInTheDocument()
  expect(state.calls.AddVehicle).toHaveLength(1)
})

it('has a link to the import, and the full vehicle list only for administrators', async () => {
  setup()
  await screen.findByRole('heading', { name: 'Your vehicles' })

  expect(screen.getByRole('link', { name: 'Import logs' })).toHaveAttribute('href', '/import')
  expect(screen.queryByRole('link', { name: 'All vehicles' })).not.toBeInTheDocument()
})

it('shows administrators a link to all vehicles', async () => {
  setup([fakeVehicle()], 'desktop', true)

  expect(await screen.findByRole('link', { name: 'All vehicles' })).toHaveAttribute('href', '/admin/vehicles')
})

const vehicleCard = async (name: string) => (await card(name)).querySelector('.vehicle-card') as HTMLElement

it('keeps the figures in the page for screen readers, hidden only visually until the card is hovered, focused or tapped', async () => {
  setup()

  const c = await vehicleCard('Octavia')

  expect(c.style.getPropertyValue('--panel-h')).toMatch(/px$/) // the panel's height: it sits just below the card edge until revealed (a transform, never display: none)
  expect(c.style.getPropertyValue('--top-h')).toMatch(/px$/) // the top block's: together they are the card's least height (jsdom measures 0, the browser the real thing)
  expect(c).not.toHaveAttribute('data-open')
  expect(within(c).getByText('Odometer')).toBeInTheDocument()
})

it('on a touch screen the first tap shows the figures and the second tap opens the vehicle', async () => {
  const ui = userEvent.setup()
  setup([fakeVehicle()], 'phone')
  const c = await vehicleCard('Octavia')

  await ui.click(within(c).getByText('Odometer'))
  expect(c).toHaveAttribute('data-open')
  expect(within(c).getByText('Tap again to open')).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: 'Your vehicles' })).toBeInTheDocument() // the first tap did not navigate

  await ui.click(within(c).getByText('Odometer'))

  expect(await screen.findByRole('link', { name: 'Back to home' })).toBeInTheDocument() // the vehicle page
})

it('a tap on the name does the same: reveal first, open on the second tap', async () => {
  const ui = userEvent.setup()
  setup([fakeVehicle()], 'phone')
  const c = await vehicleCard('Octavia')

  await ui.click(within(c).getByRole('link', { name: 'Open Octavia' }))
  expect(c).toHaveAttribute('data-open')
  expect(screen.getByRole('heading', { name: 'Your vehicles' })).toBeInTheDocument()

  await ui.click(within(c).getByRole('link', { name: 'Open Octavia' }))
  expect(await screen.findByRole('link', { name: 'Back to home' })).toBeInTheDocument()
})

it('a tap anywhere else puts the figures away again', async () => {
  const ui = userEvent.setup()
  setup([fakeVehicle()], 'phone')
  const c = await vehicleCard('Octavia')
  await ui.click(within(c).getByText('Odometer'))
  expect(c).toHaveAttribute('data-open')

  await ui.click(screen.getByRole('heading', { name: 'Your vehicles' }))

  expect(c).not.toHaveAttribute('data-open')
})

it('the keyboard and screen readers open the vehicle straight away, even on a touch device', async () => {
  const ui = userEvent.setup()
  setup([fakeVehicle()], 'phone')
  const c = await vehicleCard('Octavia')

  within(c).getByRole('link', { name: 'Open Octavia' }).focus()
  await ui.keyboard('{Enter}')

  expect(await screen.findByRole('link', { name: 'Back to home' })).toBeInTheDocument()
})

it('with a mouse a click anywhere on the card opens the vehicle: the figures, the chart, the picture area', async () => {
  const ui = userEvent.setup()
  setup([fakeVehicle()], 'desktop')
  const c = await vehicleCard('Octavia')

  await ui.click(await within(c).findByRole('img', { name: /Spending in the last six months/ })) // the chart

  expect(await screen.findByRole('link', { name: 'Back to home' })).toBeInTheDocument()
  expect(c).not.toHaveAttribute('data-open') // and nothing was toggled on the way
})

it('a click on the figures opens the vehicle too', async () => {
  const ui = userEvent.setup()
  setup([fakeVehicle()], 'desktop')
  const c = await vehicleCard('Octavia')

  await ui.click(within(c).getByText('Odometer'))

  expect(await screen.findByRole('link', { name: 'Back to home' })).toBeInTheDocument()
})

const fleet = (n: number) =>
  Array.from({ length: n }, (_, i) =>
    fakeVehicle({ id: `v${i + 1}`, name: `Car ${String(i + 1).padStart(2, '0')}`, licensePlate: `PL-${String(i + 1).padStart(3, '0')}`, summary: fakeSummary({ fillUpCount: 0, expenseCount: 0 }) }),
  )

const cardCount = () => screen.getAllByRole('link', { name: /^Open Car/ }).length

it('loads the cards a page at a time, says how many of them are shown, and shows more on request', async () => {
  const { ui, state } = setup(fleet(30))

  await screen.findByRole('link', { name: 'Open Car 01' })
  expect(cardCount()).toBe(24)
  expect(screen.getByText('Showing 24 of 30 vehicles')).toBeInTheDocument()
  expect(screen.getByRole('list', { name: '30 vehicles' })).toBeInTheDocument()

  await ui.click(screen.getByRole('button', { name: 'Show more vehicles' }))

  await screen.findByRole('link', { name: 'Open Car 30' })
  expect(cardCount()).toBe(30)
  expect(screen.getByText('Showing 30 of 30 vehicles')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Show more vehicles' })).not.toBeInTheDocument()
  expect(state.requests.Welcome).toEqual([
    { search: null, skip: 0, take: 24 },
    { search: null, skip: 24, take: 24 },
  ])
})

it('loads the next page by itself when the end of the list scrolls into view', async () => {
  let reveal: () => void = () => {}
  vi.stubGlobal(
    'IntersectionObserver',
    class {
      constructor(callback: (entries: { isIntersecting: boolean }[]) => void) {
        reveal = () => callback([{ isIntersecting: true }])
      }
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  )
  setup(fleet(30))
  await screen.findByRole('link', { name: 'Open Car 01' })
  expect(cardCount()).toBe(24)

  reveal()

  await screen.findByRole('link', { name: 'Open Car 30' })
  expect(cardCount()).toBe(30)
})

it('asks for the next page once, even when the end of the list comes into view again while it is on its way', async () => {
  let reveal: () => void = () => {}
  vi.stubGlobal(
    'IntersectionObserver',
    class {
      constructor(callback: (entries: { isIntersecting: boolean }[]) => void) {
        reveal = () => callback([{ isIntersecting: true }])
      }
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  )
  const { state } = setup(fleet(30))
  await screen.findByRole('link', { name: 'Open Car 01' })
  let answer!: () => void
  const held = new Promise<void>((resolve) => (answer = resolve))
  const first = graphql.query('Welcome', async ({ variables }) => {
    if (Number(variables.skip) === 0) return undefined // the first page as always
    state.requests.Welcome.push(variables)
    await held
    const page = state.vehicles.slice(24, 48).map((v) => ({ ...v, recurring: [] }))
    return HttpResponse.json({ data: { myVehicles: page, myVehicleCount: 30, vehicleTotal: 30 } })
  })
  server.use(first)

  reveal()
  await waitFor(() => expect(state.requests.Welcome.filter((r) => r.skip === 24)).toHaveLength(1))
  await waitFor(() => expect(screen.getByRole('button', { name: /Show more vehicles/ })).toBeDisabled()) // loading
  reveal() // scrolled into view again while the page is loading
  answer()

  await screen.findByRole('link', { name: 'Open Car 30' })
  expect(state.requests.Welcome.filter((r) => r.skip === 24)).toHaveLength(1)
})

it('says when the next page cannot be loaded, and Show more tries again', async () => {
  silenceConsoleError()
  const { ui, state } = setup(fleet(30))
  await screen.findByRole('link', { name: 'Open Car 01' })
  let refuse = true
  server.use(
    graphql.query('Welcome', ({ variables }) => {
      if (Number(variables.skip) === 0 || !refuse) return undefined
      state.requests.Welcome.push(variables)
      return HttpResponse.json(gqlError('Busy', 'INTERNAL_ERROR', 'server.busy'))
    }),
  )

  await ui.click(screen.getByRole('button', { name: 'Show more vehicles' }))

  expect(await screen.findByRole('alert')).toBeInTheDocument()
  expect(cardCount()).toBe(24)
  refuse = false
  await ui.click(screen.getByRole('button', { name: 'Show more vehicles' }))

  await screen.findByRole('link', { name: 'Open Car 30' })
  expect(screen.queryByRole('alert')).not.toBeInTheDocument()
})

it('searches by name on the server, and everything is back when the search is cleared', async () => {
  const { ui, state } = setup(fleet(30))
  await screen.findByRole('link', { name: 'Open Car 01' })

  await ui.type(screen.getByRole('searchbox', { name: 'Search vehicles' }), 'car 07')

  await waitFor(() => expect(cardCount()).toBe(1))
  expect(screen.getByRole('link', { name: 'Open Car 07' })).toBeInTheDocument()
  expect(screen.getByText('Showing 1 of 1 vehicles')).toBeInTheDocument()
  expect(state.requests.Welcome.at(-1)).toEqual({ search: 'car 07', skip: 0, take: 24 })
  expect(state.requests.Welcome.some((r) => r.search === 'c')).toBe(false) // not a request for every letter

  await ui.clear(screen.getByRole('searchbox', { name: 'Search vehicles' }))
  await waitFor(() => expect(cardCount()).toBe(24))
})

it('also finds a vehicle by its license plate', async () => {
  const { ui } = setup(fleet(30))
  await screen.findByRole('link', { name: 'Open Car 01' })

  await ui.type(screen.getByRole('searchbox', { name: 'Search vehicles' }), 'PL-012')

  await waitFor(() => expect(cardCount()).toBe(1))
  expect(screen.getByRole('link', { name: 'Open Car 12' })).toBeInTheDocument()
})

it('says when nothing matches the search, and keeps the search box to change it', async () => {
  const { ui } = setup()
  await screen.findByRole('link', { name: 'Open Octavia' })

  await ui.type(screen.getByRole('searchbox', { name: 'Search vehicles' }), 'zzz')

  expect(await screen.findByText('No vehicles match "zzz".')).toBeInTheDocument()
  expect(screen.queryByRole('link', { name: 'Open Octavia' })).not.toBeInTheDocument()
  expect(screen.getByRole('searchbox', { name: 'Search vehicles' })).toBeInTheDocument()
})

it('has no search box before the first vehicle exists', async () => {
  setup([])

  await screen.findByText(/You have no vehicles yet/)

  expect(screen.queryByRole('searchbox')).not.toBeInTheDocument()
})

// ---- quick actions on the cards -----------------------------------------------------------------------------

it('logs a refuelling from the card; the card updates in place and the home page is not asked again', async () => {
  const { ui, state, logs } = setup()
  const c = within(await card('Octavia'))

  await ui.click(c.getByRole('button', { name: 'Refuel Octavia' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  await ui.type(within(dialog).getByLabelText(/^Volume/), '38,2')
  await ui.type(within(dialog).getByLabelText('Total cost'), '19100')
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '12450')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(logs.state.calls.LogRefueling).toEqual([{ input: expect.objectContaining({ vehicleId: 'v1', volume: 38.2, totalCost: 19100, odometer: 12450 }) }])
  await c.findByText(/12,450 km/)
  expect(state.requests.VehicleCard).toEqual([{ id: 'v1' }])
  expect(state.requests.Welcome).toHaveLength(1)
})

it('adds an expense from the card; the spending updates in place', async () => {
  const { ui, state, expenses } = setup()
  const c = within(await card('Octavia'))

  await ui.click(c.getByRole('button', { name: 'Expense for Octavia' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  await ui.type(within(dialog).getByLabelText('Title'), 'Tyres')
  await ui.type(within(dialog).getByLabelText('Amount'), '120000')
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(expenses.state.calls.AddExpense).toEqual([{ input: expect.objectContaining({ vehicleId: 'v1', title: 'Tyres', amount: 120000 }) }])
  await c.findByText(/170,000/) // 50,000 + 120,000
  expect(state.requests.Welcome).toHaveLength(1)
})

it('adds an expense with tyre details from the card, and asks the tyre log again (#290)', async () => {
  const { ui, expenses } = setup()
  const tyres = fakeTyres([tyreSummer, tyreWinter])
  let asked = 0
  server.use(graphql.query('VehicleTyres', () => {
    asked++
    return HttpResponse.json({ data: { vehicleTyres: tyres.answer() } })
  }))
  const c = within(await card('Octavia'))

  await ui.click(c.getByRole('button', { name: 'Expense for Octavia' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  await ui.type(within(dialog).getByLabelText(/^Category/), 'Tyres')
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  await ui.click(within(group).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: 'Wheel balancing' }))
  await ui.type(within(dialog).getByLabelText('Amount'), '12000')
  const before = asked
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(expenses.state.calls.AddExpense).toEqual([{ input: expect.objectContaining({ title: 'Wheel balancing: Michelin Primacy 4', tyre: expect.objectContaining({ kind: 'WHEEL_BALANCING', setId: 's1' }) }) }])
  await waitFor(() => expect(asked).toBeGreaterThan(before)) // the device's copy of the tyre log has the line at once
})

it('marks a due schedule done from the card: it leaves the attention list, the logged expense shows, and the focus lands on the title', async () => {
  const { ui, state, recurring } = setup([fakeVehicle({ recurring: [tyres()] })])
  const c = within(await card('Octavia'))
  expect(c.getByRole('list', { name: 'Needs attention' })).toHaveTextContent('Tyres')

  await ui.click(c.getByRole('button', { name: 'Mark Tyres of Octavia as done' }))
  const dialog = await screen.findByRole('dialog', { name: 'Mark as done: Tyres' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '62000')
  await ui.type(within(dialog).getByLabelText('Amount (optional)'), '35000')
  await ui.click(within(dialog).getByRole('button', { name: 'Mark as done' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(recurring.state.calls.MarkRecurringExpensesDone).toEqual([{ input: expect.objectContaining({ ids: ['rc2'], odometer: 62000, amount: 35000 }) }])
  await waitFor(() => expect(c.queryByRole('list', { name: 'Needs attention' })).not.toBeInTheDocument())
  await c.findByText(/85,000/) // 50,000 + 35,000: the card was asked again
  expect(state.requests.VehicleCard).toEqual([{ id: 'v1' }])
  await waitFor(() => expect(document.activeElement).toBe(c.getByRole('link', { name: 'Open Octavia' }))) // the Done button is gone with its row
})

it('one visit from the card: the other due schedule of its category starts ticked, both leave the list, and the focus lands on the title', async () => {
  // Service, as tyres() (fakeRecurring's default): the same visit.
  const oil = fakeRecurring({ id: 'rc3', title: 'Oil change', category: 'Service', status: { state: 'DUE_SOON', limit: 'TIME', dueDate: '2026-10-20', dueOdometer: null, daysLeft: 19, distanceLeft: null } })
  const { ui, recurring } = setup([fakeVehicle({ recurring: [tyres(), oil] })])
  const c = within(await card('Octavia'))

  await ui.click(c.getByRole('button', { name: 'Mark Oil change of Octavia as done' }))
  const dialog = await screen.findByRole('dialog', { name: 'Mark as done: Oil change' })
  expect(within(dialog).getByRole('checkbox', { name: 'Tyres' })).toBeChecked()
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '62000')
  await ui.click(within(dialog).getByRole('button', { name: 'Mark as done' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(recurring.state.calls.MarkRecurringExpensesDone).toEqual([{ input: expect.objectContaining({ ids: ['rc2', 'rc3'], amount: null }) }])
  await waitFor(() => expect(c.queryByRole('list', { name: 'Needs attention' })).not.toBeInTheDocument())
  await waitFor(() => expect(document.activeElement).toBe(c.getByRole('link', { name: 'Open Octavia' })))
})

it('one visit from the card: a due schedule of another category starts unticked and stays in the list', async () => {
  const vignette = fakeRecurring({ id: 'rc3', title: 'Vignette', category: 'Fees', kind: 'TIME', status: { state: 'DUE_SOON', limit: 'TIME', dueDate: '2026-10-20', dueOdometer: null, daysLeft: 19, distanceLeft: null } })
  const { ui, recurring } = setup([fakeVehicle({ recurring: [tyres(), vignette] })])
  const c = within(await card('Octavia'))

  await ui.click(c.getByRole('button', { name: 'Mark Tyres of Octavia as done' }))
  const dialog = await screen.findByRole('dialog', { name: 'Mark as done: Tyres' })
  expect(within(dialog).getByRole('checkbox', { name: 'Vignette' })).not.toBeChecked()
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '62000')
  await ui.click(within(dialog).getByRole('button', { name: 'Mark as done' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(recurring.state.calls.MarkRecurringExpensesDone).toEqual([{ input: expect.objectContaining({ ids: ['rc2'] }) }])
  await waitFor(() => expect(c.getByRole('list', { name: 'Needs attention' })).not.toHaveTextContent('Tyres'))
  expect(c.getByRole('list', { name: 'Needs attention' })).toHaveTextContent('Vignette')
})

it('cancelling the Done dialog hands the focus back to its button', async () => {
  const { ui } = setup([fakeVehicle({ recurring: [tyres()] })])
  const c = within(await card('Octavia'))

  await ui.click(c.getByRole('button', { name: 'Mark Tyres of Octavia as done' }))
  const dialog = await screen.findByRole('dialog', { name: 'Mark as done: Tyres' })
  await ui.click(await within(dialog).findByRole('button', { name: 'Cancel' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  await waitFor(() => expect(document.activeElement).toBe(c.getByRole('button', { name: 'Mark Tyres of Octavia as done' })))
})

it('a vehicle whose logs may only be viewed has no quick actions; one that may be edited has them', async () => {
  setup([
    fakeVehicle({ id: 'a', name: 'Theirs', canEdit: false, logAccess: 'VIEW', ownerName: 'Bob', recurring: [tyres()] }),
    fakeVehicle({ id: 'b', name: 'Shared', canEdit: false, logAccess: 'EDIT', ownerName: 'Bob' }),
  ])
  const theirs = within(await card('Theirs'))
  const shared = within(await card('Shared'))

  expect(theirs.queryByRole('button')).not.toBeInTheDocument()
  expect(theirs.getByRole('list', { name: 'Needs attention' })).toHaveTextContent('Tyres')
  expect(shared.getByRole('button', { name: 'Refuel Shared' })).toBeInTheDocument()
  expect(shared.getByRole('button', { name: 'Expense for Shared' })).toBeInTheDocument()
})

it('on a touch screen a card button opens its dialog at once, and taps inside the dialog neither open the vehicle nor close it', async () => {
  const { ui } = setup([fakeVehicle()], 'phone')
  const c = await vehicleCard('Octavia')

  await ui.click(within(c).getByRole('button', { name: 'Refuel Octavia' }))

  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  expect(c).not.toHaveAttribute('data-open') // the tap went to the button, not to the figures
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  await ui.click(within(dialog).getByLabelText(/^Volume/)) // a tap on a field of the dialog (the date would open its own picker)
  const fullTank = within(dialog).getByRole('switch', { name: 'Full tank' })
  const was = (fullTank as HTMLInputElement).checked
  await ui.click(fullTank)
  expect((fullTank as HTMLInputElement).checked).toBe(!was) // the tap worked the control (the card did not swallow it as a reveal)
  expect(c).not.toHaveAttribute('data-open')
  // Still at home with the dialog open: opening the vehicle would have unmounted the card and its dialog.
  expect(screen.getByRole('dialog', { name: 'Add refuelling' })).toBeInTheDocument()

  await ui.click(within(dialog).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
})

it('on a desktop a click inside a dialog opened from the card does not open the vehicle', async () => {
  const { ui } = setup()
  const c = within(await card('Octavia'))

  await ui.click(c.getByRole('button', { name: 'Expense for Octavia' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  await ui.click(within(dialog).getByLabelText('Title'))

  expect(screen.getByRole('dialog', { name: 'Add expense' })).toBeInTheDocument() // opening the vehicle would have unmounted it
})

it('two vehicles with the same schedule have Done buttons that name the vehicle', async () => {
  setup([fakeVehicle({ id: 'a', name: 'Octavia', recurring: [tyres()] }), fakeVehicle({ id: 'b', name: 'Golf', recurring: [fakeRecurring({ ...tyres(), id: 'rc9' })] })])
  await card('Golf')

  expect(screen.getByRole('button', { name: 'Mark Tyres of Octavia as done' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Mark Tyres of Golf as done' })).toBeInTheDocument()
})

it('on a touch screen the first tap on Done opens its dialog', async () => {
  const { ui } = setup([fakeVehicle({ recurring: [tyres()] })], 'phone')
  const c = await vehicleCard('Octavia')

  await ui.click(within(c).getByRole('button', { name: 'Mark Tyres of Octavia as done' }))

  expect(await screen.findByRole('dialog', { name: 'Mark as done: Tyres' })).toBeInTheDocument()
  expect(c).not.toHaveAttribute('data-open')
})

it('Enter on a card button opens its dialog and does not open the vehicle', async () => {
  const { ui } = setup()
  const c = await vehicleCard('Octavia')

  within(c).getByRole('button', { name: 'Refuel Octavia' }).focus()
  await ui.keyboard('{Enter}')

  expect(await screen.findByRole('dialog', { name: 'Add refuelling' })).toBeInTheDocument()
  expect(c).toBeInTheDocument() // opening the vehicle would have unmounted the card
})

it('an action on a card keeps every page the home page has loaded', async () => {
  const { ui, state, logs } = setup(fleet(30))
  await screen.findByRole('link', { name: 'Open Car 01' })
  await ui.click(screen.getByRole('button', { name: 'Show more vehicles' }))
  await screen.findByRole('link', { name: 'Open Car 30' })
  expect(cardCount()).toBe(30)

  await ui.click(within(await card('Car 01')).getByRole('button', { name: 'Refuel Car 01' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  await ui.type(within(dialog).getByLabelText(/^Volume/), '30')
  await ui.type(within(dialog).getByLabelText('Total cost'), '15000')
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '12450')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(logs.state.calls.LogRefueling).toHaveLength(1)
  await within(await card('Car 01')).findByText(/12,450 km/)
  expect(cardCount()).toBe(30)
  expect(state.requests.Welcome).toHaveLength(2) // the two pages, nothing more
  expect(state.requests.VehicleCard).toEqual([{ id: 'v1' }])
})
