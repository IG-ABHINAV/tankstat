import { describe, expect, it } from 'vitest'
import type { Change } from '../../../src/offline/changes.ts'
import { applyTyreChange, noTyres, replayTyres } from '../../../src/tyres/replay.ts'
import { TyreRuleError } from '../../../src/tyres/tyreHistory.ts'
import type { Tyres } from '../../../src/tyres/tyres.ts'

const installation = (id: string, setId: string, from: string, until: string | null, distance = 1000) => ({
  installation: { id, setId, from, until, pairOneFront: true, version: 3 },
  distance: { distance, approximate: false, measure: 'MEASURED' as const },
})
const set = (id: string, name: string, onTheCar: boolean, installations: ReturnType<typeof installation>[]) => ({
  onTheCar,
  set: { id, vehicleId: 'v1', name, season: 'SUMMER' as const, size: null, since: '2023-03-01', madeWeek: null, madeYear: null, roughDistance: 0, roughUntil: null, retiredOn: null, isRetired: false, version: 2 },
  figures: { distance: 9000, approximate: false, measured: 9000, pairOneFront: 9000, pairOneRear: 0, pairsUnknown: 0, suggestion: null },
  installations,
})
const base: Tyres = {
  vehicleId: 'v1',
  hasReadings: true,
  readings: [{ date: '2026-03-01', value: 140_000 }],
  days: [],
  sets: [
    set('s2', 'Continental WinterContact', false, [installation('r2', 's2', '2025-11-15', '2026-03-28')]),
    set('s1', 'Michelin Primacy 4', true, [installation('r1', 's1', '2026-03-28', null)]),
  ],
}
let seq = 0
const change = (action: Change['action'], input: Record<string, unknown>): Change => ({ id: `c${++seq}`, seq, createdAt: seq, entity: 'tyres', action, vehicleId: 'v1', targetId: '', input })

