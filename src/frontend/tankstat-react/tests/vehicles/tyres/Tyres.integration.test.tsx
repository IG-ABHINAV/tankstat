import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse } from 'msw'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { graphql } from '../../support/graphql.ts'
import { answered, server } from '../../support/server.ts'
import { connectivity } from '../../../src/offline/connectivity.ts'
import { outbox } from '../../../src/offline/outbox.ts'
import { fakeExpenseBackend, fakeVehicle, gqlError, healthHandler, renderWithApollo, sessionHandler, stubViewport } from '../../support/mocks.tsx'
import { UUID } from '../../support/ids.ts'
import { fakeTyres, tyreSummer, tyreWinter, type FakeTyreSet } from '../../support/tyres.ts'
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

function setup(sets: Set[], { vehicle = fakeVehicle(), days = [] as Parameters<typeof fakeTyres>[1] } = {}) {
  stubViewport('desktop')
  const expenses = fakeExpenseBackend(vehicle, [])
  const tyres = fakeTyres(sets, days)
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
    vehicleId: 'v1', name: 'Michelin Primacy 4', season: 'SUMMER', since: '2023-03-25', fitted: true, roughDistance: 32_900, replace: false, id: UUID, stretchId: UUID,
  })
})

it('shows each set with its distance, age and stretch, and the tyre log a line per day with the reading typed there', async () => {
  setup([summer, winter], { days: [{ day: '2026-03-28', odometer: 141_000, note: null }] })

  const onCar = await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  expect(within(onCar).getByText('≈ 40,600 km')).toBeInTheDocument()
  expect(within(onCar).getByText(/On since .* · 7,700 km in this stretch · pair 1 at the front/)).toBeInTheDocument()
  expect(within(screen.getByRole('article', { name: 'Continental WinterContact' })).getByText(/In storage · last: pair 1 at the front/)).toBeInTheDocument()
  const log = screen.getByRole('region', { name: 'Tyre log' })
  expect(within(log).getByText(/141,000 km · reading typed here/)).toBeInTheDocument()
  expect(within(log).getByText(/≈ 135,9\d\d km|before the fuel log/)).toBeInTheDocument() // 15 Nov 2025 is before the readings here
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
  expect(state.calls.SwapTyres[0].input).toMatchObject({ setId: 's2', day: '2026-06-01', pairOneFront: false, replace: false, stretchId: UUID })
  expect(state.calls.SwapTyres[1].input).toMatchObject({ replace: true, stretchId: (state.calls.SwapTyres[0].input as { stretchId: string }).stretchId })
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
    { day: '2025-03-29', setId: 's1', odometer: 128_150, stretchId: UUID },
    { day: '2025-11-15', setId: 's1', odometer: null, stretchId: UUID },
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

it('moves a stretch to the trash from the history, saying the day’s reading stays, and takes it back with Undo', async () => {
  const { ui, state } = setup([summer, winter], { days: [{ day: '2026-03-28', odometer: 141_000, note: null }] })

  const card = await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  await ui.click(within(card).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Michelin Primacy 4' })
  await ui.click(within(history).getByRole('button', { name: 'Move to the trash' }))
  const confirm = await screen.findByRole('alertdialog', { name: 'Move to the trash?' })
  expect(within(confirm).getByText(/The reading typed that day \(141,000 km\) stays/)).toBeInTheDocument()
  await ui.click(within(confirm).getByRole('button', { name: 'Move to the trash' }))

  await waitFor(() => expect(state.calls.DeleteTyreStretch).toEqual([{ id: 'r1' }]))
  await ui.click(await screen.findByRole('button', { name: 'Undo', hidden: true })) // the history dialog is still open over the page
  await waitFor(() => expect(state.calls.RestoreTyreStretch).toEqual([{ id: 'r1' }]))
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

it('lists trashed sets and stretches on the Trash page, says why a restore is refused, and restores a set', async () => {
  stubViewport('desktop')
  const calls: Record<string, unknown[]> = {}
  const rows = [
    { __typename: 'TyreTrashRow', kind: 'STRETCH', id: 'r9', setName: 'Continental WinterContact', from: '2025-11-15', until: '2026-03-28', stretchCount: 0, deletedAt: '2026-11-09T21:14:00Z', version: 2, canDelete: true, vehicle: { __typename: 'Vehicle', id: 'v1', name: 'Octavia' } },
    { __typename: 'TyreTrashRow', kind: 'SET', id: 's9', setName: 'Pirelli Cinturato', from: null, until: null, stretchCount: 6, deletedAt: '2026-11-02T18:02:00Z', version: 3, canDelete: true, vehicle: { __typename: 'Vehicle', id: 'v1', name: 'Octavia' } },
  ]
  server.use(
    sessionHandler('NONE', () => null),
    healthHandler,
    graphql.query('TyreTrash', ({ variables }) => {
      ;(calls.TyreTrash ??= []).push(variables)
      return HttpResponse.json({ data: { tyreTrash: rows, tyreTrashCount: rows.length, tyreTrashDeletableCount: rows.length } })
    }),
    graphql.mutation('RestoreTyreStretch', () =>
      HttpResponse.json(gqlError('refused', 'VALIDATION_FAILED', 'tyres.restoreConflict', { set: 'Michelin Primacy 4', from: '2026-03-20', until: '2026-11-10' })),
    ),
    graphql.mutation('RestoreTyreSet', ({ variables }) => {
      ;(calls.RestoreTyreSet ??= []).push(variables)
      return HttpResponse.json({ data: { restoreTyreSet: { __typename: 'VehicleTyres', vehicleId: 'v1', hasReadings: false, readings: [], days: [], sets: [] } } })
    }),
  )
  const ui = userEvent.setup()
  renderWithApollo(<App />, '/trash?tab=tyres')

  expect(await screen.findByRole('rowheader', { name: 'Continental WinterContact: on the car' })).toBeInTheDocument()
  expect(screen.getByText('with 6 stretches')).toBeInTheDocument()
  expect(calls.TyreTrash[0]).toMatchObject({ orderBy: 'DELETED_AT', direction: 'DESC', skip: 0 })

  await ui.click(screen.getByRole('button', { name: 'Restore Continental WinterContact: on the car' }))
  expect(await screen.findByText('It cannot be restored as it is: Michelin Primacy 4 is on the car then, from 2026-03-20.')).toBeInTheDocument()

  await ui.click(screen.getByRole('button', { name: 'Restore Set: Pirelli Cinturato' }))
  await waitFor(() => expect(calls.RestoreTyreSet).toEqual([{ id: 's9' }]))
})

it('changes the positions of the set on the car: front and rear exchanged from a day', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(await screen.findByRole('button', { name: 'Change positions' }))
  const dialog = await screen.findByRole('dialog', { name: 'Change positions' })
  expect(within(dialog).getByText(/Now: pair 1 at the front\./)).toBeInTheDocument()
  expect(within(dialog).getByRole('radio', { name: 'Front ↔ rear' })).toBeChecked()
  await ui.click(within(dialog).getByRole('button', { name: 'Save positions' }))

  await waitFor(() => expect(state.calls.ChangeTyrePositions).toHaveLength(1))
  expect(state.calls.ChangeTyrePositions[0].input).toMatchObject({ setId: 's1', pairOneFront: false, stretchId: UUID })
})

it('adds a stretch to a set’s history with its first day’s reading, and edits one', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(within(await screen.findByRole('article', { name: 'Continental WinterContact' })).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Continental WinterContact' })
  await ui.click(within(history).getByRole('button', { name: 'Add a stretch' }))
  const add = await screen.findByRole('dialog', { name: 'Add a stretch' })
  await typeDate(ui, await findDateField(/First day on the car/, add), '2024-11-09')
  await typeDate(ui, await findDateField(/Day it came off/, add), '2025-03-29')
  await ui.type(within(add).getByLabelText(/^Odometer on the first day/), '118500')
  await ui.click(within(add).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(state.calls.AddTyreStretch).toHaveLength(1))
  expect(state.calls.AddTyreStretch[0].input).toMatchObject({ id: UUID, setId: 's2', from: '2024-11-09', until: '2025-03-29', pairOneFront: null, odometerFrom: 118_500 })

  await ui.click(within(history).getByRole('button', { name: 'Edit' }))
  const edit = await screen.findByRole('dialog', { name: 'Edit stretch' })
  await ui.click(within(edit).getByRole('combobox', { name: 'Pairs' }))
  await ui.click(screen.getByRole('option', { name: 'pair 2 at the front' }))
  await ui.click(within(edit).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(state.calls.UpdateTyreStretch).toHaveLength(1))
  expect(state.calls.UpdateTyreStretch[0].input).toMatchObject({ id: 'r2', from: '2025-11-15', until: '2026-03-28', pairOneFront: false, odometerFrom: null })
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

it('edits the reading of a tyre log day, and removes it for good after asking', async () => {
  const { ui, state } = setup([summer, winter], { days: [{ day: '2026-03-28', odometer: 141_000, note: 'At the garage' }] })

  await ui.click(await screen.findByRole('button', { name: /^Edit the reading and note of/ }))
  const dialog = await screen.findByRole('dialog', { name: /^Reading of/ })
  expect(within(dialog).getByText(/Between 140,000 km on .* and 147,000 km on .*\./)).toBeInTheDocument()
  const field = within(dialog).getByLabelText(/^Odometer/)
  await ui.clear(field)
  await ui.type(field, '141200')
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(state.calls.SetTyreLogDay).toEqual([{ input: { vehicleId: 'v1', day: '2026-03-28', odometer: 141_200, note: 'At the garage', id: 'd0' } }])) // the line it was made on

  await ui.click(await screen.findByRole('button', { name: /^Edit the reading and note of/ }))
  await ui.click(within(await screen.findByRole('dialog', { name: /^Reading of/ })).getByRole('button', { name: 'Remove the reading' }))
  await ui.click(within(await screen.findByRole('alertdialog', { name: 'Remove the reading?' })).getByRole('button', { name: 'Remove the reading' }))
  await waitFor(() => expect(state.calls.SetTyreLogDay).toHaveLength(2))
  expect(state.calls.SetTyreLogDay[1]).toEqual({ input: { vehicleId: 'v1', day: '2026-03-28', odometer: null, note: 'At the garage', id: 'd0' } })
})

it('moves a set to the trash from its history, with Undo', async () => {
  const { ui, state } = setup([summer, winter])

  await ui.click(within(await screen.findByRole('article', { name: 'Continental WinterContact' })).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Continental WinterContact' })
  await ui.click(within(history).getByRole('button', { name: 'Move the set to the trash' }))
  const confirm = await screen.findByRole('alertdialog', { name: 'Move the set to the trash?' })
  expect(confirm).toHaveTextContent(/goes to the trash with its stretches/)
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
  expect(outbox.changes).toMatchObject([{ entity: 'tyres', action: 'swapTyres', vehicleId: 'v1', targetId: UUID, input: { setId: 's2', replace: false, stretchId: UUID } }])
  const winterCard = await screen.findByRole('article', { name: 'Continental WinterContact' })
  await waitFor(() => expect(within(winterCard).getByText('Changed · not synced')).toBeInTheDocument())
  expect(within(winterCard).getByText('updated once synced')).toBeInTheDocument()
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

it('moves a stretch to the trash while offline, and Undo takes the kept change back instead of sending a restore', async () => {
  const { ui, state } = setup([summer, winter])
  await ui.click(within(await screen.findByRole('article', { name: 'Continental WinterContact' })).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Continental WinterContact' })
  await answered()
  act(() => connectivity.failed())

  await ui.click(within(history).getByRole('button', { name: 'Move to the trash' }))
  await ui.click(within(await screen.findByRole('alertdialog', { name: 'Move to the trash?' })).getByRole('button', { name: 'Move to the trash' }))
  await waitFor(() => expect(outbox.changes).toMatchObject([{ action: 'deleteTyreStretch', input: { id: 'r2' }, expectedVersion: 1 }]))
  expect(await screen.findByText('Saved on this device · syncs when online.')).toBeInTheDocument()
  await ui.click(await screen.findByRole('button', { name: 'Undo', hidden: true })) // the history dialog is still open over the page

  await waitFor(() => expect(outbox.changes).toEqual([]))
  expect(state.calls.DeleteTyreStretch).toBeUndefined()
  expect(state.calls.RestoreTyreStretch).toBeUndefined()
})

it('a change of the stretch a waiting swap cut is kept without the version the server will have moved on from', async () => {
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
  await ui.click(within(history).getByRole('button', { name: 'Move to the trash' }))
  await ui.click(within(await screen.findByRole('alertdialog', { name: 'Move to the trash?' })).getByRole('button', { name: 'Move to the trash' }))

  await waitFor(() => expect(outbox.changes).toHaveLength(2))
  expect(outbox.changes[1]).toMatchObject({ action: 'deleteTyreStretch', input: { id: 'r1' }, expectedVersion: null })
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

it('edits a day’s reading offline naming its line and the version it was made from, and a line a waiting swap wrote by neither', async () => {
  const { ui } = setup([summer, winter], { days: [{ day: '2026-03-28', odometer: 141_000, note: null }] })
  await screen.findByRole('article', { name: 'Continental WinterContact' })
  await answered()
  act(() => connectivity.failed())

  await ui.click(await screen.findByRole('button', { name: /^Edit the reading and note of Mar 28/ }))
  const field = within(await screen.findByRole('dialog', { name: /^Reading of/ })).getByLabelText(/^Odometer/)
  await ui.clear(field)
  await ui.type(field, '141200')
  await ui.click(within(screen.getByRole('dialog', { name: /^Reading of/ })).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(outbox.changes).toMatchObject([{ action: 'setTyreLogDay', input: { id: 'd0', odometer: 141_200 }, expectedVersion: 1 }]))

  // A swap with a reading, kept here: its line has no id the server knows yet.
  await ui.click(screen.getByRole('button', { name: 'Swap tyres' }))
  const swap = await screen.findByRole('dialog', { name: 'Swap tyres' })
  await ui.type(within(swap).getByLabelText(/^Odometer/), '150000')
  await ui.click(within(swap).getByRole('button', { name: 'Swap tyres' }))
  await waitFor(() => expect(outbox.changes).toHaveLength(2))
  const edits = await screen.findAllByRole('button', { name: /^Edit the reading and note of/ })
  await ui.click(edits[0]) // the newest line: today's, from the waiting swap
  const today = within(await screen.findByRole('dialog', { name: /^Reading of/ })).getByLabelText(/^Odometer/)
  await ui.clear(today)
  await ui.type(today, '150100')
  await ui.click(within(screen.getByRole('dialog', { name: /^Reading of/ })).getByRole('button', { name: 'Save' }))
  await waitFor(() => expect(outbox.changes).toHaveLength(3))
  expect(outbox.changes[2]).toMatchObject({ action: 'setTyreLogDay', input: { odometer: 150_100 }, expectedVersion: null })
  expect(outbox.changes[2].input).not.toHaveProperty('id')
})

it('shows no tyres, not an endless wait, to someone who may see the vehicle but not its logs (the server answers null)', async () => {
  setup([summer, winter])
  server.use(graphql.query('VehicleTyres', () => HttpResponse.json({ data: { vehicleTyres: null } })))

  expect(await screen.findByRole('heading', { name: 'No tyres yet' })).toBeInTheDocument()
  expect(screen.queryByRole('status', { name: /Loading/ })).not.toBeInTheDocument()
})

it('shows a day’s note on its line, and a line left with only a note can be edited or cleared, which removes it', async () => {
  const { ui, state } = setup([summer, winter], {
    days: [
      { day: '2026-03-28', odometer: 141_000, note: 'At the garage' },
      { day: '2025-06-01', odometer: null, note: 'Pressure checked' },
    ],
  })

  const log = await screen.findByRole('region', { name: 'Tyre log' })
  const swapLine = within(log).getByText('Michelin Primacy 4: on the car').closest('li')!
  expect(swapLine).toHaveTextContent('At the garage')
  const noteLine = within(log).getByText('Pressure checked').closest('li')!
  expect(noteLine).toHaveTextContent(/^Note/)
  expect(noteLine).not.toHaveTextContent('reading typed here')

  await ui.click(within(noteLine as HTMLElement).getByRole('button', { name: /^Edit the reading and note of Jun 1, 2025/ }))
  const dialog = await screen.findByRole('dialog', { name: /^Reading of/ })
  expect(within(dialog).getByLabelText(/^Odometer/)).toHaveValue('')
  expect(within(dialog).queryByRole('button', { name: 'Remove the reading' })).not.toBeInTheDocument() // there is none
  expect(within(dialog).getByText('With neither a reading nor a note, the line is removed.')).toBeInTheDocument()
  await ui.clear(within(dialog).getByLabelText(/^Note/))
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(state.calls.SetTyreLogDay).toEqual([{ input: { vehicleId: 'v1', day: '2025-06-01', odometer: null, note: null, id: 'd1' } }])) // the line it was made on
})
