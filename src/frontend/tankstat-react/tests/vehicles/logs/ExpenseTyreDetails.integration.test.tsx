import { act, screen, waitFor, within } from '@testing-library/react'
import { connectivity } from '../../../src/offline/connectivity.ts'
import { outbox } from '../../../src/offline/outbox.ts'
import userEvent from '@testing-library/user-event'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { ExpenseFormDialog, type ExpenseValues } from '../../../src/ExpenseFormDialog.tsx'
import { HttpResponse } from 'msw'
import { graphql } from '../../support/graphql.ts'
import { answered, server } from '../../support/server.ts'
import { fakeExpense, fakeExpenseBackend, fakeVehicle, healthHandler, renderWithApollo, sessionHandler, stubViewport } from '../../support/mocks.tsx'
import { brokenUpSummer, fakeTyres, tyreSummer, tyreWinter, type FakeTyreEntry, type FakeTyreSet } from '../../support/tyres.ts'
import { UUID } from '../../support/ids.ts'

// An expense's tyre details in the Expense dialog (#290, the owner's decisions 51, 52, 54, 58, 59, 62, 65 and 68).

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

function setup(
  sets: FakeTyreSet[] = [tyreSummer, tyreWinter],
  initial = [fakeExpense({ id: 'e1', date: '2026-08-01', title: 'Car wash', category: 'Wash', amount: 1500, odometer: null })],
  lines: FakeTyreEntry[] = [],
) {
  stubViewport('desktop')
  const backend = fakeExpenseBackend(fakeVehicle(), initial)
  const tyres = fakeTyres(sets, lines)
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...tyres.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=expenses')
  return { ...backend, tyres: tyres.state, ui: userEvent.setup() }
}

/** An expense the tyre log made (#288), and its line(s) on the tyre log. */
const linked = (kind: string, setName: string | null, trashed = false) =>
  fakeExpense({
    id: 'e5', date: '2026-10-02', title: 'Puncture repaired: Michelin Primacy 4', category: 'Tyres', amount: 6500, odometer: 147_100,
    tyreEntry: { __typename: 'TyreEntryRef', id: 'l1', kind, setName, trashed },
  })
const line = (over: Partial<FakeTyreEntry> = {}): FakeTyreEntry => ({
  id: 'l1', kind: 'PUNCTURE_REPAIRED', setId: 's1', day: '2026-10-02', odometer: 147_100, note: null, expenseId: 'e5', amount: 6500, currency: 'HUF', ...over,
})

async function openEdit(ui: ReturnType<typeof userEvent.setup>, title: string) {
  await ui.click(await screen.findByRole('button', { name: `Edit the expense ${title}` })) // after a save, once the list is back
  return screen.findByRole('dialog', { name: 'Edit expense' })
}

