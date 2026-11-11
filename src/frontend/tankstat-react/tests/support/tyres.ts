import { HttpResponse } from 'msw'
import { graphql } from './graphql.ts'
import { gqlError } from './mocks.tsx'

// A vehicle's tyres (#287) as the server answers them; the fake records what was sent and answers with the state it was given.
export type FakeTyreInstallation = { id: string; setId: string; from: string; until: string | null; pairOneFront: boolean | null; distance: number; measure?: string }
export type FakeTyreSet = { id: string; name: string; season: string; roughDistance: number; onTheCar: boolean; isRetired?: boolean; installations: FakeTyreInstallation[]; suggestion?: { distance: number; until: string; measuredBefore: number } }

export const tyreSummer: FakeTyreSet = {
  id: 's1', name: 'Michelin Primacy 4', season: 'SUMMER', roughDistance: 32_900, onTheCar: true,
  installations: [{ id: 'r1', setId: 's1', from: '2026-03-28', until: null, pairOneFront: true, distance: 7_700 }],
}
export const tyreWinter: FakeTyreSet = {
  id: 's2', name: 'Continental WinterContact', season: 'WINTER', roughDistance: 20_200, onTheCar: false,
  installations: [{ id: 'r2', setId: 's2', from: '2025-11-15', until: '2026-03-28', pairOneFront: true, distance: 4_512 }],
}

/** The server's answers as the tab reads them; the fake only records what was sent and answers with the state it was given. */
export function fakeTyres(initial: FakeTyreSet[], days: { day: string; odometer: number | null; note: string | null }[] = []) {
  const state = { sets: initial, days, calls: {} as Record<string, Record<string, unknown>[]>, failNext: [] as { key: string; args?: Record<string, unknown> }[] }
  const answer = () => ({
    __typename: 'VehicleTyres',
    vehicleId: 'v1',
    hasReadings: true,
    readings: [
      { __typename: 'OdometerPoint', date: '2026-03-01', value: 140_000 },
      { __typename: 'OdometerPoint', date: '2026-09-01', value: 147_000 },
    ],
    days: state.days.map((d, i) => ({ __typename: 'TyreLogDay', id: `d${i}`, version: 1, ...d })),
    sets: state.sets.map((s) => ({
      __typename: 'TyreSetInfo',
      onTheCar: s.onTheCar,
      set: {
        __typename: 'TyreSet', id: s.id, vehicleId: 'v1', name: s.name, season: s.season, size: null, since: '2023-03-25', madeWeek: 5, madeYear: 2023,
        roughDistance: s.roughDistance, roughUntil: '2026-03-28', retiredOn: s.isRetired ? '2026-10-01' : null, isRetired: s.isRetired ?? false, version: 1,
      },
      figures: {
        __typename: 'TyreSetFigures', distance: s.roughDistance + s.installations.reduce((a, x) => a + x.distance, 0), approximate: true,
        measured: s.installations.reduce((a, x) => a + x.distance, 0), pairOneFront: s.installations.reduce((a, x) => a + x.distance, 0), pairOneRear: 0, pairsUnknown: 0,
        suggestion: s.suggestion ? { __typename: 'RoughSuggestion', ...s.suggestion } : null,
      },
      installations: s.installations.map((x) => ({
        __typename: 'TyreInstallationInfo',
        installation: { __typename: 'TyreInstallation', id: x.id, setId: x.setId, from: x.from, until: x.until, pairOneFront: x.pairOneFront, version: 1 },
        distance: { __typename: 'InstallationDistance', distance: x.distance, approximate: false, measure: x.measure ?? 'MEASURED' },
      })),
    })),
  })
  const mutation = (name: string, field: string) =>
    graphql.mutation(name, ({ variables }) => {
      ;(state.calls[name] ??= []).push(variables as Record<string, unknown>)
      const fail = state.failNext.shift()
      if (fail) return HttpResponse.json(gqlError('refused', 'VALIDATION_FAILED', fail.key, fail.args))
      return HttpResponse.json({ data: { [field]: answer() } })
    })
  const handlers = [
    graphql.query('VehicleTyres', () => HttpResponse.json({ data: { vehicleTyres: answer() } })),
    mutation('AddTyreSet', 'addTyreSet'),
    mutation('SwapTyres', 'swapTyres'),
    mutation('ChangeTyrePositions', 'changeTyrePositions'),
    mutation('AddPastTyreSwaps', 'addPastTyreSwaps'),
    mutation('DeleteTyreInstallation', 'deleteTyreInstallation'),
    mutation('RestoreTyreInstallation', 'restoreTyreInstallation'),
    mutation('SetTyreLogDay', 'setTyreLogDay'),
    mutation('RetireTyreSet', 'retireTyreSet'),
    mutation('AddTyreInstallation', 'addTyreInstallation'),
    mutation('UpdateTyreInstallation', 'updateTyreInstallation'),
    mutation('SetTyreRoughDistance', 'setTyreRoughDistance'),
    mutation('DeleteTyreSet', 'deleteTyreSet'),
    mutation('RestoreTyreSet', 'restoreTyreSet'),
    mutation('UpdateTyreSet', 'updateTyreSet'),
  ]
  return { state, handlers }
}