describe('tyre changes waiting on the device, shown as done (#287 Q3)', () => {
  it('a swap puts the set on the car and the other in storage, marks both, and keeps the day’s reading', () => {
    const { tyres, touched, skipped } = replayTyres(base, [change('swapTyres', { vehicleId: 'v1', setId: 's2', day: '2026-11-10', pairOneFront: false, replace: false, odometer: 150_000, note: null, installationId: 'n1' })])

    expect(skipped).toEqual([])
    expect([...touched].sort()).toEqual(['s1', 's2'])
    const winter = tyres.sets.find((s) => s.set.id === 's2')!
    const summer = tyres.sets.find((s) => s.set.id === 's1')!
    expect(winter.onTheCar).toBe(true)
    expect(summer.onTheCar).toBe(false)
    expect(winter.installations.map((x) => [x.installation.id, x.installation.from, x.installation.until, x.distance.measure])).toEqual([
      ['n1', '2026-11-10', null, 'NOT_MEASURED'], // newest first, as the server lists them; its distance is the server's to work out
      ['r2', '2025-11-15', '2026-03-28', 'MEASURED'], // unchanged: the server's
    ])
    expect(summer.installations[0].installation.until).toBe('2026-11-10')
    expect(tyres.days).toMatchObject([{ day: '2026-11-10', odometer: 150_000 }])
    expect(tyres.readings.at(-1)).toMatchObject({ date: '2026-11-10', value: 150_000 })
    expect(base.sets[1].installations[0].installation.until).toBeNull() // what it was given stays as it was
  })

  it('a set added on the car, then trashed, leaves nothing; a change that no longer fits is left out, not thrown', () => {
    const added = change('addTyreSet', { id: 'n', installationId: 'ns', vehicleId: 'v1', name: 'Nokian', season: 'WINTER', size: null, since: '2026-11-01', madeWeek: null, madeYear: null, fitted: true, day: '2026-11-10', roughDistance: 0, replace: false })
    const shown = replayTyres(base, [added]).tyres
    expect(shown.sets.map((s) => [s.set.name, s.onTheCar])).toEqual([
      ['Continental WinterContact', false],
      ['Michelin Primacy 4', false],
      ['Nokian', true],
    ])

    const stale = change('changeTyrePositions', { vehicleId: 'v1', setId: 'gone', day: '2026-11-10', pairOneFront: false, replace: false, installationId: 'x' })
    const { tyres, skipped } = replayTyres(base, [added, change('deleteTyreSet', { id: 'n' }), stale])
    expect(skipped).toEqual([stale.id])
    expect(tyres.sets.map((s) => s.set.name)).toEqual(['Continental WinterContact', 'Michelin Primacy 4'])
    expect(tyres.sets[1].installations[0].installation.until).toBe('2026-11-10') // the add's swap still ended its installation: trashing a set takes only its own
  })

  it('refuses what the server would: a second swap on a day, retiring the set on the car', () => {
    const second = change('swapTyres', { vehicleId: 'v1', setId: 's2', day: '2026-03-28', pairOneFront: null, replace: false, installationId: 'n2' })
    expect(() => applyTyreChange(base, second)).toThrow(TyreRuleError)
    try {
      applyTyreChange(base, second)
    } catch (error) {
      expect((error as TyreRuleError).args).toEqual({ date: '2026-03-28', set: 'Michelin Primacy 4' })
    }
    expect(() => applyTyreChange(base, change('retireTyreSet', { id: 's1', day: '2026-11-10' }))).toThrow(expect.objectContaining({ key: 'tyres.retireOnCar' }))
    const replaced = applyTyreChange(base, { ...second, input: { ...second.input, replace: true } })
    expect(replaced.sets.find((s) => s.set.id === 's1')!.installations).toEqual([]) // the swap it replaced moves to the trash
  })

  it('a day’s reading cleared takes the line and its reading; a vehicle the device knows no tyres of starts empty', () => {
    const withDay = replayTyres(base, [change('setTyreLogDay', { vehicleId: 'v1', day: '2026-06-01', odometer: 143_000, note: null })]).tyres
    expect(withDay.days).toMatchObject([{ day: '2026-06-01', odometer: 143_000 }])
    const cleared = replayTyres(withDay, [change('setTyreLogDay', { vehicleId: 'v1', day: '2026-06-01', odometer: null, note: null })]).tyres
    expect(cleared.days).toEqual([])
    expect(cleared.readings.map((r) => r.date)).toEqual(['2026-03-01'])
    expect(replayTyres(noTyres('v1'), []).tyres.sets).toEqual([])
  })

  it('a day’s reading cleared with its note kept leaves the line without it; one typed lower takes the place of the one it corrects', () => {
    const withDay = replayTyres(base, [change('setTyreLogDay', { vehicleId: 'v1', day: '2026-06-01', odometer: 143_000, note: 'Pressure checked' })]).tyres

    const noteOnly = replayTyres(withDay, [change('setTyreLogDay', { vehicleId: 'v1', day: '2026-06-01', id: withDay.days[0].id, odometer: null, note: 'Pressure checked' })]).tyres
    expect(noteOnly.days).toMatchObject([{ day: '2026-06-01', odometer: null, note: 'Pressure checked' }]) // as the server's `clear`
    expect(noteOnly.readings.map((r) => r.date)).toEqual(['2026-03-01'])

    const lower = replayTyres(withDay, [change('setTyreLogDay', { vehicleId: 'v1', day: '2026-06-01', id: withDay.days[0].id, odometer: 142_500, note: 'Pressure checked' })]).tyres
    expect(lower.days).toMatchObject([{ day: '2026-06-01', odometer: 142_500 }])
    expect(lower.readings).toMatchObject([{ date: '2026-03-01', value: 140_000 }, { date: '2026-06-01', value: 142_500 }])
  })

  it('a rough figure saved without a day keeps the one it had; one added with the set stands until the day it was added', () => {
    const rough = { ...base, sets: base.sets.map((s) => (s.set.id === 's2' ? { ...s, set: { ...s.set, roughDistance: 8000, roughUntil: '2025-11-15' } } : s)) }
    const kept = replayTyres(rough, [change('setTyreRoughDistance', { id: 's2', distance: 9000, until: null })]).tyres
    expect(kept.sets.find((s) => s.set.id === 's2')!.set).toMatchObject({ roughDistance: 9000, roughUntil: '2025-11-15' })

    const added = replayTyres(base, [change('addTyreSet', { id: 'n', vehicleId: 'v1', name: 'Nokian', season: 'WINTER', since: '2024-11-01', fitted: false, day: '2026-11-10', roughDistance: 15_000, replace: false })]).tyres
    expect(added.sets.find((s) => s.set.id === 'n')!.set).toMatchObject({ roughDistance: 15_000, roughUntil: '2026-11-10' })
  })

  it('retiring refuses a set whose installation runs past the day, as the server does; back from retirement only a retired one', () => {
    expect(() => applyTyreChange(base, change('retireTyreSet', { id: 's2', day: '2026-03-01' }))).toThrow(expect.objectContaining({ key: 'tyres.retireOnCar' }))
    expect(() => applyTyreChange(base, change('unretireTyreSet', { id: 's2' }))).toThrow(expect.objectContaining({ key: 'tyres.notRetired' }))
    const back = replayTyres(base, [change('retireTyreSet', { id: 's2', day: '2026-11-10' }), change('unretireTyreSet', { id: 's2' })])
    expect(back.skipped).toEqual([])
    expect(back.tyres.sets.find((s) => s.set.id === 's2')!.set).toMatchObject({ isRetired: false, retiredOn: null })
  })
})