async function openAdd(ui: ReturnType<typeof userEvent.setup>) {
  await screen.findByText('Car wash')
  await ui.click(screen.getByRole('button', { name: 'Add expense' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  return dialog
}

const tyreSwitch = (dialog: HTMLElement) => within(dialog).getByRole('switch', { name: 'Tyre details' })
const section = (dialog: HTMLElement) => within(dialog).queryByRole('group', { name: 'Tyre details' })

it('opens by itself for a tyre category, the title following what was done and the set, and saves the line with the expense', async () => {
  const { ui, state } = setup()
  const dialog = await openAdd(ui)
  expect(tyreSwitch(dialog)).not.toBeChecked()
  expect(tyreSwitch(dialog)).toHaveAccessibleDescription(/Opens by itself for the category Tyres, Gumik\./)

  await ui.type(within(dialog).getByLabelText(/^Category/), 'gumík') // compared without case and accents
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  expect(tyreSwitch(dialog)).toBeChecked()
  expect(within(dialog).getByLabelText(/^Category/)).toHaveValue('gumík') // typed on, never taken away
  expect(await within(dialog).findByText('Tyre details opened.')).toBeInTheDocument()
  await ui.click(within(group).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: 'Puncture repaired' }))
  expect(within(group).getByRole('combobox', { name: 'Set' })).toHaveTextContent('Summer: Michelin Primacy 4') // the set on the car
  expect(within(dialog).getByLabelText('Title')).toHaveValue('Puncture repaired: Michelin Primacy 4')
  expect(within(dialog).getByLabelText('Title')).toHaveAccessibleDescription('Follows what was done and the set until you type one.')
  await ui.type(within(dialog).getByLabelText('Amount'), '6500')
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  await waitFor(() => expect(state.calls.AddExpense).toEqual([
    { input: expect.objectContaining({ title: 'Puncture repaired: Michelin Primacy 4', category: 'gumík', amount: 6500, tyre: { kind: 'PUNCTURE_REPAIRED', setId: 's1', lineId: expect.stringMatching(UUID) } }) },
  ]))
})

it('stops at What was done until one is chosen, and a title typed stays', async () => {
  const { ui, state } = setup()
  const dialog = await openAdd(ui)
  await ui.click(tyreSwitch(dialog)) // switched on: an empty category takes the tyre one
  expect(within(dialog).getByLabelText(/^Category/)).toHaveValue('Tyres')
  await ui.type(within(dialog).getByLabelText('Title'), 'Tyre hotel, winter')
  await ui.type(within(dialog).getByLabelText('Amount'), '20000')
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  const kind = within(section(dialog)!).getByRole('combobox', { name: 'What was done' })
  expect(await within(dialog).findByText('Choose what was done.')).toBeInTheDocument()
  await waitFor(() => expect(kind).toHaveFocus())
  expect(state.calls.AddExpense).toBeUndefined()
  await ui.click(kind)
  await ui.click(await screen.findByRole('option', { name: 'Storage fee' }))
  expect(within(dialog).getByLabelText('Title')).toHaveValue('Tyre hotel, winter') // typed: it no longer follows
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  await waitFor(() => expect(state.calls.AddExpense).toEqual([{ input: expect.objectContaining({ title: 'Tyre hotel, winter', tyre: expect.objectContaining({ kind: 'STORAGE_FEE', setId: 's1' }) }) }]))
})

it('closes when the category stops being a tyre one, keeping what was picked for when it opens again; switched off it stays closed', async () => {
  const { ui, state } = setup()
  const dialog = await openAdd(ui)
  const category = within(dialog).getByLabelText(/^Category/)
  await ui.type(category, 'Tyres')
  await ui.click(within(await within(dialog).findByRole('group', { name: 'Tyre details' })).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: 'Wheel balancing' }))

  await ui.clear(category)
  await ui.type(category, 'Service')
  await waitFor(() => expect(section(dialog)).not.toBeInTheDocument())
  expect(within(dialog).getByText('Tyre details closed.')).toBeInTheDocument()
  await ui.clear(category)
  await ui.type(category, 'Tyres')
  expect(within(await within(dialog).findByRole('group', { name: 'Tyre details' })).getByRole('combobox', { name: 'What was done' })).toHaveTextContent('Wheel balancing')

  await ui.click(tyreSwitch(dialog)) // off: the category no longer opens it
  expect(section(dialog)).not.toBeInTheDocument()
  await ui.type(within(dialog).getByLabelText('Title'), 'Tyres bought')
  await ui.type(within(dialog).getByLabelText('Amount'), '90000')
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))
  await waitFor(() => expect(state.calls.AddExpense).toHaveLength(1))
  expect(state.calls.AddExpense).toEqual([{ input: expect.not.objectContaining({ tyre: expect.anything() }) }])
})

it('makes a line for each tyre tapped on a broken-up set, sharing the expense', async () => {
  const { ui, state } = setup([brokenUpSummer(), tyreWinter])
  const dialog = await openAdd(ui)
  await ui.click(tyreSwitch(dialog))
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  await ui.click(within(group).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: 'Valve replaced' }))
  await ui.click(within(group).getByRole('checkbox', { name: 'The whole set' }))
  await ui.click(within(group).getByRole('button', { name: /^Tyre S3/ }))
  await ui.click(within(group).getByRole('button', { name: /^Tyre S4/ }))
  await ui.type(within(dialog).getByLabelText('Amount'), '8000')
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  await waitFor(() => expect(state.calls.AddExpense).toEqual([
    { input: expect.objectContaining({ tyre: { kind: 'VALVE_REPLACED', setId: 's1', tyres: [{ tyreId: 't3', id: expect.stringMatching(UUID) }, { tyreId: 't4', id: expect.stringMatching(UUID) }] } }) },
  ]))
  expect(await screen.findByText('Saved for S3, S4, with one expense.')).toBeInTheDocument()
})

