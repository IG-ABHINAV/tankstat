import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../src/App.tsx'
import { outbox } from '../../src/offline/outbox.ts'
import { server } from '../support/server.ts'
import { fakeExpenseBackend, fakeLogBackend, fakeRefueling, fakeVehicle, fakeVehicleBackend, adminSession, healthHandler, person, renderWithApollo, sessionHandler, stubViewport } from '../support/mocks.tsx'
import { dateValue, findDateField } from '../support/dates.ts'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

const logs = [
  fakeRefueling({ id: 'r1', date: '2026-08-01', volume: 40, totalCost: 20000, odometer: 11000 }),
  fakeRefueling({ id: 'r2', date: '2026-09-01', volume: 41.5, totalCost: 22000, odometer: 12000, isFullTank: false, note: 'Holiday' }),
]

function setup(vehicle = fakeVehicle(), initial = logs, route = '/vehicles/v1?tab=refuelings', admin = false) {
  stubViewport('desktop')
  const backend = fakeLogBackend(vehicle, initial)
  server.use(admin ? adminSession() : sessionHandler('NONE', () => null), healthHandler, ...backend.handlers)
  renderWithApollo(<App />, route)
  return { ...backend, ui: userEvent.setup() }
}

const choose = async (ui: ReturnType<typeof userEvent.setup>, control: string, option: string) => {
  await ui.click(screen.getByRole('combobox', { name: control }))
  await ui.click(await screen.findByRole('option', { name: option }))
}

it('shows the vehicle with its logs formatted in its own units', async () => {
  setup(fakeVehicle({ units: { distance: 'MILES', volume: 'US_GALLONS' } }))

  // The vehicle page and its data grid load for the first time in this file, which can take longer than the usual wait.
  const row = (await screen.findByText(/Sep 1, 2026/, {}, { timeout: 10_000 })).closest<HTMLElement>('[role="row"]')!
  expect(within(row).getByText('41.5 gal')).toBeInTheDocument()
  expect(within(row).getByText(/12,000 mi/)).toBeInTheDocument()
  expect(within(row).getByText('Partial')).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: 'Octavia' })).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Back to home' })).toHaveAttribute('href', '/') // the vehicle list is for administrators
})

it('administrators go back to the full vehicle list', async () => {
  setup(fakeVehicle(), logs, '/vehicles/v1?tab=refuelings', true)

  expect(await screen.findByRole('link', { name: 'Back to vehicles' })).toHaveAttribute('href', '/admin/vehicles')
})

it('shows the price per unit and the cost in the currency it was paid in', async () => {
  setup()

  const row = (await screen.findByText(/Sep 1, 2026/)).closest<HTMLElement>('[role="row"]')!
  expect(within(row).getByText(/22,000/)).toBeInTheDocument()
  expect(within(row).getByText(/530/)).toBeInTheDocument() // 22000 / 41.5 per liter
})

it('shows the stored consumption per 100 units, and a dash where there is none', async () => {
  setup(fakeVehicle(), [
    fakeRefueling({ id: 'r1', date: '2026-08-01', odometer: 11000, consumption: null }),
    fakeRefueling({ id: 'r2', date: '2026-09-01', odometer: 12000, consumption: 6.667 }),
  ])

  const row = (await screen.findByText(/Sep 1, 2026/)).closest<HTMLElement>('[role="row"]')!
  expect(within(row).getByText('6.67 L/100 km')).toBeInTheDocument()
  expect(within((await screen.findByText(/Aug 1, 2026/)).closest<HTMLElement>('[role="row"]')!).getAllByText('–').length).toBeGreaterThan(0)
})

it('shows miles per gallon where the vehicle uses miles and gallons', async () => {
  setup(fakeVehicle({ units: { distance: 'MILES', volume: 'US_GALLONS' } }), [fakeRefueling({ id: 'r2', date: '2026-09-01', consumption: 8 })])

  expect(await screen.findByText('12.5 mpg (US)')).toBeInTheDocument()
})

it('asks the server for the consumption and sorts by it on the server', async () => {
  const { ui, state } = setup()
  await screen.findByText(/Sep 1, 2026/)
  expect(state.requests.at(-1)).toMatchObject({ withConsumption: true })

  await ui.click(screen.getByRole('columnheader', { name: /^Consumption/ }))

  await waitFor(() => expect(state.requests.at(-1)).toMatchObject({ orderBy: 'CONSUMPTION', direction: 'ASC' }))
})

