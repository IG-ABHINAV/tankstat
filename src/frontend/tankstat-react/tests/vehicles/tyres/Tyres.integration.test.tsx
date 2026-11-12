import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse } from 'msw'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { graphql } from '../../support/graphql.ts'
import { answered, server } from '../../support/server.ts'
import { connectivity } from '../../../src/offline/connectivity.ts'
import { outbox } from '../../../src/offline/outbox.ts'
import { fakeExpense, fakeExpenseBackend, fakeVehicle, gqlError, healthHandler, renderWithApollo, sessionHandler, stubViewport } from '../../support/mocks.tsx'
import { UUID } from '../../support/ids.ts'
import { fakeTyres, tyreSummer, tyreWinter, type FakeTyreEntry, type FakeTyreSet } from '../../support/tyres.ts'
import { findDateField, typeDate } from '../../support/dates.ts'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

const summer = tyreSummer
const winter = tyreWinter
type Set = FakeTyreSet

function setup(sets: Set[], { vehicle = fakeVehicle(), entries = [] as FakeTyreEntry[], swapLines = {} as Parameters<typeof fakeTyres>[2] } = {}) {
  stubViewport('desktop')
  const expenses = fakeExpenseBackend(vehicle, [])
  const tyres = fakeTyres(sets, entries, swapLines)
  server.use(sessionHandler('NONE', () => null), healthHandler, ...expenses.handlers, ...tyres.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=tyres')
  return { ...tyres, ui: userEvent.setup() }
}

it('starts empty with a way to add the set on the car, and adds it with the ids it chose', async () => {
  const { ui, state } = setup([])

  await ui.click(await screen.findByRole('button', { name: 'Add the tyres on the car' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add a tyre set' })
  await ui.type(within(dialog).getByLabelText(/^Name/), 'Michelin Primacy 4')
  await ui.type(within(dialog).getByLabelText(/^Driven so far/), '32900')
  await typeDate(ui, await findDateField(/In use since/, dialog), '2023-03-25')
  await ui.click(within(dialog).getByRole('button', { name: 'Save set' }))

  await waitFor(() => expect(state.calls.AddTyreSet).toHaveLength(1))
  expect(state.calls.AddTyreSet[0].input).toMatchObject({
    vehicleId: 'v1', name: 'Michelin Primacy 4', season: 'SUMMER', since: '2023-03-25', fitted: true, roughDistance: 32_900, replace: false, id: UUID, installationId: UUID,
  })
})

it('shows each set with its distance, age and installation, and the tyre log a line per swap with the reading typed there', async () => {
  setup([summer, winter], { swapLines: { r1: { odometer: 141_000 } } })

  const onCar = await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  expect(within(onCar).getByText('≈ 40,600 km')).toBeInTheDocument()
  expect(within(onCar).getByText(/On since .* · 7,700 km in this installation · pair 1 at the front/)).toBeInTheDocument()
  expect(within(screen.getByRole('article', { name: 'Continental WinterContact' })).getByText(/In storage · last: pair 1 at the front/)).toBeInTheDocument()
  const log = screen.getByRole('region', { name: 'Tyre log' })
  expect(within(log).getByRole('button', { name: 'Tyre swap, Michelin Primacy 4, Mar 28, 2026' })).toHaveTextContent('141,000 km')
  expect(within(log).getByRole('button', { name: 'Tyre swap, Continental WinterContact, Nov 15, 2025' })).toHaveTextContent('before the fuel log') // before the readings here
})

it('hides every change from someone who may only view, and says why', async () => {
  setup([summer, winter], { vehicle: fakeVehicle({ canEdit: false, logAccess: 'VIEW' }) })

  expect(await screen.findByText(/Only someone who may log for this vehicle can change them/)).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Swap tyres' })).toBeNull()
  expect(screen.getAllByRole('button', { name: 'History' })).toHaveLength(2)
})

it('swaps with the pairs exchanged, says the worked-out odometer, and asks once before replacing a day that has a swap', async () => {
  const { ui, state } = setup([summer, winter])
  state.failNext.push({ key: 'tyres.swapExists', args: { date: '2026-09-20', set: 'Michelin Primacy 4' } })

  await ui.click(await screen.findByRole('button', { name: 'Swap tyres' }))
  const dialog = await screen.findByRole('dialog', { name: 'Swap tyres' })
  await typeDate(ui, await findDateField(/^Day/, dialog), '2026-06-01')
  expect(within(dialog).getByText(/Left empty, the app works it out from the fuel log: ≈ 143,500 km/)).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('radio', { name: 'Front ↔ rear' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Swap tyres' }))

  expect(await within(dialog).findByText(/This replaces the swap of .* \(Michelin Primacy 4 went on\)/)).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Replace the swap' }))

  await waitFor(() => expect(state.calls.SwapTyres).toHaveLength(2))
  expect(state.calls.SwapTyres[0].input).toMatchObject({ setId: 's2', day: '2026-06-01', pairOneFront: false, replace: false, installationId: UUID })
  expect(state.calls.SwapTyres[1].input).toMatchObject({ replace: true, installationId: (state.calls.SwapTyres[0].input as { installationId: string }).installationId })
})

it('asks again when the day changes after the replace note, never replacing a day it did not name', async () => {
  const { ui, state } = setup([summer, winter])
  state.failNext.push({ key: 'tyres.swapExists', args: { date: '2026-06-01', set: 'Michelin Primacy 4' } })

  await ui.click(await screen.findByRole('button', { name: 'Swap tyres' }))
  const dialog = await screen.findByRole('dialog', { name: 'Swap tyres' })
  await typeDate(ui, await findDateField(/^Day/, dialog), '2026-06-01')
  await ui.click(within(dialog).getByRole('button', { name: 'Swap tyres' }))
  expect(await within(dialog).findByRole('button', { name: 'Replace the swap' })).toBeInTheDocument()

  await typeDate(ui, await findDateField(/^Day/, dialog), '2026-06-02')
  expect(within(dialog).queryByText(/This replaces the swap of/)).not.toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Swap tyres' }))

  await waitFor(() => expect(state.calls.SwapTyres).toHaveLength(2))
  expect(state.calls.SwapTyres[1].input).toMatchObject({ day: '2026-06-02', replace: false })
})

it('adds a set to storage by default once a set is on the car, though the car was bare when the tab opened', async () => {
  const stored = { ...winter, onTheCar: false }
  const { ui, state } = setup([stored])

  await ui.click(await screen.findByRole('button', { name: 'Fit this set' }))
  const swap = await screen.findByRole('dialog', { name: 'Swap tyres' })
  state.sets = [{ ...stored, onTheCar: true }] // the server's answer: the set is on the car now
  await ui.click(within(swap).getByRole('button', { name: 'Swap tyres' }))
  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Swap tyres' })).not.toBeInTheDocument())
  expect(state.calls.SwapTyres).toHaveLength(1)
  expect(await screen.findByRole('button', { name: 'Change positions' })).toBeInTheDocument()

  await ui.click(await screen.findByRole('button', { name: 'Add a tyre set' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add a tyre set' })
  expect(within(dialog).getByRole('radio', { name: 'In storage' })).toBeChecked()
})

it('takes past swaps from papers, a row each, a reading only where there is one', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(await screen.findByRole('button', { name: 'Add past swaps' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add past swaps' })
  await typeDate(ui, await findDateField(/^Day/, dialog), '2025-03-29')
  await ui.click(within(dialog).getByRole('button', { name: 'Add a row' }))
  const second = within(dialog).getAllByRole('group').find((g) => g.tagName === 'FIELDSET' && /^Swap 2/.test(g.textContent ?? ''))!
  await typeDate(ui, await findDateField(/^Day/, second), '2025-11-15')
  await ui.type(within(dialog).getAllByLabelText(/^Odometer/)[0], '128150')
  await ui.click(within(dialog).getByRole('button', { name: 'Save swaps' }))

  await waitFor(() => expect(state.calls.AddPastTyreSwaps).toHaveLength(1))
  const swaps = (state.calls.AddPastTyreSwaps[0].input as { swaps: Record<string, unknown>[] }).swaps
  expect(swaps).toMatchObject([
    { day: '2025-03-29', setId: 's1', odometer: 128_150, installationId: UUID },
    { day: '2025-11-15', setId: 's1', odometer: null, installationId: UUID },
  ])
})

it('asks again about past swaps once the rows change after the replace note', async () => {
  const { ui, state } = setup([summer, winter])
  state.failNext.push({ key: 'tyres.swapExists', args: { date: '2026-03-28', set: 'Michelin Primacy 4' } })

  await ui.click(await screen.findByRole('button', { name: 'Add past swaps' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add past swaps' })
  await typeDate(ui, await findDateField(/^Day/, dialog), '2026-03-28')
  await ui.click(within(dialog).getByRole('button', { name: 'Save swaps' }))
  expect(await within(dialog).findByRole('button', { name: 'Replace the swap' })).toBeInTheDocument()

  await ui.click(within(dialog).getByRole('button', { name: 'Add a row' }))
  expect(within(dialog).getByRole('button', { name: 'Save swaps' })).toBeInTheDocument()
})

it('moves a swap to the trash from the history, its line with it, saying what that removes, and takes it back with Undo', async () => {
  const { ui, state } = setup([summer, winter], { swapLines: { r1: { odometer: 141_000 } } })

  const card = await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  await ui.click(within(card).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Michelin Primacy 4' })
  await ui.click(within(history).getByRole('button', { name: 'Move to the trash: Tyre swap, Mar 28, 2026' }))
  const confirm = await screen.findByRole('alertdialog', { name: 'Move to the trash?' })
  expect(confirm).toHaveTextContent('This removes the swap: Michelin Primacy 4 on the car from Mar 28, 2026. Its distance no longer counts.')
  expect(confirm).toHaveTextContent("The reading of 141,000 km no longer counts among the car's readings until the line is restored.")
  await ui.click(within(confirm).getByRole('button', { name: 'Move to the trash' }))

  await waitFor(() => expect(state.calls.DeleteTyreEntry).toEqual([{ input: { id: 'r1', withExpense: false } }])) // it has no expense
  await ui.click(await screen.findByRole('button', { name: 'Undo', hidden: true })) // the history dialog is still open over the page
  await waitFor(() => expect(state.calls.RestoreTyreEntry).toEqual([{ id: 'r1' }]))
})

it('retires a set in storage after asking, with Undo', async () => {
  const { ui, state } = setup([summer, winter])

  const card = await screen.findByRole('article', { name: 'Continental WinterContact' })
  await ui.click(within(card).getByRole('button', { name: 'Retire' }))
  await ui.click(within(await screen.findByRole('alertdialog', { name: 'Retire this set?' })).getByRole('button', { name: 'Retire set' }))

  await waitFor(() => expect(state.calls.RetireTyreSet).toHaveLength(1))
  expect(state.calls.RetireTyreSet[0]).toMatchObject({ id: 's2' })
  expect(within(await screen.findByRole('article', { name: 'Michelin Primacy 4' })).queryByRole('button', { name: 'Retire' })).toBeNull() // only in storage
})

it('lists trashed sets and tyre log lines on the Trash page, says why a restore is refused, and restores a set', async () => {
  stubViewport('desktop')
  const calls: Record<string, unknown[]> = {}
  const rows = [
    { __typename: 'TyreTrashRow', kind: 'ENTRY', entryKind: 'SWAP', id: 'r9', setName: 'Continental WinterContact', day: '2025-11-15', until: '2026-03-28', installationCount: 0, deletedAt: '2026-11-09T21:14:00Z', version: 2, canDelete: true, vehicle: { __typename: 'Vehicle', id: 'v1', name: 'Octavia' } },
    { __typename: 'TyreTrashRow', kind: 'SET', entryKind: null, id: 's9', setName: 'Pirelli Cinturato', day: null, until: null, installationCount: 6, deletedAt: '2026-11-02T18:02:00Z', version: 3, canDelete: true, vehicle: { __typename: 'Vehicle', id: 'v1', name: 'Octavia' } },
    { __typename: 'TyreTrashRow', kind: 'ENTRY', entryKind: 'OTHER', id: 'e9', setName: null, day: '2026-05-02', until: null, installationCount: 0, deletedAt: '2026-11-01T10:00:00Z', version: 2, canDelete: true, vehicle: { __typename: 'Vehicle', id: 'v1', name: 'Octavia' } },
  ]
  server.use(
    sessionHandler('NONE', () => null),
    healthHandler,
    graphql.query('TyreTrash', ({ variables }) => {
      ;(calls.TyreTrash ??= []).push(variables)
      return HttpResponse.json({ data: { tyreTrash: rows, tyreTrashCount: rows.length, tyreTrashDeletableCount: rows.length } })
    }),
    graphql.mutation('RestoreTyreEntry', () =>
      HttpResponse.json(gqlError('refused', 'VALIDATION_FAILED', 'tyres.restoreConflict', { set: 'Michelin Primacy 4', from: '2026-03-20', until: '2026-11-10' })),
    ),
    graphql.mutation('RestoreTyreSet', ({ variables }) => {
      ;(calls.RestoreTyreSet ??= []).push(variables)
      return HttpResponse.json({ data: { restoreTyreSet: { __typename: 'VehicleTyres', vehicleId: 'v1', hasReadings: false, readings: [], entries: [], sets: [] } } })
    }),
  )
  const ui = userEvent.setup()
  renderWithApollo(<App />, '/trash?tab=tyres')

  expect(await screen.findByRole('rowheader', { name: 'Tyre swap: Continental WinterContact' })).toBeInTheDocument()
  expect(screen.getByText('Nov 15, 2025 · on the car until Mar 28, 2026')).toBeInTheDocument()
  expect(screen.getByRole('rowheader', { name: 'Other' })).toBeInTheDocument() // a line of no set
  expect(screen.getByText('with 6 installations')).toBeInTheDocument()
  expect(calls.TyreTrash[0]).toMatchObject({ orderBy: 'DELETED_AT', direction: 'DESC', skip: 0 })

  await ui.click(screen.getByRole('button', { name: 'Restore Tyre swap: Continental WinterContact' }))
  expect(await screen.findByText('It cannot be restored as it is: Michelin Primacy 4 is on the car then, from 2026-03-20.')).toBeInTheDocument()

  await ui.click(screen.getByRole('button', { name: 'Restore Set: Pirelli Cinturato' }))
  await waitFor(() => expect(calls.RestoreTyreSet).toEqual([{ id: 's9' }]))
})

it('says a line restored from the trash brought its expense back when the two went there together (#288)', async () => {
  stubViewport('desktop')
  const row = { __typename: 'TyreTrashRow', kind: 'ENTRY', entryKind: 'PUNCTURE_REPAIRED', id: 'e9', setName: 'Michelin Primacy 4', day: '2026-06-01', until: null, installationCount: 0,
    deletedAt: '2026-11-09T21:14:00Z', version: 2, withExpense: true, canDelete: true, vehicle: { __typename: 'Vehicle', id: 'v1', name: 'Octavia' } }
  server.use(
    sessionHandler('NONE', () => null),
    healthHandler,
    graphql.query('TyreTrash', () => HttpResponse.json({ data: { tyreTrash: [row], tyreTrashCount: 1, tyreTrashDeletableCount: 1 } })),
    graphql.mutation('RestoreTyreEntry', () => HttpResponse.json({ data: { restoreTyreEntry: { __typename: 'VehicleTyres', vehicleId: 'v1', hasReadings: false, expenseCategory: null, readings: [], entries: [], sets: [] } } })),
  )
  const ui = userEvent.setup()
  renderWithApollo(<App />, '/trash?tab=tyres')

  await ui.click(await screen.findByRole('button', { name: 'Restore Puncture repaired: Michelin Primacy 4' }))
  expect(await screen.findByText('Restored, with its expense.')).toBeInTheDocument()
})

it('changes the positions of the set on the car: front and rear exchanged from a day', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(await screen.findByRole('button', { name: 'Change positions' }))
  const dialog = await screen.findByRole('dialog', { name: 'Change positions' })
  expect(within(dialog).getByText(/Now: pair 1 at the front\./)).toBeInTheDocument()
  expect(within(dialog).getByRole('radio', { name: 'Front ↔ rear' })).toBeChecked()
  await ui.click(within(dialog).getByRole('button', { name: 'Save positions' }))

  await waitFor(() => expect(state.calls.ChangeTyrePositions).toHaveLength(1))
  expect(state.calls.ChangeTyrePositions[0].input).toMatchObject({ setId: 's1', pairOneFront: false, installationId: UUID })
})

it('adds an installation to a set’s history with its first day’s reading, and edits one', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(within(await screen.findByRole('article', { name: 'Continental WinterContact' })).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Continental WinterContact' })
  await ui.click(within(history).getByRole('button', { name: 'Add an installation' }))
  const add = await screen.findByRole('dialog', { name: 'Add an installation' })
  await typeDate(ui, await findDateField(/First day on the car/, add), '2024-11-09')
  await typeDate(ui, await findDateField(/Day it came off/, add), '2025-03-29')
  await ui.type(within(add).getByLabelText(/^Odometer on the first day/), '118500')
  await ui.click(within(add).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(state.calls.AddTyreInstallation).toHaveLength(1))
  expect(state.calls.AddTyreInstallation[0].input).toMatchObject({ id: UUID, setId: 's2', from: '2024-11-09', until: '2025-03-29', pairOneFront: null, odometerFrom: 118_500 })

  await ui.click(within(history).getByRole('button', { name: 'Edit' }))
  const edit = await screen.findByRole('dialog', { name: 'Edit installation' })
  await ui.click(within(edit).getByRole('combobox', { name: 'Pairs' }))
  await ui.click(screen.getByRole('option', { name: 'pair 2 at the front' }))
  await ui.click(within(edit).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(state.calls.UpdateTyreInstallation).toHaveLength(1))
  expect(state.calls.UpdateTyreInstallation[0].input).toMatchObject({ id: 'r2', from: '2025-11-15', until: '2026-03-28', pairOneFront: false, odometerFrom: null })
})

it('suggests a new rough figure once the history reaches back, and saves it with the day it now stands for', async () => {
  const { ui, state } = setup([{ ...summer, suggestion: { distance: 12_900, until: '2025-03-29', measuredBefore: 20_000 } }, winter])

  const card = await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  expect(within(card).getByText(/the rough figure only stands for the time before/)).toBeInTheDocument()
  await ui.click(within(card).getByRole('button', { name: 'Check the rough figure' }))
  const dialog = await screen.findByRole('dialog', { name: 'The rough figure' })
  expect(within(dialog).getByLabelText(/^Driven before that/)).toHaveValue('12900')
  expect(within(dialog).getByText(/It was 32,900 km and stood for more time\. 12,900 km keeps the total as it was\./)).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(state.calls.SetTyreRoughDistance).toEqual([{ input: { id: 's1', distance: 12_900, until: '2025-03-29' } }]))
})

it('adds a line to the tyre log with the ids it chose, for the set on the car by default', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(await screen.findByRole('button', { name: 'Add entry' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add entry' })
  expect(within(dialog).getByRole('combobox', { name: 'What was done' })).toHaveTextContent('Puncture repaired')
  expect(within(dialog).getByRole('combobox', { name: 'Set' })).toHaveTextContent('Summer: Michelin Primacy 4')
  await typeDate(ui, await findDateField(/^Day/, dialog), '2026-06-01')
  expect(within(dialog).getByText(/Optional\. It becomes one of the car's readings\./)).toBeInTheDocument()
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '143200')
  await ui.type(within(dialog).getByLabelText(/^Note/), 'Rear left')
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(state.calls.AddTyreEntry).toHaveLength(1))
  expect(state.calls.AddTyreEntry[0].input).toMatchObject({ vehicleId: 'v1', id: UUID, kind: 'PUNCTURE_REPAIRED', setId: 's1', day: '2026-06-01', odometer: 143_200, note: 'Rear left' })
})

it('changes a line from the log, and moves one to the trash after asking, with Undo', async () => {
  const { ui, state } = setup([summer, winter], { entries: [{ id: 'e1', kind: 'VALVE_REPLACED', setId: 's2', day: '2025-06-01', odometer: null, note: 'Pressure checked' }] })

  const log = await screen.findByRole('region', { name: 'Tyre log' })
  const line = within(log).getByRole('button', { name: 'Valve replaced, Continental WinterContact, Jun 1, 2025' })
  expect(line).toHaveTextContent('Pressure checked')
  await ui.click(line)
  const dialog = await screen.findByRole('dialog', { name: 'Edit entry' })
  await ui.clear(within(dialog).getByLabelText(/^Note/))
  await ui.type(within(dialog).getByLabelText(/^Note/), 'Front right')
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(state.calls.UpdateTyreEntry).toHaveLength(1))
  expect(state.calls.UpdateTyreEntry[0].input).toMatchObject({ id: 'e1', kind: 'VALVE_REPLACED', setId: 's2', day: '2025-06-01', odometer: null, note: 'Front right', fee: { amount: null } })

  await ui.click(within(log).getByRole('button', { name: 'Move to the trash: Valve replaced, Jun 1, 2025' }))
  const confirm = await screen.findByRole('alertdialog', { name: 'Move to the trash?' })
  expect(confirm).toHaveTextContent('Valve replaced, Continental WinterContact, Jun 1, 2025 · Pressure checked. It can be restored from the trash.')
  await ui.click(within(confirm).getByRole('button', { name: 'Move to the trash' }))
  await waitFor(() => expect(state.calls.DeleteTyreEntry).toEqual([{ input: { id: 'e1', withExpense: false } }]))
  await ui.click(await screen.findByRole('button', { name: 'Undo' }))
  await waitFor(() => expect(state.calls.RestoreTyreEntry).toEqual([{ id: 'e1' }]))
})

it('keeps a swap’s line to its kind and set, saying why, and moves the swap with its day, saying a refusal in the dialog', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(await screen.findByRole('button', { name: 'Tyre swap, Michelin Primacy 4, Mar 28, 2026' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit entry' })
  expect(within(dialog).getByRole('combobox', { name: 'What was done' })).toHaveAttribute('aria-disabled', 'true')
  expect(within(dialog).getByRole('combobox', { name: 'Set' })).toHaveAttribute('aria-disabled', 'true')
  expect(within(dialog).getAllByText('A swap keeps its set. For another set, move the swap to the trash and log it again.')).toHaveLength(2)
  expect(within(dialog).getByText('Changing the day moves the swap: the set is on the car from that day.')).toBeInTheDocument()
  state.failNext.push({ key: 'tyres.swapBeforePrevious', args: { set: 'Continental WinterContact', from: '2025-11-15' } })
  await typeDate(ui, await findDateField(/^Day/, dialog), '2025-11-15')
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))

  expect(await within(dialog).findByText('The swap cannot move to or before 2025-11-15, the day Continental WinterContact went on the car before it.')).toBeInTheDocument()
  expect(state.calls.UpdateTyreEntry).toHaveLength(1)
  expect(state.calls.UpdateTyreEntry[0].input).toMatchObject({ id: 'r1', kind: 'SWAP', setId: 's1', day: '2025-11-15', odometer: null, note: null })
})

it('moves a set to the trash from its history, with Undo', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(within(await screen.findByRole('article', { name: 'Continental WinterContact' })).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Continental WinterContact' })
  await ui.click(within(history).getByRole('button', { name: 'Move the set to the trash' }))
  const confirm = await screen.findByRole('alertdialog', { name: 'Move the set to the trash?' })
  expect(confirm).toHaveTextContent(/goes to the trash with its installations/)
  await ui.click(within(confirm).getByRole('button', { name: 'Move to the trash' }))

  await waitFor(() => expect(state.calls.DeleteTyreSet).toEqual([{ id: 's2' }]))
  await ui.click(await screen.findByRole('button', { name: 'Undo' }))
  await waitFor(() => expect(state.calls.RestoreTyreSet).toEqual([{ id: 's2' }]))
})

it('corrects a set’s own details from its history, without where it is or what it ran', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(within(await screen.findByRole('article', { name: 'Continental WinterContact' })).getByRole('button', { name: 'History' }))
  await ui.click(within(await screen.findByRole('dialog', { name: 'Continental WinterContact' })).getByRole('button', { name: 'Edit set' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit tyre set' })
  expect(within(dialog).getByLabelText(/^Name/)).toHaveValue('Continental WinterContact')
  expect(within(dialog).getByLabelText(/^Made/)).toHaveValue('0523')
  expect(within(dialog).queryByText('Where are they now?')).not.toBeInTheDocument()
  expect(within(dialog).queryByLabelText('These tyres are new')).not.toBeInTheDocument()
  expect(within(dialog).getByRole('radio', { name: 'Winter' })).toBeChecked() // opened afresh from the set it edits, not the add form's summer
  await ui.clear(within(dialog).getByLabelText(/^Name/))
  await ui.type(within(dialog).getByLabelText(/^Name/), 'Continental TS 870')
  await ui.click(within(dialog).getByRole('radio', { name: 'All-season' }))
  await ui.type(within(dialog).getByLabelText(/^Size/), '205/55 R16')
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))

  await waitFor(() =>
    expect(state.calls.UpdateTyreSet).toEqual([
      { input: { id: 's2', name: 'Continental TS 870', season: 'ALL_SEASON', size: '205/55 R16', since: '2023-03-25', madeWeek: 5, madeYear: 2023 } },
    ]),
  )
})

it('swaps while the server is out of reach: kept on the device, shown done and marked, a second swap that day asked about first (#287 Q3)', async () => {
  const { ui, state } = setup([summer, winter])
  await screen.findByRole('article', { name: 'Continental WinterContact' })
  await answered()
  act(() => connectivity.failed())

  await ui.click(screen.getByRole('button', { name: 'Swap tyres' }))
  const dialog = await screen.findByRole('dialog', { name: 'Swap tyres' })
  expect(within(dialog).getByText(/You are offline\. What you save is kept on this device/)).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Swap tyres' }))

  expect(await screen.findByText('Saved on this device · syncs when online.')).toBeInTheDocument()
  expect(state.calls.SwapTyres).toBeUndefined()
  expect(outbox.changes).toMatchObject([{ entity: 'tyres', action: 'swapTyres', vehicleId: 'v1', targetId: UUID, input: { setId: 's2', replace: false, installationId: UUID } }])
  const winterCard = await screen.findByRole('article', { name: 'Continental WinterContact' })
  await waitFor(() => expect(within(winterCard).getByText('Changed · not synced')).toBeInTheDocument())
  expect(within(winterCard).getAllByText('updated once synced')).toHaveLength(3) // its distance, what it cost and its repairs (#288)
  expect(within(winterCard).getByRole('button', { name: 'Swap tyres' })).toBeInTheDocument() // on the car now
  expect(within(screen.getByRole('article', { name: 'Michelin Primacy 4' })).getByText('Changed · not synced')).toBeInTheDocument()

  // The same day again: the device asks, as the server would, then keeps the replacing swap.
  await ui.click(within(winterCard).getByRole('button', { name: 'Swap tyres' }))
  const again = await screen.findByRole('dialog', { name: 'Swap tyres' })
  await ui.click(within(again).getByRole('button', { name: 'Swap tyres' }))
  expect(await within(again).findByText(/This replaces the swap of .* \(Continental WinterContact went on\)/)).toBeInTheDocument()
  await ui.click(within(again).getByRole('button', { name: 'Replace the swap' }))
  await waitFor(() => expect(outbox.changes).toHaveLength(2))
  expect(outbox.changes[1]).toMatchObject({ action: 'swapTyres', input: { setId: 's1', replace: true } })
  await waitFor(() => expect(within(screen.getByRole('article', { name: 'Michelin Primacy 4' })).getByRole('button', { name: 'Swap tyres' })).toBeInTheDocument())
  expect(state.calls.SwapTyres).toBeUndefined()
})

it('moves a swap to the trash while offline, and Undo takes the kept change back instead of sending a restore', async () => {
  const { ui, state } = setup([summer, winter])
  await ui.click(within(await screen.findByRole('article', { name: 'Continental WinterContact' })).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Continental WinterContact' })
  await answered()
  act(() => connectivity.failed())

  await ui.click(within(history).getByRole('button', { name: /^Move to the trash: Tyre swap/ }))
  await ui.click(within(await screen.findByRole('alertdialog', { name: 'Move to the trash?' })).getByRole('button', { name: 'Move to the trash' }))
  await waitFor(() => expect(outbox.changes).toMatchObject([{ action: 'deleteTyreEntry', input: { id: 'r2' }, expectedVersion: 1 }]))
  expect(await screen.findByText('Saved on this device · syncs when online.')).toBeInTheDocument()
  await ui.click(await screen.findByRole('button', { name: 'Undo', hidden: true })) // the history dialog is still open over the page

  await waitFor(() => expect(outbox.changes).toEqual([]))
  expect(state.calls.DeleteTyreEntry).toBeUndefined()
  expect(state.calls.RestoreTyreEntry).toBeUndefined()
})

it('a swap’s line whose installation a waiting swap cut keeps its version: the cut changes the installation, not the line', async () => {
  const { ui } = setup([summer, winter])
  await screen.findByRole('article', { name: 'Continental WinterContact' })
  await answered()
  act(() => connectivity.failed())

  await ui.click(screen.getByRole('button', { name: 'Swap tyres' }))
  await ui.click(within(await screen.findByRole('dialog', { name: 'Swap tyres' })).getByRole('button', { name: 'Swap tyres' }))
  await screen.findByText('Saved on this device · syncs when online.')
  const summerCard = await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  await waitFor(() => expect(within(summerCard).getByText('Changed · not synced')).toBeInTheDocument())

  await ui.click(within(summerCard).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Michelin Primacy 4' })
  await ui.click(within(history).getByRole('button', { name: /^Move to the trash: Tyre swap/ }))
  await ui.click(within(await screen.findByRole('alertdialog', { name: 'Move to the trash?' })).getByRole('button', { name: 'Move to the trash' }))

  await waitFor(() => expect(outbox.changes).toHaveLength(2))
  expect(outbox.changes[1]).toMatchObject({ action: 'deleteTyreEntry', input: { id: 'r1' }, expectedVersion: 1 })
})

it('retires a set while offline with the day it was retired, kept as the server takes it', async () => {
  const { ui, state } = setup([summer, winter])
  const card = await screen.findByRole('article', { name: 'Continental WinterContact' })
  await answered()
  act(() => connectivity.failed())

  await ui.click(within(card).getByRole('button', { name: 'Retire' }))
  await ui.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Retire set' }))

  await waitFor(() => expect(outbox.changes).toMatchObject([{ action: 'retireTyreSet', targetId: 's2', input: { id: 's2', day: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/) }, expectedVersion: 1 }]))
  expect(state.calls.RetireTyreSet).toBeUndefined()
  expect(await screen.findByRole('heading', { name: 'Retired' })).toBeInTheDocument()
})

it('edits a line offline naming the version it was made from, and a line a waiting swap made by none', async () => {
  const { ui } = setup([summer, winter], { swapLines: { r1: { odometer: 141_000 } } })
  await screen.findByRole('article', { name: 'Continental WinterContact' })
  await answered()
  act(() => connectivity.failed())

  await ui.click(await screen.findByRole('button', { name: 'Tyre swap, Michelin Primacy 4, Mar 28, 2026' }))
  const field = within(await screen.findByRole('dialog', { name: 'Edit entry' })).getByLabelText(/^Odometer/)
  await ui.clear(field)
  await ui.type(field, '141200')
  await ui.click(within(screen.getByRole('dialog', { name: 'Edit entry' })).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(outbox.changes).toMatchObject([{ action: 'updateTyreEntry', targetId: 'r1', input: { id: 'r1', kind: 'SWAP', odometer: 141_200 }, expectedVersion: 1 }]))

  // A swap with a reading, kept here: its line is new on this device, so a change of it goes without a version.
  await ui.click(screen.getByRole('button', { name: 'Swap tyres' }))
  const swap = await screen.findByRole('dialog', { name: 'Swap tyres' })
  await ui.type(within(swap).getByLabelText(/^Odometer/), '150000')
  await ui.click(within(swap).getByRole('button', { name: 'Swap tyres' }))
  await waitFor(() => expect(outbox.changes).toHaveLength(2))
  const log = screen.getByRole('region', { name: 'Tyre log' })
  await ui.click(await within(log).findByRole('button', { name: /^Tyre swap, Continental WinterContact, (?!Nov 15)/ })) // today's, from the waiting swap
  const today = within(await screen.findByRole('dialog', { name: 'Edit entry' })).getByLabelText(/^Odometer/)
  expect(today).toHaveValue('150000')
  await ui.clear(today)
  await ui.type(today, '150100')
  await ui.click(within(screen.getByRole('dialog', { name: 'Edit entry' })).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(outbox.changes).toHaveLength(3))
  expect(outbox.changes[2]).toMatchObject({ action: 'updateTyreEntry', input: { odometer: 150_100 }, expectedVersion: null })
  expect(outbox.changes[2].input?.id).toBe(outbox.changes[1].input?.installationId) // the swap's line has its installation's id
})

it('shows a note line the device made for a waiting change without its Edit and bin: the server names that line, so it is changed once synced', async () => {
  const { ui } = setup([summer, winter])
  await screen.findByRole('article', { name: 'Continental WinterContact' })
  await answered()
  act(() => connectivity.failed())

  await ui.click(within(screen.getByRole('article', { name: 'Continental WinterContact' })).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Continental WinterContact' })
  await ui.click(within(history).getByRole('button', { name: 'Add an installation' }))
  const add = await screen.findByRole('dialog', { name: 'Add an installation' })
  await typeDate(ui, await findDateField(/First day on the car/, add), '2024-11-09')
  await typeDate(ui, await findDateField(/Day it came off/, add), '2025-03-29')
  await ui.type(within(add).getByLabelText(/^Odometer on the day it came off/), '124000')
  await ui.click(within(add).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(outbox.changes).toMatchObject([{ action: 'addTyreInstallation', input: { odometerUntil: 124_000 } }]))

  // The page is behind the history dialog: read through it.
  const log = screen.getByRole('region', { name: 'Tyre log', hidden: true })
  await waitFor(() => expect(log).toHaveTextContent('124,000 km'))
  expect(within(log).getByRole('button', { name: 'Tyre swap, Continental WinterContact, Nov 9, 2024', hidden: true })).toBeInTheDocument() // the installation's own line
  expect(within(log).queryByRole('button', { name: /^Note, Continental WinterContact, Mar 29, 2025/, hidden: true })).not.toBeInTheDocument()
  expect(within(log).queryByRole('button', { name: 'Move to the trash: Note, Mar 29, 2025', hidden: true })).not.toBeInTheDocument()
})

it('shows no tyres, not an endless wait, to someone who may see the vehicle but not its logs (the server answers null)', async () => {
  setup([summer, winter])
  server.use(graphql.query('VehicleTyres', () => HttpResponse.json({ data: { vehicleTyres: null } })))

  expect(await screen.findByRole('heading', { name: 'No tyres yet' })).toBeInTheDocument()
  expect(screen.queryByRole('status', { name: /Loading/ })).not.toBeInTheDocument()
})

it('shows a line of no set with its kind and note, and the reading typed with it', async () => {
  setup([summer, winter], { entries: [{ id: 'e2', kind: 'STORAGE_FEE', setId: null, day: '2025-06-01', odometer: 133_000, note: 'Winter tyres stored' }] })

  const line = await screen.findByRole('button', { name: 'Storage fee, Jun 1, 2025' })
  expect(line).toHaveTextContent('133,000 km · Winter tyres stored')
})

const expenseLine = { id: 'e1', kind: 'PUNCTURE_REPAIRED', setId: 's1', day: '2026-06-01', odometer: 143_000, note: null, expenseId: 'x1', amount: 25_000, currency: 'HUF', category: 'Tyres' }

it('adds a line with an amount: an expense in the vehicle’s currency and the Tyres category, titled by what was done and the set (#288)', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(await screen.findByRole('button', { name: 'Add entry' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add entry' })
  expect(within(dialog).getByText('Optional. An amount makes an expense on the Expenses tab.')).toBeInTheDocument()
  expect(within(dialog).getByLabelText(/^Category/)).toHaveValue('Tyres')
  await ui.type(within(dialog).getByLabelText(/^Paid/), '25000')
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(state.calls.AddTyreEntry).toHaveLength(1))
  expect(state.calls.AddTyreEntry[0].input).toMatchObject({
    kind: 'PUNCTURE_REPAIRED', fee: { amount: 25_000, currency: 'HUF', category: 'Tyres', title: 'Puncture repaired: Michelin Primacy 4', expenseId: UUID },
  })
})

it('offers no amount for a note', async () => {
  const { ui } = setup([summer, winter])

  await ui.click(await screen.findByRole('button', { name: 'Add entry' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add entry' })
  await ui.click(within(dialog).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: 'Note' }))
  expect(within(dialog).queryByLabelText(/^Paid/)).not.toBeInTheDocument()
  expect(within(dialog).queryByLabelText(/^Category/)).not.toBeInTheDocument()
})

it('shows a line’s amount, and its bin asks whether the expense goes too, saying what stays (#288)', async () => {
  const { ui, state } = setup([summer, winter], { entries: [expenseLine] })

  const line = await screen.findByRole('button', { name: /^Puncture repaired, Michelin Primacy 4, Jun 1, 2026, HUF/ })
  expect(line).toHaveTextContent(/25,000/)
  await ui.click(screen.getByRole('button', { name: 'Move to the trash: Puncture repaired, Jun 1, 2026' }))
  const confirm = await screen.findByRole('alertdialog', { name: 'Move to the trash?' })
  const box = within(confirm).getByRole('checkbox', { name: /^Also move its expense \(HUF\s25,000(\.00)?\) to the trash$/ })
  expect(box).toBeChecked()
  expect(confirm).toHaveTextContent(/With the expense in the trash too, the reading of 143,000 km no longer counts/)
  await ui.click(box)
  expect(confirm).toHaveTextContent('Unticked, the expense stays on the Expenses tab, with the reading.')
  await ui.click(within(confirm).getByRole('button', { name: 'Move to the trash' }))

  await waitFor(() => expect(state.calls.DeleteTyreEntry).toEqual([{ input: { id: 'e1', withExpense: false } }]))
})

it('edits a line with an expense: its day and reading are said to be the expense’s too, and an emptied amount sends none (#288)', async () => {
  const { ui, state } = setup([summer, winter], { entries: [expenseLine] })

  await ui.click(await screen.findByRole('button', { name: /^Puncture repaired, Michelin Primacy 4, Jun 1, 2026/ }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit entry' })
  expect(within(dialog).getByText('Its expense and its reading carry the same day.')).toBeInTheDocument()
  expect(within(dialog).getByText(/^One of the car's readings\. The expense shows the same one\./)).toBeInTheDocument()
  expect(within(dialog).getByText(/Emptying the amount moves the expense to the trash\./)).toBeInTheDocument()
  await ui.clear(within(dialog).getByLabelText(/^Paid/))
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(state.calls.UpdateTyreEntry).toHaveLength(1))
  expect(state.calls.UpdateTyreEntry[0].input).toMatchObject({ id: 'e1', fee: { amount: null } })
})

it('takes a swap’s fee, titled by the swap and the set going on (#288)', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(within(await screen.findByRole('article', { name: 'Michelin Primacy 4' })).getByRole('button', { name: 'Swap tyres' }))
  const dialog = await screen.findByRole('dialog', { name: 'Swap tyres' })
  expect(within(dialog).getByText(/^Every swap is a Tyre swap line of the tyre log/)).toBeInTheDocument()
  await typeDate(ui, await findDateField(/^Day/, dialog), '2026-06-01')
  await ui.type(within(dialog).getByLabelText(/^Paid for the swap/), '8000')
  await ui.click(within(dialog).getByRole('button', { name: 'Swap tyres' }))

  await waitFor(() => expect(state.calls.SwapTyres).toHaveLength(1))
  expect(state.calls.SwapTyres[0].input).toMatchObject({ fee: { amount: 8000, currency: 'HUF', title: 'Tyre swap: Continental WinterContact', expenseId: UUID } })
})

it('asks a used set’s price for the day it was bought, starting from in use since (#288)', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(await screen.findByRole('button', { name: 'Add a tyre set' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add a tyre set' })
  await ui.type(within(dialog).getByLabelText(/^Name/), 'Nokian')
  await typeDate(ui, await findDateField(/In use since/, dialog), '2024-05-01')
  expect(within(dialog).queryByRole('group', { name: /^Bought on/ })).not.toBeInTheDocument()
  await ui.type(within(dialog).getByLabelText(/^Price/), '120000')
  expect(await findDateField(/^Bought on/, dialog)).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Save set' }))

  await waitFor(() => expect(state.calls.AddTyreSet).toHaveLength(1))
  expect(state.calls.AddTyreSet[0].input).toMatchObject({
    name: 'Nokian', price: { amount: 120_000, currency: 'HUF', title: 'Bought: Nokian', expenseId: UUID }, boughtOn: '2024-05-01', boughtEntryId: UUID,
  })
})

it('marks an expense a line made on the Expenses tab, and its edit dialog opens the line in the tyre log (#288)', async () => {
  stubViewport('desktop')
  const vehicle = fakeVehicle()
  const expenses = fakeExpenseBackend(vehicle, [
    fakeExpense({ id: 'x1', title: 'Puncture repaired: Michelin Primacy 4', category: 'Tyres', canDelete: true, tyreEntry: { __typename: 'TyreEntryRef', id: 'e1', kind: 'PUNCTURE_REPAIRED', setName: 'Michelin Primacy 4', trashed: false } }),
  ])
  const tyres = fakeTyres([summer, winter], [expenseLine])
  server.use(sessionHandler('NONE', () => null), healthHandler, ...expenses.handlers, ...tyres.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=expenses')
  const ui = userEvent.setup()

  const row = (await screen.findByText('Puncture repaired: Michelin Primacy 4')).closest('[role="row"]') as HTMLElement
  expect(row).toHaveTextContent('Tyre log')
  await ui.click(within(row).getByRole('button', { name: /^Delete/ }))
  expect(await screen.findByRole('alertdialog')).toHaveTextContent('Its line stays in the tyre log, without the amount.')
  await ui.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Cancel' }))

  await ui.click(within(row).getByRole('button', { name: /^Edit/ }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit expense' })
  expect(await within(dialog).findByText(/Puncture repaired, Michelin Primacy 4\. The date and the odometer are the same there/)).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('link', { name: 'Open in the tyre log' }))

  expect(await screen.findByRole('dialog', { name: 'Edit entry' })).toBeInTheDocument()
  expect(screen.getByRole('tab', { name: 'Tyres', selected: true, hidden: true })).toBeInTheDocument() // behind the dialog
})

it('says an expense restored on its own leaves its tyre log line in the trash (#288)', async () => {
  stubViewport('desktop')
  const vehicle = fakeVehicle()
  const expenses = fakeExpenseBackend(vehicle, [])
  expenses.state.trash = [fakeExpense({ id: 'x1', title: 'Puncture repaired: Michelin Primacy 4', deletedAt: '2026-10-01T08:00:00Z', canDelete: true,
    tyreEntry: { __typename: 'TyreEntryRef', id: 'e1', kind: 'PUNCTURE_REPAIRED', setName: 'Michelin Primacy 4', trashed: true } })]
  server.use(sessionHandler('NONE', () => null), healthHandler, ...expenses.handlers)
  renderWithApollo(<App />, '/trash?tab=expenses')
  const ui = userEvent.setup()

  await ui.click(await screen.findByRole('button', { name: /^Restore Puncture repaired/ }))
  expect(await screen.findByText('Restored. Its tyre log line stays in the trash.')).toBeInTheDocument()
})

it('shows what each set cost and its repairs, and what the tyres cost altogether, per set and for the lines of no set (#288)', async () => {
  setup([{ ...summer, spent: [{ currency: 'HUF', amount: 120_000 }, { currency: 'EUR', amount: 40 }], repairs: 2 }, winter], {
    entries: [{ id: 'n1', kind: 'OTHER', setId: null, day: '2026-05-01', odometer: null, note: null, expenseId: 'x', amount: 8, currency: 'EUR' }],
  })

  const card = await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  expect(within(card).getByText('Spent').nextSibling).toHaveTextContent(/HUF.*120,000.*·.*€40\.00/)
  expect(within(card).getByText('Repairs').nextSibling).toHaveTextContent('2')
  expect(within(screen.getByRole('article', { name: 'Continental WinterContact' })).getByText('Spent').nextSibling).toHaveTextContent('–')
  const block = screen.getByRole('heading', { name: 'Spent on tyres' }).parentElement!
  expect(within(block).getByText('All').nextSibling).toHaveTextContent(/HUF.*120,000.*·.*€48\.00/)
  expect(within(block).getByText('Continental WinterContact').nextSibling).toHaveTextContent('–') // every set has its row
  expect(within(block).getByText('Not tied to a set').nextSibling).toHaveTextContent('€8.00')
  expect(within(block).getByText("The amounts are the expenses linked to the tyre log's lines.")).toBeInTheDocument()
})

it('marks the costs a repair kept on the device puts out of date, and only those (#288)', async () => {
  const { ui, state } = setup([{ ...summer, spent: [{ currency: 'EUR', amount: 40 }], repairs: 1 }, { ...winter, spent: [{ currency: 'EUR', amount: 15 }] }])
  await screen.findByRole('article', { name: 'Continental WinterContact' })
  await answered()
  act(() => connectivity.failed())

  await ui.click(screen.getByRole('button', { name: 'Add entry' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add entry' })
  await typeDate(ui, await findDateField(/^Day/, dialog), '2026-06-01')
  await ui.click(within(dialog).getByRole('button', { name: 'Save' })) // a puncture on the set on the car, no amount

  expect(await screen.findByText('Saved on this device · syncs when online.')).toBeInTheDocument()
  expect(state.calls.AddTyreEntry).toBeUndefined()
  const summerCard = screen.getByRole('article', { name: 'Michelin Primacy 4' })
  await waitFor(() => expect(within(summerCard).getByText('Repairs').nextSibling).toHaveTextContent('updated once synced'))
  expect(within(summerCard).getByText('Spent').nextSibling).toHaveTextContent('updated once synced')
  const winterCard = screen.getByRole('article', { name: 'Continental WinterContact' })
  expect(within(winterCard).getByText('Spent').nextSibling).toHaveTextContent('€15.00') // its lines are as they were
  const block = screen.getByRole('heading', { name: 'Spent on tyres' }).parentElement!
  expect(within(block).getByText('All').nextSibling).toHaveTextContent('updated once synced')
  expect(within(block).getByText('Michelin Primacy 4').nextSibling).toHaveTextContent('updated once synced')
  expect(within(block).getByText('Continental WinterContact').nextSibling).toHaveTextContent('€15.00')
  expect(within(block).queryByText('Not tied to a set')).not.toBeInTheDocument()
})

it('lists a trashed tyre and a trashed time on the car on the Trash page, and restores each with its own mutation (#289)', async () => {
  stubViewport('desktop')
  const calls: Record<string, unknown[]> = {}
  const base = { __typename: 'TyreTrashRow', entryKind: null, setName: 'Michelin Primacy 4', installationCount: 0, version: 2, withExpense: false, canDelete: true, vehicle: { __typename: 'Vehicle', id: 'v1', name: 'Octavia' } }
  const rows = [
    { ...base, kind: 'TYRE', id: 't9', label: 'S2', corner: null, day: null, until: null, deletedAt: '2026-11-09T21:14:00Z' },
    { ...base, kind: 'PLACE', id: 'p9', label: 'S3', corner: 'REAR_LEFT', day: '2026-03-28', until: '2026-06-01', deletedAt: '2026-11-02T18:02:00Z' },
  ]
  const answer = { __typename: 'VehicleTyres', vehicleId: 'v1', hasReadings: false, expenseCategory: null, spentAll: [], spentNoSet: [], readings: [], entries: [], sets: [] }
  server.use(
    sessionHandler('NONE', () => null),
    healthHandler,
    graphql.query('TyreTrash', () => HttpResponse.json({ data: { tyreTrash: rows, tyreTrashCount: 2, tyreTrashDeletableCount: 2 } })),
    graphql.mutation('RestoreTyre', ({ variables }) => {
      ;(calls.RestoreTyre ??= []).push(variables)
      return HttpResponse.json({ data: { restoreTyre: answer } })
    }),
    graphql.mutation('RestoreTyrePlace', ({ variables }) => {
      ;(calls.RestoreTyrePlace ??= []).push(variables)
      return HttpResponse.json({ data: { restoreTyrePlace: answer } })
    }),
  )
  renderWithApollo(<App />, '/trash?tab=tyres')
  const ui = userEvent.setup()

  expect(await screen.findByText('with its times on the car and lines')).toBeInTheDocument()
  expect(screen.getByText('S3: on the car, rear left')).toBeInTheDocument()
  await ui.click(screen.getByRole('button', { name: 'Restore Tyre S2' }))
  await waitFor(() => expect(calls.RestoreTyre).toEqual([{ id: 't9' }]))
  await ui.click(screen.getByRole('button', { name: 'Restore S3: on the car, rear left' }))
  await waitFor(() => expect(calls.RestoreTyrePlace).toEqual([{ id: 'p9' }]))
})