it('gives a plain expense its tyre details in Edit expense, opening by itself for its tyre category', async () => {
  const { ui, state } = setup(undefined, [fakeExpense({ id: 'e9', date: '2026-09-12', title: 'Gumiszerviz Kft.', category: 'Tyres', amount: 6500, odometer: null })])
  await screen.findByText('Gumiszerviz Kft.')
  await ui.click(screen.getByRole('button', { name: 'Edit the expense Gumiszerviz Kft.' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit expense' })
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  expect(tyreSwitch(dialog)).toHaveAccessibleDescription('Puts this expense on the tyre log too, with its date and odometer.')
  expect(within(dialog).getByLabelText('Title')).toHaveValue('Gumiszerviz Kft.') // an edit's title never follows
  await ui.click(within(group).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: 'Puncture repaired' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))

  await waitFor(() => expect(state.calls.UpdateExpense).toEqual([
    { input: expect.objectContaining({ id: 'e9', title: 'Gumiszerviz Kft.', tyre: { kind: 'PUNCTURE_REPAIRED', setId: 's1', lineId: expect.stringMatching(UUID) } }) },
  ]))
})

it('has no tyre details for a vehicle without a tyre set, whatever the category', async () => {
  const { ui } = setup([])
  const dialog = await openAdd(ui)
  await ui.type(within(dialog).getByLabelText(/^Category/), 'Tyres')
  expect(within(dialog).queryByRole('switch', { name: 'Tyre details' })).not.toBeInTheDocument()
  expect(section(dialog)).not.toBeInTheDocument()
})

// ---- an expense the tyre log made (the owner's decisions 53, 55, 60, 61 and 69) ------------------------------

it('shows a linked expense its line without the switch, and sends what was done and the set only when they changed', async () => {
  const { ui, state } = setup(undefined, [linked('PUNCTURE_REPAIRED', 'Michelin Primacy 4')], [line()])
  const dialog = await openEdit(ui, 'Puncture repaired: Michelin Primacy 4')
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  expect(within(dialog).queryByRole('switch', { name: 'Tyre details' })).not.toBeInTheDocument()
  expect(within(group).getByText("The date and the odometer are the line's too: changing them here changes both.")).toBeInTheDocument()
  expect(within(group).getByRole('combobox', { name: 'What was done' })).toHaveTextContent('Puncture repaired')
  await ui.clear(within(dialog).getByLabelText('Amount'))
  await ui.type(within(dialog).getByLabelText('Amount'), '7000')
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))
  await waitFor(() => expect(state.calls.UpdateExpense).toHaveLength(1))
  expect(state.calls.UpdateExpense).toEqual([{ input: expect.not.objectContaining({ tyre: expect.anything() }) }]) // only the amount changed

  const again = await openEdit(ui, 'Puncture repaired: Michelin Primacy 4')
  const section = await within(again).findByRole('group', { name: 'Tyre details' })
  await ui.click(within(section).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: 'Storage fee' }))
  await ui.click(within(section).getByRole('combobox', { name: 'Set' }))
  await ui.click(await screen.findByRole('option', { name: 'Winter: Continental WinterContact' }))
  await ui.click(within(again).getByRole('button', { name: 'Save changes' }))
  await waitFor(() => expect(state.calls.UpdateExpense).toHaveLength(2))
  expect(state.calls.UpdateExpense).toEqual([expect.anything(), { input: expect.objectContaining({ id: 'e5', tyre: { kind: 'STORAGE_FEE', setId: 's2' } }) }])
})