it('sorts the logs on the server, newest first by default', async () => {
  const { ui, state } = setup()
  await screen.findByText(/Sep 1, 2026/)
  expect(screen.getAllByRole('rowheader').map((c) => c.textContent)).toEqual(['Sep 1, 2026', 'Aug 1, 2026'])
  expect(state.requests.at(-1)).toMatchObject({ vehicleId: 'v1', orderBy: 'DATE', direction: 'DESC', skip: 0 })

  await ui.click(screen.getByRole('columnheader', { name: /^Odometer/ }))

  await waitFor(() => expect(state.requests.at(-1)).toMatchObject({ orderBy: 'ODOMETER', direction: 'ASC' }))
})

it('adds a refuelling: starts from today, the last reading as a hint and the usual currency', async () => {
  const { ui, state } = setup()
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Add refuelling' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await within(dialog).findByText(/Last reading: 12,000 km on Sep 1, 2026/)
  expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF')
  expect(dateValue(await findDateField('Date', dialog))).toBe(new Date().toLocaleDateString('sv-SE'))
  await ui.type(within(dialog).getByLabelText(/^Volume/), '38,2') // a comma is fine
  await ui.type(within(dialog).getByLabelText('Total cost'), '19100')
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '12450')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(state.calls.LogRefueling).toEqual([
    { input: expect.objectContaining({ vehicleId: 'v1', volume: 38.2, totalCost: 19100, currency: 'HUF', odometer: 12450, isFullTank: true, note: null }) },
  ])
  await screen.findByText(/12,450 km/)
})

it('blocks missing and malformed numbers in the form without calling the server', async () => {
  const { ui, state } = setup()
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Add refuelling' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await ui.type(within(dialog).getByLabelText(/^Volume/), 'lots')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  expect(await within(dialog).findByText('Enter a number, for example 12.5.')).toBeInTheDocument()
  expect(within(dialog).getByText('Total cost is required')).toBeInTheDocument()
  expect(state.calls.LogRefueling).toBeUndefined()
})

it('shows the odometer rule from the server in the form and keeps the dialog open', async () => {
  const { ui, state } = setup()
  state.failWith = { message: 'too low', key: 'odometer.belowPrevious', args: { previous: '12,000 km', date: '2026-09-01' } }
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Add refuelling' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await ui.type(within(dialog).getByLabelText(/^Volume/), '30')
  await ui.type(within(dialog).getByLabelText('Total cost'), '15000')
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '100')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  expect(await within(dialog).findByRole('alert')).toHaveTextContent('The odometer cannot be lower than 12,000 km, the reading on 2026-09-01.')
  expect(screen.getByRole('dialog')).toBeInTheDocument()
})

it('edits a refuelling starting from its real values', async () => {
  const { ui, state } = setup()
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Edit the refuelling of Sep 1, 2026' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit refuelling' })
  await waitFor(() => expect(within(dialog).getByLabelText('Total cost')).toHaveValue('22000'))
  expect(within(dialog).getByLabelText('Note (optional)')).toHaveValue('Holiday')
  expect(within(dialog).getByRole('switch', { name: /Full tank/ })).not.toBeChecked()
  await ui.clear(within(dialog).getByLabelText('Total cost'))
  await ui.type(within(dialog).getByLabelText('Total cost'), '22500')
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))

  await waitFor(() => expect(state.calls.UpdateRefueling).toEqual([{ input: expect.objectContaining({ id: 'r2', totalCost: 22500, volume: 41.5, odometer: 12000, isFullTank: false, note: 'Holiday' }) }]))
})

