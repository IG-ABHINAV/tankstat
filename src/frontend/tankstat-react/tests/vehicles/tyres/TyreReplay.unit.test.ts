import { describe, expect, it } from 'vitest'
import type { Change } from '../../../src/offline/changes.ts'
import { isDeviceLine } from '../../../src/tyres/entries.ts'
import { applyTyreChange, NO_SET, noTyres, replayTyres } from '../../../src/tyres/replay.ts'
import { placeOf } from '../../../src/tyres/tyrePlaces.ts'
import { TyreRuleError } from '../../../src/tyres/tyreHistory.ts'
import type { Tyres } from '../../../src/tyres/tyres.ts'

const installation = (id: string, setId: string, from: string, until: string | null, distance = 1000) => ({
  installation: { id, setId, from, until, pairOneFront: true, version: 3 },
  distance: { distance, approximate: false, measure: 'MEASURED' as const },
})
const set = (id: string, name: string, onTheCar: boolean, installations: ReturnType<typeof installation>[]) => ({
  onTheCar,
  set: { id, vehicleId: 'v1', name, season: 'SUMMER' as const, size: null, since: '2023-03-01', madeWeek: null, madeYear: null, roughDistance: 0, roughUntil: null, retiredOn: null, isRetired: false, brokenUpOn: null, version: 2 },
  figures: { distance: 9000, approximate: false, measured: 9000, pairOneFront: 9000, pairOneRear: 0, pairsUnknown: 0, suggestion: null },
  spent: [],
  repairs: 0,
  installations,
  tyres: [] as Tyres['sets'][number]['tyres'],
})
const line = (id: string, setId: string | null, day: string, kind: Tyres['entries'][number]['kind'] = 'SWAP', odometer: number | null = null, note: string | null = null) => ({
  id, kind, setId, installationId: kind === 'SWAP' ? id : null, day, odometer, note, version: 4,
  expenseId: null, amount: null, currency: null, category: null, expenseTrashed: false, tyreId: null, groupId: null,
})
const base: Tyres = {
  vehicleId: 'v1',
  hasReadings: true,
  readings: [{ date: '2026-03-01', value: 140_000 }],
  expenseCategory: null,
  spentAll: [],
  spentNoSet: [],
  entries: [line('r1', 's1', '2026-03-28'), line('r2', 's2', '2025-11-15')],
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
    expect(tyres.entries[0]).toMatchObject({ id: 'n1', kind: 'SWAP', setId: 's2', installationId: 'n1', day: '2026-11-10', odometer: 150_000, version: 0 }) // the swap is its line
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

  it('a line added waits with its reading; changed lower, its reading takes the place of the one it corrects; cleared, it goes', () => {
    const added = replayTyres(base, [change('addTyreEntry', { vehicleId: 'v1', id: 'e1', kind: 'PUNCTURE_REPAIRED', setId: 's1', day: '2026-06-01', odometer: 143_000, note: ' Rear left ' })]).tyres
    expect(added.entries.map((e) => [e.id, e.kind, e.day])).toEqual([['r1', 'SWAP', '2026-03-28'], ['r2', 'SWAP', '2025-11-15']].toSpliced(0, 0, ['e1', 'PUNCTURE_REPAIRED', '2026-06-01']))
    expect(added.entries[0]).toMatchObject({ setId: 's1', odometer: 143_000, note: 'Rear left', version: 0 })
    expect(added.readings).toMatchObject([{ date: '2026-03-01', value: 140_000 }, { date: '2026-06-01', value: 143_000 }])

    const lower = replayTyres(added, [change('updateTyreEntry', { id: 'e1', kind: 'VALVE_REPLACED', setId: null, day: '2026-06-02', odometer: 142_500, note: null })]).tyres
    expect(lower.entries[0]).toMatchObject({ kind: 'VALVE_REPLACED', setId: null, day: '2026-06-02', odometer: 142_500, note: null })
    expect(lower.readings).toMatchObject([{ date: '2026-03-01', value: 140_000 }, { date: '2026-06-02', value: 142_500 }])

    const cleared = replayTyres(added, [change('deleteTyreEntry', { id: 'e1' })]).tyres
    expect(cleared.entries.map((e) => e.id)).toEqual(['r1', 'r2'])
    expect(cleared.readings.map((r) => r.date)).toEqual(['2026-03-01'])
    expect(replayTyres(noTyres('v1'), []).tyres.sets).toEqual([])
  })

  it('a swap’s line keeps its kind and set; its new day moves the swap, refused before the set ahead of it went on; its bin takes the swap', () => {
    expect(() => applyTyreChange(base, change('updateTyreEntry', { id: 'r1', kind: 'OTHER', setId: 's1', day: '2026-03-28' }))).toThrow(expect.objectContaining({ key: 'tyres.swapLineLocked' }))
    expect(() => applyTyreChange(base, change('addTyreEntry', { id: 'x', kind: 'SWAP', day: '2026-03-28' }))).toThrow(expect.objectContaining({ key: 'tyres.entryKindNotAllowed' }))
    expect(() => applyTyreChange(base, change('updateTyreEntry', { id: 'r1', kind: 'SWAP', setId: 's1', day: '2025-11-15' }))).toThrow(expect.objectContaining({ key: 'tyres.swapBeforePrevious' }))

    const moved = applyTyreChange(base, change('updateTyreEntry', { id: 'r1', kind: 'SWAP', setId: 's1', day: '2026-04-02', odometer: 141_000, note: 'Moved' }))
    expect(moved.sets.find((s) => s.set.id === 's2')!.installations[0].installation.until).toBe('2026-04-02')
    expect(moved.sets.find((s) => s.set.id === 's1')!.installations[0].installation.from).toBe('2026-04-02')
    expect(moved.entries[0]).toMatchObject({ id: 'r1', day: '2026-04-02', odometer: 141_000, note: 'Moved' })

    const gone = applyTyreChange(base, change('deleteTyreEntry', { id: 'r1' }))
    expect(gone.sets.find((s) => s.set.id === 's1')!.installations).toEqual([])
    expect(gone.entries.map((e) => e.id)).toEqual(['r2'])
  })

  it('a join keeps what the joined swap’s line held on its day, on a note line of the set', () => {
    const withLines: Tyres = {
      ...base,
      readings: [...base.readings, { date: '2026-08-01', value: 146_000 }],
      sets: [
        set('s2', 'Continental WinterContact', false, [installation('w', 's2', '2026-06-01', '2026-08-01')]),
        set('s1', 'Michelin Primacy 4', true, [installation('b', 's1', '2026-08-01', null), installation('a', 's1', '2026-03-28', '2026-06-01')]),
      ],
      entries: [line('b', 's1', '2026-08-01', 'SWAP', 146_000, 'Summer again'), line('w', 's2', '2026-06-01'), line('a', 's1', '2026-03-28')],
    }
    const joined = applyTyreChange(withLines, change('swapTyres', { vehicleId: 'v1', setId: 's1', day: '2026-06-01', pairOneFront: true, replace: true, installationId: 'n' }))

    expect(joined.sets.find((s) => s.set.id === 's1')!.installations.map((x) => [x.installation.id, x.installation.until])).toEqual([['a', null]])
    expect(joined.entries.map((e) => [e.kind, e.setId, e.day, e.odometer, e.note])).toEqual([['NOTE', 's1', '2026-08-01', 146_000, 'Summer again'], ['SWAP', 's1', '2026-03-28', null, null]])
    expect(joined.readings).toContainEqual(expect.objectContaining({ date: '2026-08-01', value: 146_000 }))
  })

  it('what is typed with a swap that makes no installation is a note line with the swap’s id, as the server’s; a line only the device names is not one to change', () => {
    const joining: Tyres = {
      ...base,
      sets: [
        set('s2', 'Continental WinterContact', false, [installation('w', 's2', '2026-06-01', '2026-08-01')]),
        set('s1', 'Michelin Primacy 4', true, [installation('b', 's1', '2026-08-01', null), installation('a', 's1', '2026-03-28', '2026-06-01')]),
      ],
      entries: [line('b', 's1', '2026-08-01'), line('w', 's2', '2026-06-01'), line('a', 's1', '2026-03-28')],
    }
    const swap = change('swapTyres', { vehicleId: 'v1', setId: 's1', day: '2026-06-01', pairOneFront: true, replace: true, odometer: 143_000, note: 'Joined', installationId: 'n' })
    const joined = replayTyres(joining, [swap]).tyres

    const note = joined.entries.find((e) => e.day === '2026-06-01')!
    expect(note).toMatchObject({ id: 'n', kind: 'NOTE', setId: 's1', installationId: null, odometer: 143_000, note: 'Joined' })
    expect(isDeviceLine(note)).toBe(false)
    // The line can be changed while the swap waits: the change names the id the server gives the line.
    const changed = replayTyres(joining, [swap, change('updateTyreEntry', { id: 'n', kind: 'NOTE', setId: 's1', day: '2026-06-01', odometer: 143_500, note: null })])
    expect(changed.skipped).toEqual([])
    expect(changed.tyres.entries.find((e) => e.id === 'n')).toMatchObject({ odometer: 143_500, note: null })

    // An installation's last reading on a day nothing starts: the server gives that note line an id of its own.
    const past = replayTyres(base, [change('addTyreInstallation', { id: 'p', setId: 's2', from: '2024-11-10', until: '2025-03-20', pairOneFront: null, odometerFrom: null, odometerUntil: 120_000 })]).tyres
    const kept = past.entries.find((e) => e.day === '2025-03-20')!
    expect(kept).toMatchObject({ kind: 'NOTE', setId: 's2', odometer: 120_000 })
    expect(isDeviceLine(kept)).toBe(true)
    expect(past.entries.filter((e) => e !== kept).some(isDeviceLine)).toBe(false)
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
      expect(r.tyres.entries.find((e) => e.id === 'q1')).toMatchObject({ kind: 'SWAP', day: '2025-04-01', odometer: 120_000 })
    }],
    ['addTyreInstallation', { id: 'a1', setId: 's2', from: '2024-11-09', until: '2025-03-29', pairOneFront: null, odometerFrom: 110_000, odometerUntil: null }, (r: ReturnType<typeof after>) =>
      expect(winter(r).installations.map((x) => x.installation.id)).toEqual(['r2', 'a1'])],
    ['updateTyreInstallation', { id: 'r2', from: '2025-11-01', until: '2026-03-28', pairOneFront: false, odometerFrom: null, odometerUntil: null }, (r: ReturnType<typeof after>) =>
      expect(winter(r).installations[0].installation).toMatchObject({ from: '2025-11-01', pairOneFront: false, version: 0 })],
    ['deleteTyreInstallation', { id: 'r2' }, (r: ReturnType<typeof after>) => expect(winter(r).installations).toEqual([])],
    ['restoreTyreSet', { id: 'gone' }, (r: ReturnType<typeof after>) => expect(r.tyres.sets).toHaveLength(2)], // the trash is the server's: shown once synced
    ['restoreTyreInstallation', { id: 'gone' }, (r: ReturnType<typeof after>) => expect(r.touched.size).toBe(0)],
    ['restoreTyreEntry', { id: 'gone' }, (r: ReturnType<typeof after>) => expect(r.tyres.entries).toHaveLength(2)],
  ] as const)('%s', (action, input, check) => {
    const replayed = after(change(action, input))
    expect(replayed.skipped).toEqual([])
    check(replayed)
  })

  it('a set or installation untouched keeps the server’s version, so a change of it is checked against it', () => {
    const r = after(change('addTyreEntry', { vehicleId: 'v1', id: 'e', kind: 'NOTE', day: '2026-06-01', odometer: 143_000, note: null }))
    expect(summer(r).set.version).toBe(2)
    expect(summer(r).installations[0].installation.version).toBe(3)
    expect(r.added.size).toBe(0)
  })
})