it("keeps a tyre's set, names its tyres, and removes every line holding the expense from the tyre log", async () => {
  const lines = [line({ id: 'l1', tyreId: 't3', groupId: 'l1' }), line({ id: 'l2', tyreId: 't4', groupId: 'l1' })]
  const { ui, tyres } = setup([brokenUpSummer(), tyreWinter], [linked('PUNCTURE_REPAIRED', 'Michelin Primacy 4')], lines)
  const dialog = await openEdit(ui, 'Puncture repaired: Michelin Primacy 4')
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  expect(within(group).getByRole('combobox', { name: 'Set' })).toHaveAttribute('aria-disabled', 'true')
  expect(within(group).getByRole('combobox', { name: 'Set' })).toHaveAccessibleDescription("A tyre's line stays with its set.")
  expect(within(group).getByRole('combobox', { name: 'What was done' })).toHaveAccessibleDescription('Changes the lines of S3, S4: this amount is one expense, shared with them.')
  expect(within(group).getByText('For: S3, S4')).toBeInTheDocument()

  await ui.click(await within(dialog).findByRole('button', { name: 'Remove from the tyre log' }))
  const confirm = await screen.findByRole('alertdialog', { name: 'Remove it from the tyre log?' })
  expect(confirm).toHaveAccessibleDescription(/^The lines of S3, S4 go to the trash\. This expense stays here/)
  await ui.click(within(confirm).getByRole('button', { name: 'Remove from the tyre log' }))

  expect(await screen.findByText('Removed from the tyre log. Its line is in the trash.')).toBeInTheDocument()
  expect(tyres.calls.DeleteTyreEntry).toEqual([{ input: { id: 'l1', withExpense: false } }, { input: { id: 'l2', withExpense: false } }])
  expect(screen.getByRole('dialog', { name: 'Edit expense' })).toBeInTheDocument() // the dialog stays open
})

it("locks a swap's line whole, and offers no removal: the swap is moved to the trash in the tyre log", async () => {
  const { ui } = setup(undefined, [linked('SWAP', 'Michelin Primacy 4')], [line({ kind: 'SWAP' })])
  const dialog = await openEdit(ui, 'Puncture repaired: Michelin Primacy 4')
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  expect(within(group).getByRole('combobox', { name: 'What was done' })).toHaveAttribute('aria-disabled', 'true')
  expect(within(group).getByRole('combobox', { name: 'Set' })).toHaveAttribute('aria-disabled', 'true')
  expect(within(dialog).queryByRole('button', { name: 'Remove from the tyre log' })).not.toBeInTheDocument()
  expect(await within(dialog).findByText("A swap's line stays: removing it would remove the swap. Move the swap to the trash in the tyre log.")).toBeInTheDocument()
})

it('says when its line is in the trash, with no switch and no section', async () => {
  const { ui } = setup(undefined, [linked('PUNCTURE_REPAIRED', 'Michelin Primacy 4', true)])
  const dialog = await openEdit(ui, 'Puncture repaired: Michelin Primacy 4')
  expect(await within(dialog).findByText(/Puncture repaired, Michelin Primacy 4\. Its line is in the trash: restoring it there links them again\./)).toBeInTheDocument()
  expect(within(dialog).queryByRole('switch', { name: 'Tyre details' })).not.toBeInTheDocument()
  expect(within(dialog).queryByRole('group', { name: 'Tyre details' })).not.toBeInTheDocument()
  expect(within(dialog).queryByRole('link', { name: 'Open in the tyre log' })).not.toBeInTheDocument()
})

// ---- offline (O1, the owner's decision 70) --------------------------------------------------------------------

it('keeps an expense with tyre details on the device, its line shown on the tyre log, marked', async () => {
  const { ui, state } = setup()
  const dialog = await openAdd(ui)
  await within(dialog).findByRole('switch', { name: 'Tyre details' }) // the tyres are on the device now
  await answered()
  act(() => connectivity.failed())

  await ui.type(within(dialog).getByLabelText(/^Category/), 'Tyres')
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  await ui.click(within(group).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: 'Wheel balancing' }))
  await ui.type(within(dialog).getByLabelText('Amount'), '12000')
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  expect(await screen.findByText('Saved on this device · syncs when online.')).toBeInTheDocument()
  expect(state.calls.AddExpense).toBeUndefined()
  expect(outbox.changes).toMatchObject([{ entity: 'expenses', action: 'add', input: { title: 'Wheel balancing: Michelin Primacy 4', tyre: { kind: 'WHEEL_BALANCING', setId: 's1' } } }])
  await ui.click(screen.getByRole('tab', { name: 'Tyres' }))
  const log = await screen.findByRole('region', { name: 'Tyre log' })
  expect(await within(log).findByRole('button', { name: /^Wheel balancing, Michelin Primacy 4, .*, HUF\s?12,000/ })).toBeInTheDocument()
  expect(within(screen.getByRole('article', { name: 'Michelin Primacy 4' })).getAllByText('updated once synced').length).toBeGreaterThan(0) // what it cost: the server's to work out

})