it('works out the third of volume, unit price and total from the other two, and saves volume and total', async () => {
  const { ui, state } = setup()
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Add refuelling' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await ui.type(within(dialog).getByLabelText(/^Volume/), '38,2')
  await ui.type(within(dialog).getByLabelText('Price per L'), '599,9')

  expect(within(dialog).getByLabelText('Total cost')).toHaveValue('22916,18')
  expect(within(dialog).getByLabelText('Total cost')).toHaveAccessibleDescription(/Worked out from the other amounts\./)
  await ui.clear(within(dialog).getByLabelText('Total cost'))
  await ui.type(within(dialog).getByLabelText('Total cost'), '24000') // all three filled: the unit price gives way, the litres never (#204)
  expect(within(dialog).getByLabelText(/^Volume/)).toHaveValue('38,2')
  expect(within(dialog).getByLabelText('Price per L')).toHaveValue('628,272')
  expect(within(dialog).getByLabelText('Price per L')).toHaveAccessibleDescription(/Worked out from the other amounts\./)
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '12450')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  await waitFor(() => expect(state.calls.LogRefueling).toEqual([{ input: expect.objectContaining({ volume: 38.2, totalCost: 24000 }) }]))
  expect(state.calls.LogRefueling).toEqual([{ input: expect.not.objectContaining({ unitPrice: expect.anything() }) }]) // a help for typing, never saved
})

it('takes a discount off the total and saves it with the refuelling', async () => {
  const { ui, state } = setup()
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Add refuelling' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await ui.type(within(dialog).getByLabelText(/^Volume/), '43')
  await ui.type(within(dialog).getByLabelText('Price per L'), '818')
  expect(within(dialog).getByLabelText('Total cost')).toHaveValue('35174') // the pump's total
  await ui.type(within(dialog).getByLabelText('Discount'), '430')
  expect(within(dialog).getByLabelText('Total cost')).toHaveValue('34744') // what the receipt says was paid (#204)
  expect(within(dialog).getByLabelText('Price per L')).toHaveValue('818') // the pump's price stays
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '12450')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  await waitFor(() => expect(state.calls.LogRefueling).toEqual([{ input: expect.objectContaining({ volume: 43, totalCost: 34744, discount: 430 }) }]))
})

it('shows a saved discount with the pump price in the edit dialog, and clears it when the field is emptied', async () => {
  const { ui, state } = setup(fakeVehicle(), [fakeRefueling({ id: 'r3', date: '2026-09-15', volume: 43, totalCost: 34744, discount: 430, odometer: 12500 })])
  await screen.findByText(/Sep 15, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Edit the refuelling of Sep 15, 2026' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit refuelling' })
  await waitFor(() => expect(within(dialog).getByLabelText('Discount')).toHaveValue('430'))
  expect(within(dialog).getByLabelText('Price per L')).toHaveValue('818') // (34744 + 430) / 43: the pump's price
  expect(within(dialog).getByLabelText('Total cost')).toHaveValue('34744')
  await ui.clear(within(dialog).getByLabelText('Discount'))
  expect(within(dialog).getByLabelText('Price per L')).toHaveValue('808') // no discount: the price paid per litre
  expect(within(dialog).getByLabelText('Total cost')).toHaveValue('34744') // what was paid never moves
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))

  await waitFor(() => expect(state.calls.UpdateRefueling).toEqual([{ input: expect.objectContaining({ id: 'r3', totalCost: 34744, discount: 0 }) }])) // 0 clears; left out would keep it
})

it('shows the unit price of a saved log and keeps the amounts in step when it changes', async () => {
  const { ui, state } = setup()
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Edit the refuelling of Sep 1, 2026' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit refuelling' })
  await waitFor(() => expect(within(dialog).getByLabelText('Price per L')).toHaveValue('530.12'))
  await ui.clear(within(dialog).getByLabelText('Price per L'))
  await ui.type(within(dialog).getByLabelText('Price per L'), '500')

  expect(within(dialog).getByLabelText('Total cost')).toHaveValue('20750') // nobody typed the volume or the total: the total gives way
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))

  await waitFor(() => expect(state.calls.UpdateRefueling).toEqual([{ input: expect.objectContaining({ id: 'r2', volume: 41.5, totalCost: 20750 }) }]))
})

