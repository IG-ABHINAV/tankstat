import { describe, expect, it } from 'vitest'
import { createApolloClient } from '../../src/apolloClient.ts'
import { baseOf, changedOnServer, merge, same, situationOf, valuesOf, type Current } from '../../src/offline/conflicts.ts'
import { RefuelingChangeValuesFragmentDoc, type RefuelingChangeValuesFragment } from '../../src/gql/generated.ts'

const now = (state: 'LIVE' | 'TRASHED', lastChange: string | null, entity: Record<string, unknown> = {}): NonNullable<Current> =>
  ({ state, version: 2, changedAt: null, lastChange, changedBy: null, vehicle: null, refueling: null, expense: null, schedule: null, ...entity }) as NonNullable<Current>

describe('valuesOf', () => {
  it('takes what a change can set, in its input’s shape, and only what the entry holds', () => {
    expect(valuesOf('vehicles', { __typename: 'Vehicle', id: 'v1', version: 3, name: 'Golf', units: { __typename: 'MeasurementUnits', distance: 'KILOMETERS', volume: 'LITERS' } }))
      .toEqual({ name: 'Golf', units: { distance: 'KILOMETERS', volume: 'LITERS' } })
    expect(valuesOf('refuelings', { volume: 40, note: null, consumption: 6.1 })).toEqual({ volume: 40, note: null })
    expect(valuesOf('expenses', null)).toEqual({})
  })
})

describe('same', () => {
  it('counts blank and null alike, typed and stored numbers alike, and objects by their fields', () => {
    expect(same('', null)).toBe(true)
    expect(same(undefined, null)).toBe(true)
    expect(same('40', 40)).toBe(true)
    expect(same(' 40.5 ', 40.5)).toBe(true)
    expect(same('40', 41)).toBe(false)
    expect(same('2026-09-01', '2026-09-01')).toBe(true)
    expect(same({ distance: 'KILOMETERS', volume: 'LITERS', __typename: 'MeasurementUnits' }, { distance: 'KILOMETERS', volume: 'LITERS' })).toBe(true)
    expect(same({ distance: 'MILES' }, { distance: 'KILOMETERS' })).toBe(false)
    expect(same(false, null)).toBe(false)
  })
})

describe('changedOnServer', () => {
  it('lists what changed since the values the change was made from, only fields both know', () => {
    const current = now('LIVE', 'EDITED', { refueling: { volume: 41, note: 'Anna', odometer: 1000, date: '2026-09-01' } })
    expect(changedOnServer('refuelings', { volume: 40, note: null, odometer: '1000' }, current)).toEqual(['volume', 'note'])
    expect(changedOnServer('refuelings', undefined, current)).toEqual([])
    expect(changedOnServer('refuelings', { volume: 40 }, null)).toEqual([])
  })
})

describe('situationOf', () => {
  const trash = { action: 'trash' as const, entity: 'refuelings' as const }
  const edit = { action: 'update' as const, entity: 'refuelings' as const }

  it('says where a trash stands: changed, trashed and back, already in the trash, or gone', () => {
    expect(situationOf(trash, now('LIVE', 'EDITED'), 'sync.versionMismatch')).toBe('changed')
    expect(situationOf(trash, now('LIVE', 'RESTORED'), 'sync.versionMismatch')).toBe('restored')
    expect(situationOf(trash, now('TRASHED', 'TRASHED'), 'refueling.notFound')).toBe('alreadyTrashed')
    expect(situationOf(trash, null, 'refueling.notFound')).toBe('gone')
  })

  it('says where an edit stands; one a rule refused, or another kind of change, is decided as before', () => {
    expect(situationOf(edit, now('TRASHED', 'TRASHED'), 'refueling.notFound')).toBe('trashedMeanwhile')
    expect(situationOf(edit, now('LIVE', 'EDITED'), 'sync.versionMismatch')).toBe('edited')
    expect(situationOf(edit, null, 'refueling.notFound')).toBe('gone')
    expect(situationOf(edit, now('LIVE', 'EDITED'), 'odometer.belowPrevious')).toBeNull()
    expect(situationOf(edit, null, 'odometer.belowPrevious')).toBeNull()
    expect(situationOf({ action: 'add', entity: 'refuelings' }, null, 'odometer.belowPrevious')).toBeNull()
  })
})