it('says in the dialog what the server would refuse of a change kept on the device', async () => {
  const { ui, state } = setup()
  const dialog = await openAdd(ui)
  await within(dialog).findByRole('switch', { name: 'Tyre details' })
  await answered()
  act(() => connectivity.failed())
  await ui.click(tyreSwitch(dialog))
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  await ui.click(within(group).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: 'Puncture repaired' }))
  await ui.click(within(group).getByRole('checkbox', { name: 'The whole set' }))
  await ui.click(within(group).getByRole('button', { name: /front left/ }))
  await ui.clear(within(group).getByLabelText(/^Label, front right/))
  await ui.type(within(group).getByLabelText(/^Label, front right/), 'S1') // the same label twice: the break-up is refused
  await ui.type(within(dialog).getByLabelText('Amount'), '5000')
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  expect(await within(dialog).findByText('Each tyre needs its own label.')).toBeInTheDocument()
  expect(outbox.changes).toEqual([])
  expect(state.calls.AddExpense).toBeUndefined()
})

// ---- a change edited on Waiting to sync (decision 70) ---------------------------------------------------------

function editChange(sets: FakeTyreSet[], tyre?: Record<string, unknown>, { lines = [], targetId, noAnswer = false }: { lines?: FakeTyreEntry[]; targetId?: string; noAnswer?: boolean } = {}) {
  stubViewport('desktop')
  const backend = fakeExpenseBackend(fakeVehicle(), [])
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...fakeTyres(sets, lines).handlers)
  // The vehicle's tyres not answered (a failure, or offline with none kept).
  if (noAnswer) server.use(graphql.query('VehicleTyres', () => HttpResponse.json({ errors: [{ message: 'Unexpected Execution Error' }] })))
  const sent: ExpenseValues[] = []
  renderWithApollo(
    <ExpenseFormDialog
      open
      vehicle={{ id: 'v1', units: { distance: 'KILOMETERS' } }}
      change={{
        initial: { date: '2026-10-05', title: 'Puncture', category: 'Tyres', amount: 5000, currency: 'HUF', odometer: null, note: null, ...(tyre ? { tyre: tyre as never } : {}) },
        title: 'Edit the change',
        submitLabel: 'Save the change',
        targetId,
      }}
      onSubmit={async (values) => {
        sent.push(values)
        return undefined
      }}
    />,
  )
  return { sent, ui: userEvent.setup() }
}

it("shows a waiting change's tyre details to fix, keeping its own ids, and switched off sends them as none", async () => {
  const { sent, ui } = editChange([brokenUpSummer(), tyreWinter], { kind: 'PUNCTURE_REPAIRED', setId: 's1', tyres: [{ tyreId: 't3', id: 'L3' }, { tyreId: 't4', id: 'L4' }] })
  const dialog = await screen.findByRole('dialog', { name: 'Edit the change' })
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  expect(within(group).getByRole('combobox', { name: 'What was done' })).toHaveTextContent('Puncture repaired')
  expect(within(group).getByRole('button', { name: /^Tyre S3/ })).toHaveAttribute('aria-pressed', 'true')
  await ui.click(within(group).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: 'Valve replaced' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(sent).toHaveLength(1))
  expect(sent[0].tyre).toEqual({ kind: 'VALVE_REPLACED', setId: 's1', tyres: [{ tyreId: 't3', id: 'L3' }, { tyreId: 't4', id: 'L4' }] }) // its own line ids
})