it('moves a refuelling to the trash after a confirmation, not before', async () => {
  const { ui, state } = setup()
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Delete the refuelling of Sep 1, 2026' }))
  expect(state.calls.DeleteRefueling).toBeUndefined()
  await ui.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Move to trash' }))

  await waitFor(() => expect(screen.queryByRole('rowheader', { name: /Sep 1, 2026/ })).not.toBeInTheDocument())
  expect(state.calls.DeleteRefueling).toEqual([{ id: 'r2' }])

  // A message says so, and Undo brings the row back.
  expect(screen.getByText('The refuelling of Sep 1, 2026 is in the trash.')).toBeInTheDocument()
  await ui.click(screen.getByRole('button', { name: 'Undo' }))
  expect(await screen.findByRole('rowheader', { name: /Sep 1, 2026/ })).toBeInTheDocument()
  expect(state.calls.RestoreRefueling).toEqual([{ id: 'r2' }])
})

it('logs only view access: no add button, no edit or delete', async () => {
  setup(fakeVehicle({ canEdit: false, logAccess: 'VIEW' }), logs.map((l) => ({ ...l, canEdit: false })))

  await screen.findByText(/Sep 1, 2026/)

  expect(screen.queryByRole('button', { name: 'Add refuelling' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /^Edit the refuelling/ })).not.toBeInTheDocument()
  expect(screen.getAllByText('view only')).toHaveLength(2)
})