describe('an answer an older build kept', () => {
  it('a set with stretches, not installations, counts as no tyres instead of throwing (#288)', () => {
    const old = { ...base, sets: base.sets.map(({ installations, ...s }) => ({ ...s, stretches: installations })) } as unknown as Tyres
    expect(() => replayTyres(old, [change('swapTyres', { vehicleId: 'v1', setId: 's2', day: '2026-11-10', pairOneFront: null, replace: false, installationId: 'n' })])).not.toThrow()
    expect(replayTyres(old, []).tyres.sets).toEqual([])
  })

  it('a tyre log of days, not lines, counts as no tyres instead of throwing (#288)', () => {
    const { entries: _, ...rest } = base
    const old = { ...rest, days: [{ id: 'd', day: '2026-03-28', odometer: 140_000, note: null, version: 1 }] } as unknown as Tyres
    expect(() => replayTyres(old, [change('addTyreEntry', { vehicleId: 'v1', id: 'e', kind: 'NOTE', day: '2026-06-01', note: 'x' })])).not.toThrow()
    expect(replayTyres(old, []).tyres.entries).toEqual([])
  })
})

describe('a line’s amount waiting on the device (#288)', () => {
  const fee = (amount: number | null, expenseId = 'x1') => ({ amount, currency: 'HUF', category: 'Tyres', title: 'Puncture repaired', expenseId })

  it('shows on its line, and emptied it is gone (its expense in the trash)', () => {
    const added = replayTyres(base, [change('addTyreEntry', { vehicleId: 'v1', id: 'e1', kind: 'PUNCTURE_REPAIRED', setId: 's1', day: '2026-06-01', fee: fee(25_000) })]).tyres
    expect(added.entries[0]).toMatchObject({ id: 'e1', expenseId: 'x1', amount: 25_000, currency: 'HUF', category: 'Tyres', expenseTrashed: false })

    const emptied = replayTyres(added, [change('updateTyreEntry', { id: 'e1', kind: 'PUNCTURE_REPAIRED', setId: 's1', day: '2026-06-01', fee: { amount: null } })]).tyres
    expect(emptied.entries[0]).toMatchObject({ expenseId: 'x1', amount: null, expenseTrashed: true })
    expect(() => applyTyreChange(base, change('addTyreEntry', { vehicleId: 'v1', id: 'n', kind: 'NOTE', day: '2026-06-01', fee: fee(5) }))).toThrow(expect.objectContaining({ key: 'tyres.noteTakesNoAmount' }))
  })

  it('a swap’s fee is on its line, and a set’s price on a Bought line on the day it was bought', () => {
    const swapped = replayTyres(base, [change('swapTyres', { vehicleId: 'v1', setId: 's2', day: '2026-11-10', pairOneFront: null, replace: false, installationId: 'n1', fee: fee(8000, 'x2') })]).tyres
    expect(swapped.entries[0]).toMatchObject({ id: 'n1', kind: 'SWAP', amount: 8000, expenseId: 'x2' })

    const bought = replayTyres(base, [change('addTyreSet', {
      id: 'n', vehicleId: 'v1', name: 'Nokian', season: 'WINTER', since: '2024-05-01', fitted: false, day: '2026-11-10', roughDistance: 0, replace: false,
      price: fee(120_000, 'x3'), boughtOn: '2024-05-01', boughtEntryId: 'b1',
    })]).tyres
    expect(bought.entries.find((e) => e.id === 'b1')).toMatchObject({ kind: 'BOUGHT', setId: 'n', day: '2024-05-01', amount: 120_000, expenseId: 'x3' })
  })

  it('a waiting edit of a line’s expense shows on its line: its day and reading, a swap’s day moving the swap', () => {
    const withFee: Tyres = { ...base, entries: base.entries.map((e) => (e.id === 'r1' ? { ...e, expenseId: 'x1', amount: 60, currency: 'EUR' } : e)) }
    const edit: Change = { id: 'c-x', seq: 99, createdAt: 99, entity: 'expenses', action: 'update', vehicleId: 'v1', targetId: 'x1',
      input: { id: 'x1', date: '2026-04-02', title: 'Tyre swap', amount: 65, currency: 'EUR', odometer: 141_000 } }

    const { tyres, skipped } = replayTyres(withFee, [edit])

    expect(skipped).toEqual([])
    expect(tyres.entries.find((e) => e.id === 'r1')).toMatchObject({ day: '2026-04-02', odometer: 141_000, amount: 65 })
    expect(tyres.sets.find((s) => s.set.id === 's2')!.installations[0].installation.until).toBe('2026-04-02')
    expect(replayTyres(base, [edit]).tyres.entries).toEqual(base.entries) // an expense no line made changes nothing here
  })

  it('a moved swap keeps its fee', () => {
    const withFee: Tyres = { ...base, entries: base.entries.map((e) => (e.id === 'r1' ? { ...e, expenseId: 'x1', amount: 60, currency: 'EUR' } : e)) }
    const moved = applyTyreChange(withFee, change('updateTyreEntry', { id: 'r1', kind: 'SWAP', setId: 's1', day: '2026-04-02' }))
    expect(moved.entries.find((e) => e.id === 'r1')).toMatchObject({ day: '2026-04-02', expenseId: 'x1', amount: 60 })
  })
})

