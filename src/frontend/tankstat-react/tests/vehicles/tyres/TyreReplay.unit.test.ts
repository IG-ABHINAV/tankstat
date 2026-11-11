import { describe, expect, it } from 'vitest'
import type { Change } from '../../../src/offline/changes.ts'
import { isDeviceLine } from '../../../src/tyres/entries.ts'
import { applyTyreChange, NO_SET, noTyres, replayTyres } from '../../../src/tyres/replay.ts'
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
  spent: [],
  repairs: 0,
  installations,
})
const line = (id: string, setId: string | null, day: string, kind: Tyres['entries'][number]['kind'] = 'SWAP', odometer: number | null = null, note: string | null = null) => ({
  id, kind, setId, installationId: kind === 'SWAP' ? id : null, day, odometer, note, version: 4,
  expenseId: null, amount: null, currency: null, category: null, expenseTrashed: false,
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
  const expense = (action: Change['action'], targetId: string): Change => ({ id: `c${++seq}`, seq, createdAt: seq, entity: 'expenses', action, vehicleId: 'v1', targetId, input: action === 'update' ? { amount: 30 } : null })

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
