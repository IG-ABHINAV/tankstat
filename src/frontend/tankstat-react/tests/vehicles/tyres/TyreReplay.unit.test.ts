import { describe, expect, it } from 'vitest'
import type { Change } from '../../../src/offline/changes.ts'
import { applyTyreChange, noTyres, replayTyres } from '../../../src/tyres/replay.ts'
import { TyreRuleError } from '../../../src/tyres/tyreHistory.ts'
import type { Tyres } from '../../../src/tyres/tyres.ts'

const stretch = (id: string, setId: string, from: string, until: string | null, distance = 1000) => ({
  stretch: { id, setId, from, until, pairOneFront: true, version: 3 },
  distance: { distance, approximate: false, measure: 'MEASURED' as const },
})
const set = (id: string, name: string, onTheCar: boolean, stretches: ReturnType<typeof stretch>[]) => ({
  onTheCar,
  set: { id, vehicleId: 'v1', name, season: 'SUMMER' as const, size: null, since: '2023-03-01', madeWeek: null, madeYear: null, roughDistance: 0, roughUntil: null, retiredOn: null, isRetired: false, version: 2 },
  figures: { distance: 9000, approximate: false, measured: 9000, pairOneFront: 9000, pairOneRear: 0, pairsUnknown: 0, suggestion: null },
  stretches,
})
const base: Tyres = {
  vehicleId: 'v1',
  hasReadings: true,
  readings: [{ date: '2026-03-01', value: 140_000 }],
  days: [],
  sets: [
    set('s2', 'Continental WinterContact', false, [stretch('r2', 's2', '2025-11-15', '2026-03-28')]),
    set('s1', 'Michelin Primacy 4', true, [stretch('r1', 's1', '2026-03-28', null)]),
  ],
}
let seq = 0
const change = (action: Change['action'], input: Record<string, unknown>): Change => ({ id: `c${++seq}`, seq, createdAt: seq, entity: 'tyres', action, vehicleId: 'v1', targetId: '', input })

describe('tyre changes waiting on the device, shown as done (#287 Q3)', () => {
  it('a swap puts the set on the car and the other in storage, marks both, and keeps the day’s reading', () => {
    const { tyres, touched, skipped } = replayTyres(base, [change('swapTyres', { vehicleId: 'v1', setId: 's2', day: '2026-11-10', pairOneFront: false, replace: false, odometer: 150_000, note: null, stretchId: 'n1' })])

    expect(skipped).toEqual([])
    expect([...touched].sort()).toEqual(['s1', 's2'])
    const winter = tyres.sets.find((s) => s.set.id === 's2')!
    const summer = tyres.sets.find((s) => s.set.id === 's1')!
    expect(winter.onTheCar).toBe(true)
    expect(summer.onTheCar).toBe(false)
    expect(winter.stretches.map((x) => [x.stretch.id, x.stretch.from, x.stretch.until, x.distance.measure])).toEqual([
      ['n1', '2026-11-10', null, 'NOT_MEASURED'], // newest first, as the server lists them; its distance is the server's to work out
      ['r2', '2025-11-15', '2026-03-28', 'MEASURED'], // unchanged: the server's
    ])
    expect(summer.stretches[0].stretch.until).toBe('2026-11-10')
    expect(tyres.days).toMatchObject([{ day: '2026-11-10', odometer: 150_000 }])
    expect(tyres.readings.at(-1)).toMatchObject({ date: '2026-11-10', value: 150_000 })
    expect(base.sets[1].stretches[0].stretch.until).toBeNull() // what it was given stays as it was
  })

  it('a set added on the car, then trashed, leaves nothing; a change that no longer fits is left out, not thrown', () => {
    const added = change('addTyreSet', { id: 'n', stretchId: 'ns', vehicleId: 'v1', name: 'Nokian', season: 'WINTER', size: null, since: '2026-11-01', madeWeek: null, madeYear: null, fitted: true, day: '2026-11-10', roughDistance: 0, replace: false })
    const shown = replayTyres(base, [added]).tyres
    expect(shown.sets.map((s) => [s.set.name, s.onTheCar])).toEqual([
      ['Continental WinterContact', false],
      ['Michelin Primacy 4', false],
      ['Nokian', true],
    ])

    const stale = change('changeTyrePositions', { vehicleId: 'v1', setId: 'gone', day: '2026-11-10', pairOneFront: false, replace: false, stretchId: 'x' })
    const { tyres, skipped } = replayTyres(base, [added, change('deleteTyreSet', { id: 'n' }), stale])
    expect(skipped).toEqual([stale.id])
    expect(tyres.sets.map((s) => s.set.name)).toEqual(['Continental WinterContact', 'Michelin Primacy 4'])
    expect(tyres.sets[1].stretches[0].stretch.until).toBe('2026-11-10') // the add's swap still ended its stretch: trashing a set takes only its own
  })

  it('refuses what the server would: a second swap on a day, retiring the set on the car', () => {
    const second = change('swapTyres', { vehicleId: 'v1', setId: 's2', day: '2026-03-28', pairOneFront: null, replace: false, stretchId: 'n2' })
    expect(() => applyTyreChange(base, second)).toThrow(TyreRuleError)
    try {
      applyTyreChange(base, second)
    } catch (error) {
      expect((error as TyreRuleError).args).toEqual({ date: '2026-03-28', set: 'Michelin Primacy 4' })
    }
    expect(() => applyTyreChange(base, change('retireTyreSet', { id: 's1', day: '2026-11-10' }))).toThrow(expect.objectContaining({ key: 'tyres.retireOnCar' }))
    const replaced = applyTyreChange(base, { ...second, input: { ...second.input, replace: true } })
    expect(replaced.sets.find((s) => s.set.id === 's1')!.stretches).toEqual([]) // the swap it replaced moves to the trash
  })

  it('a day’s reading cleared takes the line and its reading; a vehicle the device knows no tyres of starts empty', () => {
    const withDay = replayTyres(base, [change('setTyreLogDay', { vehicleId: 'v1', day: '2026-06-01', odometer: 143_000, note: null })]).tyres
    expect(withDay.days).toMatchObject([{ day: '2026-06-01', odometer: 143_000 }])
    const cleared = replayTyres(withDay, [change('setTyreLogDay', { vehicleId: 'v1', day: '2026-06-01', odometer: null, note: null })]).tyres
    expect(cleared.days).toEqual([])
    expect(cleared.readings.map((r) => r.date)).toEqual(['2026-03-01'])
    expect(replayTyres(noTyres('v1'), []).tyres.sets).toEqual([])
  })
})