it('a log grantee works with the logs but cannot reach the vehicle settings or sharing', async () => {
  setup(fakeVehicle({ canEdit: false, logAccess: 'EDIT' }))

  await screen.findByText(/Sep 1, 2026/)

  expect(screen.getByRole('button', { name: 'Add refuelling' })).toBeInTheDocument()
  expect(screen.queryByRole('tab', { name: /Sharing/ })).not.toBeInTheDocument()
  expect(screen.getByText(/can work with this vehicle's logs, but not change the vehicle itself/)).toBeInTheDocument()
})

/** The vehicle page on a phone, with the refuelling and expense backends of the floating add button. */
function setupPhone(vehicle = fakeVehicle(), route = '/vehicles/v1?tab=details') {
  stubViewport('phone')
  const backend = fakeLogBackend(vehicle, logs)
  const expenses = fakeExpenseBackend(vehicle, [])
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...expenses.handlers)
  renderWithApollo(<App />, route)
  return { ...backend, expenses, ui: userEvent.setup() }
}

it('on a phone, the floating add button logs a refuelling from any tab and gives the focus back', async () => {
  const { ui, state } = setupPhone()
  await screen.findByRole('heading', { name: 'Octavia' })
  const fab = screen.getByRole('button', { name: 'Add a refuelling or an expense' })

  await ui.click(fab)
  const menu = await screen.findByRole('menu', { name: 'Add a refuelling or an expense' })
  expect(within(menu).getAllByRole('menuitem').map((i) => i.textContent)).toEqual(['Add refuelling', 'Add expense'])
  await ui.click(within(menu).getByRole('menuitem', { name: 'Add refuelling' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await ui.type(within(dialog).getByLabelText(/^Volume/), '38.2')
  await ui.type(within(dialog).getByLabelText('Total cost'), '19100')
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '12450')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(state.calls.LogRefueling).toEqual([{ input: expect.objectContaining({ vehicleId: 'v1', volume: 38.2, totalCost: 19100, odometer: 12450 }) }])
  await waitFor(() => expect(fab).toHaveFocus())
})

it('on a phone, the floating add button logs an expense too', async () => {
  const { ui, expenses } = setupPhone()
  await screen.findByRole('heading', { name: 'Octavia' })

  await ui.click(screen.getByRole('button', { name: 'Add a refuelling or an expense' }))
  await ui.click(await screen.findByRole('menuitem', { name: 'Add expense' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await ui.type(within(dialog).getByLabelText('Title'), 'Car wash')
  await ui.type(within(dialog).getByLabelText('Amount'), '3200')
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  await waitFor(() => expect(expenses.state.calls.AddExpense).toEqual([{ input: expect.objectContaining({ vehicleId: 'v1', title: 'Car wash', amount: 3200 }) }]))
})

it('there is no floating add button on a desktop, nor for someone who may only view the logs', async () => {
  setup()
  await screen.findByText(/Sep 1, 2026/)
  expect(screen.queryByRole('button', { name: 'Add a refuelling or an expense' })).not.toBeInTheDocument()
})

it('the add button is only hidden, not unmounted, off a phone: a dialog opened from it survives a turn of the phone', async () => {
  setup()
  await screen.findByText(/Sep 1, 2026/)

  expect(document.querySelector('button[aria-label="Add a refuelling or an expense"]')).not.toBeVisible() // there, out of sight
})

it('nor on a phone for someone who may only view the logs', async () => {
  setupPhone(fakeVehicle({ canEdit: false, logAccess: 'VIEW' }))
  await screen.findByRole('heading', { name: 'Octavia' })
  expect(screen.queryByRole('button', { name: 'Add a refuelling or an expense' })).not.toBeInTheDocument()
})

it('every tab has its panel, but only the open one is filled', async () => {
  const { ui } = setup(fakeVehicle(), logs, '/vehicles/v1?tab=details')
  const details = await screen.findByRole('tab', { name: /Details/, selected: true })

  const panels = screen.getAllByRole('tabpanel', { hidden: true })
  expect(panels).toHaveLength(7)
  for (const tab of screen.getAllByRole('tab')) expect(document.getElementById(tab.getAttribute('aria-controls')!)).toHaveAttribute('aria-labelledby', tab.id)
  expect(panels.filter((p) => !p.hidden)).toEqual([document.getElementById(details.getAttribute('aria-controls')!)])
  expect(panels.filter((p) => p.hidden).every((p) => p.childElementCount === 0)).toBe(true)

  details.focus()
  await ui.keyboard('{ArrowLeft}') // the arrow keys choose, as before
  expect(await screen.findByRole('tab', { name: /Tyres/, selected: true })).toBeInTheDocument()
})

it('explains an unknown or inaccessible vehicle', async () => {
  setup(fakeVehicle(), logs, '/vehicles/nope')

  await screen.findByText(/does not exist, or you do not have access/)
})

it('opens the tab named in the address and keeps the choice in it', async () => {
  const { ui } = setup(fakeVehicle(), logs, '/vehicles/v1?tab=details')

  expect(await screen.findByRole('tab', { name: /Details/, selected: true })).toBeInTheDocument()
  await ui.click(screen.getByRole('tab', { name: /Refuelings/ }))
  expect(await screen.findByRole('button', { name: 'Add refuelling' })).toBeInTheDocument()
})

it('lists, adds, changes and revokes who can work with the logs', async () => {
  const { ui, state } = setup(fakeVehicle(), logs, '/vehicles/v1?tab=sharing')
  await screen.findByText('Nobody has been given access to the logs yet.')

  await choose(ui, 'Person', 'Bob')
  await choose(ui, 'Access', 'Add, change and delete logs permanently')
  await ui.click(screen.getByRole('button', { name: 'Give access' }))

  await screen.findByRole('combobox', { name: 'Access of Bob' })
  expect(state.calls.SetVehicleLogAccess).toEqual([{ input: { vehicleId: 'v1', userId: person('Bob').id, level: 'DELETE' } }])
  expect(screen.getByRole('combobox', { name: 'Access of Bob' })).toHaveTextContent('delete')

  await choose(ui, 'Access of Bob', 'Add and change logs')
  await waitFor(() => expect(state.grants[0]?.level).toBe('EDIT'))

  await ui.click(screen.getByRole('button', { name: 'Remove access of Bob' }))
  await screen.findByText('Nobody has been given access to the logs yet.')
  expect(state.calls.SetVehicleLogAccess?.at(-1)).toEqual({ input: { vehicleId: 'v1', userId: person('Bob').id, level: 'NONE' } })
})

it('the details tab shows the units and, for editors, the picture controls', async () => {
  setup(fakeVehicle({ units: { distance: 'MILES', volume: 'IMPERIAL_GALLONS' } }), logs, '/vehicles/v1?tab=details')

  expect(await screen.findByText('Imperial gallons')).toBeInTheDocument()
  expect(screen.getByText('Miles')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Choose a picture' })).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Remove picture' })).not.toBeInTheDocument()
})

it('removes the picture on the server at once while it answers, and says so', async () => {
  const { ui } = setupDetails(fakeVehicle({ pictureUrl: '/media/p1' }))
  const deleted: string[] = []
  server.use(http.delete('/media/*', ({ request }) => (deleted.push(new URL(request.url).pathname), new HttpResponse(null, { status: 204 }))))

  await ui.click(await screen.findByRole('button', { name: 'Remove picture' }))

  expect(await screen.findByText('Picture removed.')).toBeInTheDocument() // not "on this device": it went
  expect(deleted).toEqual(['/media/vehicles/v1/picture'])
  expect(outbox.changes).toEqual([])
})

function setupDetails(vehicle = fakeVehicle()) {
  stubViewport('desktop')
  const vehicles = fakeVehicleBackend([vehicle])
  server.use(sessionHandler('NONE', () => null), healthHandler, ...vehicles.handlers, ...fakeLogBackend(vehicle, logs).handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=details')
  return { ...vehicles, ui: userEvent.setup() }
}

it('moves the vehicle to the trash from its details, after a confirmation, and goes back home', async () => {
  const { ui, state } = setupDetails()

  await ui.click(await screen.findByRole('button', { name: 'Move to trash' }))
  const dialog = await screen.findByRole('alertdialog', { name: 'Move Octavia to the trash?' })
  expect(state.calls.DeleteVehicle).toBeUndefined() // nothing happens before the confirmation
  await ui.click(within(dialog).getByRole('button', { name: 'Move to trash' }))

  await screen.findByRole('heading', { name: 'Your vehicles' })
  expect(state.calls.DeleteVehicle).toEqual([{ id: 'v1' }])
  expect(state.trash.map((v) => v.id)).toEqual(['v1'])

  // A message says where it went, with a way back.
  expect(await screen.findByText('Octavia is in the trash.')).toBeInTheDocument()
  await ui.click(screen.getByRole('button', { name: 'Undo' }))
  await waitFor(() => expect(state.calls.RestoreVehicle).toEqual([{ id: 'v1' }]))
  expect(await screen.findByRole('link', { name: 'Open Octavia' })).toBeInTheDocument() // back on the home page
})

it('keeps the vehicle when the confirmation is cancelled', async () => {
  const { ui, state } = setupDetails()

  await ui.click(await screen.findByRole('button', { name: 'Move to trash' }))
  await ui.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancel' }))

  expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  expect(state.calls.DeleteVehicle).toBeUndefined()
  expect(screen.getByRole('heading', { name: 'Octavia' })).toBeInTheDocument()
})

it('offers no trash button to someone who may only view the vehicle', async () => {
  setupDetails(fakeVehicle({ canEdit: false, logAccess: 'VIEW' }))

  await screen.findByRole('heading', { name: 'Octavia' })

  expect(screen.queryByRole('button', { name: 'Move to trash' })).not.toBeInTheDocument()
})

it('marks a refuelling that follows a fill-up that was not logged', async () => {
  const { ui, state } = setup()
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Add refuelling' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  const missed = within(dialog).getByRole('switch', { name: 'Missed fill-up before this one' })
  expect(missed).not.toBeChecked()
  expect(missed).toHaveAccessibleDescription(/no consumption is worked out across the gap/)
  await ui.type(within(dialog).getByLabelText(/^Volume/), '38,2')
  await ui.type(within(dialog).getByLabelText('Total cost'), '19100')
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '12450')
  await ui.click(missed)
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  await waitFor(() => expect(state.calls.LogRefueling).toEqual([{ input: expect.objectContaining({ odometer: 12450, isFullTank: true, missedPreviousFillUp: true }) }]))
  expect(await screen.findByText('Missed one before')).toBeInTheDocument()
})

it('shows which log follows a missed fill-up, and editing it keeps the mark', async () => {
  const { ui, state } = setup(fakeVehicle(), [logs[0]!, { ...logs[1]!, missedPreviousFillUp: true }])
  const row = (await screen.findByText(/Sep 1, 2026/)).closest<HTMLElement>('[role="row"]')!
  expect(within(row).getByText('Missed one before')).toBeInTheDocument()
  expect(within(row).getByText('Partial')).toBeInTheDocument()

  await ui.click(screen.getByRole('button', { name: 'Edit the refuelling of Sep 1, 2026' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit refuelling' })
  await waitFor(() => expect(within(dialog).getByRole('switch', { name: 'Missed fill-up before this one' })).toBeChecked())
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))

  await waitFor(() => expect(state.calls.UpdateRefueling).toEqual([{ input: expect.objectContaining({ id: 'r2', missedPreviousFillUp: true }) }]))
})