describe('whose costs waiting changes put out of date (#288 step 4)', () => {
  const paid = { ...line('p1', 's1', '2026-05-01', 'PUNCTURE_REPAIRED'), expenseId: 'x1', amount: 25, currency: 'EUR' }
  const withPaid: Tyres = { ...base, entries: [paid, ...base.entries] }
  const expense = (action: Change['action'], targetId: string): Change => ({ id: `c${++seq}`, seq, createdAt: seq, entity: 'expenses', action, vehicleId: 'v1', targetId, input: action === 'update' ? { amount: 30 } : undefined })

  it('nothing waiting, nothing out of date', () => {
    expect([...replayTyres(withPaid, []).costs]).toEqual([])
  })

  it('a repair added without an amount: its set (the repairs count)', () => {
    const { costs } = replayTyres(base, [change('addTyreEntry', { vehicleId: 'v1', id: 'e1', kind: 'VALVE_REPLACED', setId: 's2', day: '2026-06-01', odometer: null, note: null })])
    expect([...costs]).toEqual(['s2'])
  })

  it('a line of no set with an amount: the lines of no set', () => {
    const { costs } = replayTyres(base, [change('addTyreEntry', { vehicleId: 'v1', id: 'e1', kind: 'OTHER', setId: null, day: '2026-06-01', odometer: null, note: null,
      fee: { amount: 8, currency: 'EUR', category: 'Tyres', title: 't', expenseId: 'x9' } })])
    expect([...costs]).toEqual([NO_SET])
  })

  it('a line moved to another set: both sets', () => {
    const { costs } = replayTyres(withPaid, [change('updateTyreEntry', { id: 'p1', kind: 'PUNCTURE_REPAIRED', setId: 's2', day: '2026-05-01', odometer: null, note: null })])
    expect([...costs].sort()).toEqual(['s1', 's2'])
  })

  it('a line with its expense trashed: its set', () => {
    const { costs } = replayTyres(withPaid, [change('deleteTyreEntry', { id: 'p1', withExpense: true })])
    expect([...costs]).toEqual(['s1'])
  })

  it('its expense edited, trashed or restored on the Expenses tab: its set; another expense, nothing', () => {
    expect([...replayTyres(withPaid, [expense('update', 'x1')]).costs]).toEqual(['s1'])
    expect([...replayTyres(withPaid, [expense('trash', 'x1')]).costs]).toEqual(['s1'])
    expect([...replayTyres(withPaid, [expense('restore', 'x1')]).costs]).toEqual(['s1'])
    expect([...replayTyres(withPaid, [expense('trash', 'other')]).costs]).toEqual([])
  })

  it('a change of a set that leaves its lines as they were: not its costs', () => {
    const { costs, touched } = replayTyres(base, [change('updateTyreSet', { id: 's2', name: 'Winter', season: 'WINTER', size: null, since: '2023-03-01', madeWeek: null, madeYear: null, roughDistance: 0, day: null })])
    expect([...touched]).toEqual(['s2'])
    expect([...costs]).toEqual([])
  })
})