describe('every other tyre change, shown as the server will make it', () => {
  const after = (c: Change) => replayTyres(base, [c])
  const winter = (r: ReturnType<typeof after>) => r.tyres.sets.find((s) => s.set.id === 's2')!
  const summer = (r: ReturnType<typeof after>) => r.tyres.sets.find((s) => s.set.id === 's1')!

  it.each([
    ['updateTyreSet', { id: 's2', name: 'Continental TS 870', season: 'ALL_SEASON', size: '205/55 R16', since: '2023-03-01', madeWeek: 5, madeYear: 2023 }, (r: ReturnType<typeof after>) => expect(winter(r).set).toMatchObject({ name: 'Continental TS 870', season: 'ALL_SEASON', size: '205/55 R16', madeWeek: 5, version: 0 })],
    ['setTyreRoughDistance', { id: 's2', distance: 12_000, until: '2025-11-15' }, (r: ReturnType<typeof after>) => expect(winter(r).set).toMatchObject({ roughDistance: 12_000, roughUntil: '2025-11-15' })],
    ['retireTyreSet', { id: 's2', day: '2026-11-10' }, (r: ReturnType<typeof after>) => expect(winter(r).set).toMatchObject({ isRetired: true, retiredOn: '2026-11-10' })],
    ['changeTyrePositions', { vehicleId: 'v1', setId: 's1', day: '2026-06-01', pairOneFront: false, replace: false, installationId: 'p1' }, (r: ReturnType<typeof after>) =>
      expect(summer(r).installations.map((x) => [x.installation.id, x.installation.from, x.installation.until, x.installation.pairOneFront, x.installation.version])).toEqual([
        ['p1', '2026-06-01', null, false, 0],
        ['r1', '2026-03-28', '2026-06-01', true, 0], // changed here: sent again without the version the server will have moved on from
      ])],
    ['addPastTyreSwaps', { vehicleId: 'v1', replace: false, swaps: [{ day: '2025-04-01', setId: 's1', odometer: 120_000, installationId: 'q1' }, { day: '2025-01-10', setId: 's2', odometer: null, installationId: 'q0' }] }, (r: ReturnType<typeof after>) => {
      expect(winter(r).installations.map((x) => [x.installation.id, x.installation.from, x.installation.until])).toEqual([['r2', '2025-11-15', '2026-03-28'], ['q0', '2025-01-10', '2025-04-01']])
      expect(summer(r).installations.map((x) => [x.installation.id, x.installation.from, x.installation.until])).toEqual([['r1', '2026-03-28', null], ['q1', '2025-04-01', '2025-11-15']])
      expect(r.tyres.days).toMatchObject([{ day: '2025-04-01', odometer: 120_000 }])
    }],
    ['addTyreInstallation', { id: 'a1', setId: 's2', from: '2024-11-09', until: '2025-03-29', pairOneFront: null, odometerFrom: 110_000, odometerUntil: null }, (r: ReturnType<typeof after>) =>
      expect(winter(r).installations.map((x) => x.installation.id)).toEqual(['r2', 'a1'])],
    ['updateTyreInstallation', { id: 'r2', from: '2025-11-01', until: '2026-03-28', pairOneFront: false, odometerFrom: null, odometerUntil: null }, (r: ReturnType<typeof after>) =>
      expect(winter(r).installations[0].installation).toMatchObject({ from: '2025-11-01', pairOneFront: false, version: 0 })],
    ['deleteTyreInstallation', { id: 'r2' }, (r: ReturnType<typeof after>) => expect(winter(r).installations).toEqual([])],
    ['restoreTyreSet', { id: 'gone' }, (r: ReturnType<typeof after>) => expect(r.tyres.sets).toHaveLength(2)], // the trash is the server's: shown once synced
    ['restoreTyreInstallation', { id: 'gone' }, (r: ReturnType<typeof after>) => expect(r.touched.size).toBe(0)],
  ] as const)('%s', (action, input, check) => {
    const replayed = after(change(action, input))
    expect(replayed.skipped).toEqual([])
    check(replayed)
  })

  it('a set or installation untouched keeps the server’s version, so a change of it is checked against it', () => {
    const r = after(change('setTyreLogDay', { vehicleId: 'v1', day: '2026-06-01', odometer: 143_000, note: null }))
    expect(summer(r).set.version).toBe(2)
    expect(summer(r).installations[0].installation.version).toBe(3)
    expect(r.added.size).toBe(0)
  })
})
