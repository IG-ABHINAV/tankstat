import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { server } from '../../support/server.ts'
import { fakeExpense, fakeExpenseBackend, fakeVehicle, healthHandler, renderWithApollo, sessionHandler, stubViewport } from '../../support/mocks.tsx'
import { brokenUpSummer, fakeTyres, tyreSummer, tyreWinter, type FakeTyreSet } from '../../support/tyres.ts'
import { UUID } from '../../support/ids.ts'

// An expense's tyre details in the Expense dialog (#290, the owner's decisions 51, 52, 54, 58, 59, 62, 65 and 68).

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

function setup(sets: FakeTyreSet[] = [tyreSummer, tyreWinter], initial = [fakeExpense({ id: 'e1', date: '2026-08-01', title: 'Car wash', category: 'Wash', amount: 1500, odometer: null })]) {
  stubViewport('desktop')
  const backend = fakeExpenseBackend(fakeVehicle(), initial)
  const tyres = fakeTyres(sets)
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...tyres.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=expenses')
  return { ...backend, ui: userEvent.setup() }
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