describe('single tyres changed on the device, shown as done (#289)', () => {
  const ids = ['a1111111-1111-4111-8111-111111111111', 'b2222222-2222-4222-8222-222222222222', 'c3333333-3333-4333-8333-333333333333', 'd4444444-4444-4444-8444-444444444444']
  const breakUp = change('breakUpTyreSet', {
    setId: 's1', day: '2026-11-01',
    tyres: ['FRONT_LEFT', 'FRONT_RIGHT', 'REAR_LEFT', 'REAR_RIGHT'].map((corner, n) => ({ id: ids[n], label: `S${n + 1}`, corner })),
  })
  const tyresOf = (t: Tyres, setId = 's1') => t.sets.find((x) => x.set.id === setId)!.tyres

  it('a break-up shows four tyres at their corners, their places worked out as the server works them out, marked', () => {
    const { tyres, touched, touchedTyres, skipped } = replayTyres(base, [breakUp, breakUp]) // sent twice: once

    expect(skipped).toEqual([])
    const summer = tyresOf(tyres)
    expect(summer.map((t) => [t.tyre.label, t.corner, t.onTheCar, t.tyre.version])).toEqual([
      ['S1', 'FRONT_LEFT', true, 0], ['S2', 'FRONT_RIGHT', true, 0], ['S3', 'REAR_LEFT', true, 0], ['S4', 'REAR_RIGHT', true, 0],
    ])
    expect(summer[0].places.map((p) => [p.place.id, p.place.installationId, p.place.from, p.distance.measure])).toEqual([[placeOf(ids[0], 'r1'), 'r1', '2026-03-28', 'NOT_MEASURED']])
    expect(tyres.sets.find((x) => x.set.id === 's1')!.set.brokenUpOn).toBe('2026-11-01')
    expect(touched.has('s1')).toBe(true)
    expect([...touchedTyres].sort()).toEqual([...ids].sort())
  })

  it('a swap onto a broken-up set places each tyre at the corner given, and its own time on the car follows its edit alone', () => {
    const off = change('swapTyres', { vehicleId: 'v1', setId: 's2', day: '2026-11-10', pairOneFront: null, replace: false, installationId: 'n1' })
    const back = change('swapTyres', {
      vehicleId: 'v1', setId: 's1', day: '2026-12-01', pairOneFront: null, replace: false, installationId: 'n2',
      corners: [{ tyreId: ids[0], corner: 'REAR_LEFT' }, { tyreId: ids[2], corner: 'FRONT_LEFT' }, { tyreId: ids[1], corner: 'FRONT_RIGHT' }],
    })
    const s4 = placeOf(ids[3], 'r1')
    const ownDays = change('updateTyrePlace', { id: s4, from: '2026-03-28', until: '2026-06-01', corner: 'REAR_RIGHT', tyreId: ids[3], installationId: 'r1' })
    const { tyres, skipped } = replayTyres(base, [breakUp, off, back, ownDays])

    expect(skipped).toEqual([])
    const summer = tyresOf(tyres)
    expect(summer.map((t) => [t.tyre.label, t.corner])).toEqual([['S1', 'REAR_LEFT'], ['S2', 'FRONT_RIGHT'], ['S3', 'FRONT_LEFT'], ['S4', null]])
    expect(summer[3].places.map((p) => [p.place.from, p.place.until])).toEqual([['2026-12-01', null], ['2026-03-28', '2026-06-01']])
    expect(summer[1].places.map((p) => p.place.until)).toEqual([null, '2026-11-10']) // the winter swap ended its place with the installation
  })

  it('a label another tyre has is refused before it waits; a tyre trashed leaves its corner empty; a restore is the server\'s', () => {
    const broken = applyTyreChange(base, breakUp)
    expect(() => applyTyreChange(broken, change('updateTyre', { id: ids[1], label: 's1' }))).toThrow(expect.objectContaining({ key: 'tyres.labelTaken' }))
    const relabelled = applyTyreChange(broken, change('updateTyre', { id: ids[1], label: 'S2a' }))
    expect(tyresOf(relabelled).map((t) => t.tyre.label)).toEqual(['S1', 'S2a', 'S3', 'S4'])

    const { tyres } = replayTyres(broken, [change('deleteTyre', { id: ids[0] }), change('restoreTyre', { id: ids[0] })])
    expect(tyresOf(tyres).map((t) => t.tyre.label)).toEqual(['S2', 'S3', 'S4'])
  })

  it('an installation typed in a broken-up set\'s History records no pairs, as the server stores it, and places each tyre', () => {
    const typed = change('addTyreInstallation', { id: 'p1', setId: 's1', from: '2025-06-01', until: '2025-10-01', pairOneFront: true })
    const { tyres, skipped } = replayTyres(base, [breakUp, typed])

    expect(skipped).toEqual([])
    const summer = tyres.sets.find((x) => x.set.id === 's1')!
    expect(summer.installations.find((x) => x.installation.id === 'p1')!.installation.pairOneFront).toBeNull()
    expect(tyresOf(tyres).map((t) => t.places.at(-1)!.place.installationId)).toEqual(['p1', 'p1', 'p1', 'p1'])
  })

  it('a time on the car outside the set\'s installations is refused with the reason, naming the set on the car then', () => {
    const broken = applyTyreChange(base, breakUp)
    expect(() => applyTyreChange(broken, change('addTyrePlace', { id: 'x1', tyreId: ids[0], from: '2025-12-01', until: '2026-01-01', corner: null })))
      .toThrow(expect.objectContaining({ key: 'tyres.placeWhileOtherSet' }))
  })

  it('a kept answer from before single tyres counts its sets as whole', () => {
    const older = { ...base, sets: base.sets.map((x) => ({ ...x, tyres: undefined, set: { ...x.set, brokenUpOn: undefined } })) } as unknown as Tyres
    expect(tyresOf(replayTyres(older, []).tyres)).toEqual([])
    const { tyres, touched } = replayTyres(older, [])
    expect(tyres.sets.map((x) => x.set.brokenUpOn)).toEqual([null, null])
    expect([...touched]).toEqual([]) // nothing changed by it
  })
})