it('switched off in a change\'s edit, its tyre details go as none', async () => {
  const { sent, ui } = editChange([tyreSummer, tyreWinter], { kind: 'STORAGE_FEE', setId: 's2', lineId: 'L1' })
  const dialog = await screen.findByRole('dialog', { name: 'Edit the change' })
  await within(dialog).findByRole('group', { name: 'Tyre details' })
  await ui.click(within(dialog).getByRole('switch', { name: 'Tyre details' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(sent).toHaveLength(1))
  expect(sent[0].tyre).toBeNull()
})

it("opens an edit of an expense the tyre log made as linked: no switch, and an amount changed sends no tyre details", async () => {
  const { sent, ui } = editChange([tyreSummer, tyreWinter], undefined, { lines: [line()], targetId: 'e5' })
  const dialog = await screen.findByRole('dialog', { name: 'Edit the change' })
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  expect(within(dialog).queryByRole('switch', { name: 'Tyre details' })).not.toBeInTheDocument()
  expect(within(group).getByRole('combobox', { name: 'What was done' })).toHaveTextContent('Puncture repaired')
  await ui.clear(within(dialog).getByLabelText('Amount'))
  await ui.type(within(dialog).getByLabelText('Amount'), '7000')
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(sent).toHaveLength(1))
  expect(sent[0]).not.toHaveProperty('tyre')
})

it("shows what a linked expense's waiting edit says was done, and keeps sending it", async () => {
  const { sent, ui } = editChange([tyreSummer, tyreWinter], { kind: 'STORAGE_FEE', setId: 's1' }, { lines: [line()], targetId: 'e5' })
  const dialog = await screen.findByRole('dialog', { name: 'Edit the change' })
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  expect(within(group).getByRole('combobox', { name: 'What was done' })).toHaveTextContent('Storage fee') // the change's, not the line's as it is
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(sent).toHaveLength(1))
  expect(sent[0].tyre).toEqual({ kind: 'STORAGE_FEE', setId: 's1' })
})

it("leaves a change's tyre details as they are when its edit has no section (the vehicle's tyres not answered)", async () => {
  const { sent, ui } = editChange([tyreSummer], { kind: 'STORAGE_FEE', setId: 's2', lineId: 'L1' }, { noAnswer: true })
  const dialog = await screen.findByRole('dialog', { name: 'Edit the change' })
  await within(dialog).findByLabelText('Amount')
  expect(within(dialog).queryByRole('switch', { name: 'Tyre details' })).not.toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(sent).toHaveLength(1))
  expect(sent[0]).not.toHaveProperty('tyre') // the change keeps its own, never switched off unseen
})

it("shows a change's tyre details when the vehicle has no tyre set left, so they can be switched off", async () => {
  const { sent, ui } = editChange([], { kind: 'STORAGE_FEE', setId: 's2', lineId: 'L1' })
  const dialog = await screen.findByRole('dialog', { name: 'Edit the change' })
  await within(dialog).findByRole('group', { name: 'Tyre details' })
  await ui.click(within(dialog).getByRole('switch', { name: 'Tyre details' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(sent).toHaveLength(1))
  expect(sent[0].tyre).toBeNull() // its only set gone (refused for it): it can be applied without them
})

it("starts a change whose set is gone at the whole set on the car, none of the gone set's tyres picked", async () => {
  const { sent, ui } = editChange([tyreSummer, tyreWinter], {
    kind: 'PUNCTURE_REPAIRED', setId: 'gone', tyres: [{ tyreId: 'n1', id: 'L1' }],
    breakUp: { setId: 'gone', day: '2026-10-05', tyres: [{ id: 'n1', label: 'G1', corner: 'FRONT_LEFT' }] },
  })
  const dialog = await screen.findByRole('dialog', { name: 'Edit the change' })
  await within(dialog).findByRole('group', { name: 'Tyre details' })
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(sent).toHaveLength(1))
  expect(sent[0].tyre).toEqual({ kind: 'PUNCTURE_REPAIRED', setId: expect.any(String), lineId: expect.any(String) })
})

it('removed from the tyre log while the server is out of reach, says it is saved on the device', async () => {
  const { ui, tyres } = setup(undefined, [linked('PUNCTURE_REPAIRED', 'Michelin Primacy 4')], [line()])
  const dialog = await openEdit(ui, 'Puncture repaired: Michelin Primacy 4')
  await ui.click(await within(dialog).findByRole('button', { name: 'Remove from the tyre log' }))
  const confirm = await screen.findByRole('alertdialog', { name: 'Remove it from the tyre log?' })
  await answered()
  act(() => connectivity.failed())
  await ui.click(within(confirm).getByRole('button', { name: 'Remove from the tyre log' }))

  expect(await screen.findByText('Saved on this device · syncs when online.')).toBeInTheDocument()
  expect(screen.queryByText('Removed from the tyre log. Its line is in the trash.')).not.toBeInTheDocument()
  expect(tyres.calls.DeleteTyreEntry).toBeUndefined()
  expect(outbox.changes).toMatchObject([{ entity: 'tyres', action: 'deleteTyreEntry', input: { id: 'l1', withExpense: false } }])
})