describe('baseOf', () => {
  it('reads what the screen holds of the entry, only at the version the change is made from', () => {
    const client = createApolloClient('http://localhost/graphql')
    client.cache.writeFragment({
      id: client.cache.identify({ __typename: 'Refueling', id: 'r1' }),
      fragment: RefuelingChangeValuesFragmentDoc,
      // As the server answers it (the generated types leave __typename out).
      data: { __typename: 'Refueling', version: 2, date: '2026-09-01', volume: 40, totalCost: 60, discount: null, currency: 'EUR', odometer: 1000, isFullTank: true, missedPreviousFillUp: false, note: null } as RefuelingChangeValuesFragment,
    })

    expect(baseOf(client, 'refuelings', 'r1', 2)).toMatchObject({ volume: 40, note: null, currency: 'EUR' })
    expect(baseOf(client, 'refuelings', 'r1', 1)).toBeUndefined() // another version: its values are not known
    expect(baseOf(client, 'refuelings', 'r2', 2)).toBeUndefined()
    expect(baseOf(client, 'refuelings', 'r1', null)).toBeUndefined()
  })
})

describe('merge', () => {
  const base = { date: '2026-09-20', volume: 40, totalCost: 60, currency: 'EUR', odometer: 1500, isFullTank: true, missedPreviousFillUp: false, note: null }

  it('takes a field changed on one side from that side, and starts one changed on both with this device’s', () => {
    const mine = { ...base, volume: 45, totalCost: 70, note: 'Motorway' }
    const theirs = { ...base, date: '2026-09-21', volume: 41, note: 'Fleet card' }
    const { values, fields } = merge('refuelings', base, mine, theirs)

    expect(values).toMatchObject({ date: '2026-09-21', volume: 45, totalCost: 70, note: 'Motorway', odometer: 1500 })
    expect(Object.fromEntries(fields.map((f) => [f.field, f.as]))).toEqual({
      date: 'theirs', volume: 'both', totalCost: 'mine', currency: 'same', odometer: 'same', isFullTank: 'same', missedPreviousFillUp: 'same', note: 'both',
    })
  })

  it('without a base, every field that differs is changed on both sides; a partial base decides only what it knows', () => {
    const mine = { volume: 45, note: 'x', odometer: 1500 }
    const theirs = { volume: 41, note: null, odometer: 1500 }
    expect(merge('refuelings', undefined, mine, theirs).fields).toEqual([{ field: 'volume', as: 'both' }, { field: 'odometer', as: 'same' }, { field: 'note', as: 'both' }])
    expect(merge('refuelings', { note: null }, mine, theirs).fields.find((f) => f.field === 'note')).toEqual({ field: 'note', as: 'mine' })
  })

  it('a null the server keeps its own value for is no change: the server’s stays, unlisted; typed and stored numbers, blank and none, are alike', () => {
    const merged = merge('refuelings', { volume: 40, note: null }, { volume: '40', note: '', currency: null, missedPreviousFillUp: null }, { volume: 40, note: null, currency: 'HUF', missedPreviousFillUp: true })
    expect(merged.values).toMatchObject({ currency: 'HUF', missedPreviousFillUp: true })
    expect(merged.fields).toEqual([{ field: 'volume', as: 'same' }, { field: 'note', as: 'same' }])
    // A null that clears (a note) is a change like any other.
    expect(merge('expenses', { note: 'Old' }, { note: null }, { note: 'Old' }).fields).toEqual([{ field: 'note', as: 'mine' }])
  })
})