describe('lines for several tyres sharing one expense on the device (#289), as the server’s TyreExpenseTests', () => {
  const corners = ['FRONT_LEFT', 'FRONT_RIGHT', 'REAR_LEFT', 'REAR_RIGHT']
  const fee = (amount: number | null, expenseId = 'x1') => ({ amount, currency: 'HUF', category: 'Tyres', title: 'Puncture repaired', expenseId })
  /** A flat on S3 and S4 of the whole summer set: broken up on save, a line each, one expense of 13,000 and one reading. */
  const repair = (odometer: number | null = 148_200) => change('addTyreEntry', {
    vehicleId: 'v1', kind: 'PUNCTURE_REPAIRED', day: '2026-09-20', setId: 's1', odometer, note: 'Screws in the tread', fee: fee(13_000),
    tyres: [{ tyreId: 't3', id: 'l3' }, { tyreId: 't4', id: 'l4' }],
    breakUp: { setId: 's1', day: '2026-09-20', tyres: ['t1', 't2', 't3', 't4'].map((id, n) => ({ id, label: `S${n + 1}`, corner: corners[n] })) },
  })
  const edit = (id: string, input: Record<string, unknown>) =>
    change('updateTyreEntry', { id, kind: 'PUNCTURE_REPAIRED', setId: 's1', day: '2026-09-20', odometer: 148_200, note: 'Screws in the tread', ...input })
  const lineOf = (tyres: Tyres, id: string) => tyres.entries.find((e) => e.id === id)!

  it('a flat on two tyres of a whole set breaks it up, makes a line each in one group with one expense, and counts one reading', () => {
    const { tyres, skipped, costs } = replayTyres(base, [repair()])

    expect(skipped).toEqual([])
    expect(tyres.sets.find((s) => s.set.id === 's1')!.set.brokenUpOn).toBe('2026-09-20')
    expect(['l3', 'l4'].map((id) => lineOf(tyres, id))).toMatchObject([
      { tyreId: 't3', groupId: 'l3', expenseId: 'x1', amount: 13_000, version: 0 },
      { tyreId: 't4', groupId: 'l3', expenseId: 'x1', amount: 13_000, version: 0 },
    ])
    expect(tyres.readings.filter((r) => r.date === '2026-09-20')).toHaveLength(1) // one row for both lines
    expect([...costs].sort()).toEqual(['s1', 't3', 't4'])
  })

  it('the same add sent again is found by its first line before the break-up would refuse it', () => {
    const once = applyTyreChange(base, repair())
    expect(applyTyreChange(once, repair()).entries.filter((e) => e.kind === 'PUNCTURE_REPAIRED')).toHaveLength(2)
  })

  it('a new amount on one line changes the one expense for all; nothing of its own keeps the group', () => {
    const { tyres } = replayTyres(base, [repair(), edit('l3', { fee: fee(15_000) })])
    expect(['l3', 'l4'].map((id) => [lineOf(tyres, id).amount, lineOf(tyres, id).groupId])).toEqual([[15_000, 'l3'], [15_000, 'l3']])
  })

  it('a day of its own parts a line from the expense, whatever amount the dialog still sent; the other keeps it', () => {
    const { tyres } = replayTyres(base, [repair(), edit('l3', { day: '2026-09-22', odometer: 148_400, fee: fee(13_000, 'new') })])
    expect(lineOf(tyres, 'l3')).toMatchObject({ day: '2026-09-22', odometer: 148_400, expenseId: null, amount: null, groupId: null })
    expect(lineOf(tyres, 'l4')).toMatchObject({ day: '2026-09-20', expenseId: 'x1', amount: 13_000 })
  })

  it('Also change it for moves every ticked line together, still sharing', () => {
    const { tyres } = replayTyres(base, [repair(), edit('l3', { day: '2026-09-21', odometer: 148_300, also: ['l4'] })])
    expect(['l3', 'l4'].map((id) => lineOf(tyres, id))).toMatchObject([
      { day: '2026-09-21', odometer: 148_300, expenseId: 'x1', groupId: 'l3' },
      { day: '2026-09-21', odometer: 148_300, expenseId: 'x1', groupId: 'l3' },
    ])
  })

  it('an amount typed for one line of a group without one makes its own expense and leaves the group', () => {
    const plain = change('addTyreEntry', { ...repair().input, fee: null })
    const { tyres } = replayTyres(base, [plain, edit('l3', { fee: fee(9_000, 'x3') })])
    expect(lineOf(tyres, 'l3')).toMatchObject({ expenseId: 'x3', amount: 9_000, groupId: null })
    expect(lineOf(tyres, 'l4')).toMatchObject({ expenseId: null, amount: null, groupId: 'l3' })
  })

  it('a note changed alone leaves the group but keeps the expense', () => {
    const { tyres } = replayTyres(base, [repair(), edit('l3', { note: 'Only the rear left' })])
    expect(lineOf(tyres, 'l3')).toMatchObject({ note: 'Only the rear left', groupId: null, expenseId: 'x1', amount: 13_000 })
  })

  it('an amount emptied on one line asks what it does: refused without a choice, LET_GO parts it, TRASH empties it for all', () => {
    const emptied = (sharedExpense?: string) => edit('l3', { fee: fee(null), ...(sharedExpense ? { sharedExpense } : {}) })
    const once = applyTyreChange(base, repair())
    expect(() => applyTyreChange(once, emptied())).toThrow(TyreRuleError)
    try {
      applyTyreChange(once, emptied())
    } catch (e) {
      expect((e as TyreRuleError).key).toBe('tyres.sharedExpenseChoice')
    }

    const letGo = applyTyreChange(once, emptied('LET_GO'))
    expect([lineOf(letGo, 'l3').expenseId, lineOf(letGo, 'l4').amount]).toEqual([null, 13_000])

    const trashed = applyTyreChange(once, emptied('TRASH'))
    expect(['l3', 'l4'].map((id) => [lineOf(trashed, id).amount, lineOf(trashed, id).expenseTrashed])).toEqual([[null, true], [null, true]])
  })

  it('one line to the trash leaves the other with the expense and the reading; its sharer’s costs are out of date', () => {
    const { tyres, costs } = replayTyres(base, [repair(), change('deleteTyreEntry', { id: 'l3', withExpense: true })])
    expect(tyres.entries.some((e) => e.id === 'l3')).toBe(false)
    expect(lineOf(tyres, 'l4')).toMatchObject({ expenseId: 'x1', amount: 13_000 })
    expect(tyres.readings.filter((r) => r.date === '2026-09-20')).toHaveLength(1)
    expect([...costs].sort()).toEqual(['s1', 't4']) // S3's line was only ever on the device: its share is as the server will have it
  })

  it('the expense’s new day on the Expenses tab moves every line holding it', () => {
    const made = repair() // first: the changes replay in the order they were made
    const moved: Change = { id: `c${++seq}`, seq, createdAt: seq, entity: 'expenses', action: 'update', vehicleId: 'v1', targetId: 'x1', input: { date: '2026-09-21', odometer: 148_300, amount: 13_000 } }
    const { tyres } = replayTyres(base, [made, moved])
    expect(['l3', 'l4'].map((id) => [lineOf(tyres, id).day, lineOf(tyres, id).odometer])).toEqual([['2026-09-21', 148_300], ['2026-09-21', 148_300]])
  })

  it('a tyre to the trash takes its lines along; its sharer keeps the expense', () => {
    const { tyres } = replayTyres(base, [repair(), change('deleteTyre', { id: 't3' })])
    expect(tyres.entries.some((e) => e.id === 'l3')).toBe(false)
    expect(lineOf(tyres, 'l4')).toMatchObject({ expenseId: 'x1', amount: 13_000 })
  })

  it('a line of a tyre keeps its set, and names no tyre twice', () => {
    const once = applyTyreChange(base, repair())
    expect(() => applyTyreChange(once, edit('l3', { setId: 's2' }))).toThrow(TyreRuleError)
    const twice = change('addTyreEntry', { vehicleId: 'v1', kind: 'VALVE_REPLACED', day: '2026-09-21', setId: 's1', tyres: [{ tyreId: 't1', id: 'a' }, { tyreId: 't1', id: 'b' }] })
    expect(() => applyTyreChange(once, twice)).toThrow(TyreRuleError)
  })
})

describe('tyres replaced early on the device (#289 step 3)', () => {
  const corners = ['FRONT_LEFT', 'FRONT_RIGHT', 'REAR_LEFT', 'REAR_RIGHT']
  const breakUp = { setId: 's1', day: '2026-07-09', tyres: ['t1', 't2', 't3', 't4'].map((id, n) => ({ id, label: `S${n + 1}`, corner: corners[n] })) }
  const replacing = (more: Record<string, unknown> = {}) => change('replaceTyres', {
    setId: 's1', day: '2026-07-09', why: 'DAMAGED', odometer: 145_500, breakUp,
    tyres: [{ tyreId: 't1', id: 't5', label: 'S5', madeWeek: 20, madeYear: 2026, lineId: 'b5' }, { tyreId: 't2', id: 't6', label: 'S6', name: 'Pilot Sport 5', lineId: 'b6' }],
    price: { amount: 78_000, currency: 'HUF', category: 'Tyres', title: 'Bought', expenseId: 'x9' }, ...more,
  })
  const tyreOf = (tyres: Tyres, id: string) => tyres.sets.flatMap((s) => s.tyres ?? []).find((t) => t.tyre.id === id)!

  it('retires the old tyres, puts the new ones at their corners from the day, and makes a Bought line each sharing one expense', () => {
    const { tyres, skipped, costs } = replayTyres(base, [replacing()])

    expect(skipped).toEqual([])
    expect(tyreOf(tyres, 't1').tyre).toMatchObject({ isRetired: true, retiredOn: '2026-07-09', retiredWhy: 'DAMAGED', replacedById: 't5', version: 0 })
    expect([tyreOf(tyres, 't5').corner, tyreOf(tyres, 't6').corner]).toEqual(['FRONT_LEFT', 'FRONT_RIGHT'])
    expect(tyreOf(tyres, 't5').tyre).toMatchObject({ label: 'S5', since: '2026-07-09', roughDistance: 0, madeWeek: 20, isRetired: false })
    expect(tyreOf(tyres, 't6').tyre.name).toBe('Pilot Sport 5')
    expect(tyreOf(tyres, 't5').places.map((p) => [p.place.id, p.place.from, p.place.until])).toEqual([[placeOf('t5', placeOf('t1', 'r1')), '2026-07-09', null]]) // as the server works it out
    expect(tyreOf(tyres, 't1').places.map((p) => p.place.until)).toEqual(['2026-07-09'])
    const bought = tyres.entries.filter((e) => e.kind === 'BOUGHT')
    expect(bought.map((e) => [e.id, e.tyreId, e.groupId, e.expenseId, e.amount])).toEqual([['b5', 't5', 'b5', 'x9', 78_000], ['b6', 't6', 'b5', 'x9', 78_000]])
    expect([...costs].sort()).toEqual(['s1', 't5', 't6'])
  })

  it('a replacement tyre whose own time on the car has no corner shows none, not the corner of the tyre it replaced', () => {
    const own = placeOf('t5', placeOf('t1', 'r1'))
    const { tyres } = replayTyres(base, [replacing(), change('updateTyrePlace', { id: own, from: '2026-07-09', until: null, corner: null, tyreId: 't5', installationId: 'r1' })])
    expect(tyreOf(tyres, 't5').corner).toBeNull()
  })

  it('the same replacement sent again changes nothing', () => {
    const once = applyTyreChange(base, replacing())
    expect(applyTyreChange(once, replacing()).entries.filter((e) => e.kind === 'BOUGHT')).toHaveLength(2)
  })

  it('refuses a reason the set gives, and nothing tapped', () => {
    expect(() => applyTyreChange(base, replacing({ why: 'SET_RETIRED' }))).toThrow(TyreRuleError)
    expect(() => applyTyreChange(base, replacing({ tyres: [] }))).toThrow(TyreRuleError)
  })
})
