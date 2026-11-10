import type { Change } from '../offline/changes.ts'
import { isTyreAction } from '../offline/tyreChanges.ts'
import { changePositions, checked, swap, TyreRuleError, type SetState, type StretchState } from './tyreHistory.ts'
import type { Tyres, TyreSetInfo } from './tyres.ts'

/**
 * A vehicle's tyres with the tyre changes waiting on this device laid over them (#287, the owner's choice Q3: a change made offline shows as
 * done, marked). Pure, never writing into what it was given. The history's rules are the server's (`tyreHistory.ts`, pinned by the shared
 * contract); what only the server works out (a stretch's distance, a set's figures) stays as it was for what did not change, and a set the
 * changes touched is listed in `touched`, so the tab says its figures are updated once synced.
 */
export interface Replayed {
  tyres: Tyres
  /** The sets whose details or time on the car the waiting changes changed (or added). */
  touched: Set<string>
  /** The sets the waiting changes added (not in the server's answer). */
  added: Set<string>
  /** The waiting changes that no longer fit (what they were made on is gone, or a rule now refuses them): left out, never thrown. */
  skipped: string[]
}

type SetInfo = TyreSetInfo
type Day = Tyres['days'][number]
type Distance = SetInfo['stretches'][number]['distance']

interface Model {
  sets: SetInfo[]
  stretches: StretchState[]
  days: Day[]
  readings: Tyres['readings']
}

/** An object of the answer with its type's name, which Apollo's cache needs and the generated types leave out. */
const named = <T extends object>(typename: string, value: T): T => ({ __typename: typename, ...value }) as T

const UNMEASURED: Distance = named('StretchDistance', { distance: 0, approximate: true, measure: 'NOT_MEASURED' })

const modelOf = (tyres: Tyres): Model => ({
  sets: tyres.sets.map((s) => ({ ...s, set: { ...s.set } })),
  stretches: tyres.sets.flatMap((s) =>
    s.stretches.map((x) => ({ id: x.stretch.id, setId: s.set.id, from: x.stretch.from, until: x.stretch.until ?? null, pairOneFront: x.stretch.pairOneFront ?? null })),
  ),
  days: tyres.days.map((d) => ({ ...d })),
  readings: [...tyres.readings],
})

const states = (m: Model): SetState[] => m.sets.map((s) => ({ id: s.set.id, name: s.set.name, retiredOn: s.set.retiredOn ?? null }))

const setOf = (m: Model, id: unknown) => {
  const found = m.sets.find((s) => s.set.id === id)
  if (!found) throw new TyreRuleError('tyres.setNotFound')
  return found
}

const str = (v: unknown) => (typeof v === 'string' ? v : '')
const num = (v: unknown) => (typeof v === 'number' ? v : null)
const day = (v: unknown) => (typeof v === 'string' && v !== '' ? v : null)

/** The id the device gives a tyre log line a waiting change wrote: not one the server knows, so a change of that line names none. */
const LOCAL_LINE = 'local:'
export const isLocalLine = (id: string) => id.startsWith(LOCAL_LINE)

/**
 * A reading typed with a change: the tyre log's line for that day (its note kept), and a reading of the car on that day. A line it changes
 * has version 0 from then on, so a later change of it goes without the version the server will have moved on from.
 */
function typed(m: Model, vehicleId: string, on: string | null, odometer: number | null, note?: unknown) {
  if (!on || (odometer === null && note === undefined)) return
  const existing = m.days.find((d) => d.day === on)
  const next: Day = existing
    ? { ...existing, odometer: odometer ?? existing.odometer, note: note === undefined ? existing.note : ((note as string | null) ?? null), version: 0 }
    : named<Day>('TyreLogDay', { id: `${LOCAL_LINE}${vehicleId}:${on}`, day: on, odometer, note: (note as string | null | undefined) ?? null, version: 0 })
  m.days = [...m.days.filter((d) => d !== existing), next]
  if (odometer !== null) m.readings = [...m.readings.filter((r) => r.date !== on || r.value > odometer), ...(m.readings.some((r) => r.date === on && r.value > odometer) ? [] : [named('OdometerPoint', { date: on, value: odometer })])]
}

/** Applies one tyre change to the model; a rule it breaks is thrown (`TyreRuleError`), as the server would refuse it. */
function apply(m: Model, c: Change, vehicleId: string) {
  if (!isTyreAction(c.action)) return
  const input = c.input ?? {}
  switch (c.action) {
    case 'addTyreSet': {
      if (m.sets.some((s) => s.set.id === input.id)) return // sent before: the server answers it with what it made
      const rough = num(input.roughDistance) ?? 0
      m.sets.push(named<SetInfo>('TyreSetInfo', {
        onTheCar: false,
        set: named<SetInfo['set']>('TyreSet', {
          id: str(input.id), vehicleId, name: str(input.name), season: input.season as SetInfo['set']['season'], size: (input.size as string | null) ?? null,
          since: str(input.since), madeWeek: num(input.madeWeek), madeYear: num(input.madeYear), roughDistance: rough, roughUntil: null, retiredOn: null, isRetired: false, version: 0,
        }),
        figures: named<SetInfo['figures']>('TyreSetFigures', { distance: rough, approximate: rough > 0, measured: 0, pairOneFront: 0, pairOneRear: 0, pairsUnknown: 0, suggestion: null }),
        stretches: [],
      }))
      if (input.fitted === true) {
        m.stretches = swap(states(m), m.stretches, str(input.id), str(input.day), (input.pairOneFront as boolean | null) ?? null, { id: str(input.stretchId) || c.id, replace: input.replace === true })
        typed(m, vehicleId, day(input.day), num(input.odometer))
      }
      return
    }
    case 'updateTyreSet': {
      const s = setOf(m, input.id)
      s.set = { ...s.set, name: str(input.name), season: input.season as SetInfo['set']['season'], size: (input.size as string | null) ?? null, since: str(input.since), madeWeek: num(input.madeWeek), madeYear: num(input.madeYear) }
      return
    }
    case 'setTyreRoughDistance': {
      const s = setOf(m, input.id)
      s.set = { ...s.set, roughDistance: num(input.distance) ?? 0, roughUntil: day(input.until) }
      return
    }
    case 'retireTyreSet': {
      const s = setOf(m, input.id)
      if (m.stretches.some((x) => x.setId === s.set.id && x.until === null)) throw new TyreRuleError('tyres.retireOnCar', { set: s.set.name })
      s.set = { ...s.set, retiredOn: day(input.day), isRetired: true }
      return
    }
    case 'unretireTyreSet': {
      const s = setOf(m, input.id)
      s.set = { ...s.set, retiredOn: null, isRetired: false }
      return
    }
    case 'deleteTyreSet': {
      const s = setOf(m, input.id)
      m.sets = m.sets.filter((x) => x !== s)
      m.stretches = m.stretches.filter((x) => x.setId !== s.set.id)
      return
    }
    case 'swapTyres':
      setOf(m, input.setId)
      m.stretches = swap(states(m), m.stretches, str(input.setId), str(input.day), (input.pairOneFront as boolean | null) ?? null, { id: str(input.stretchId) || c.id, replace: input.replace === true })
      typed(m, vehicleId, day(input.day), num(input.odometer), input.note ?? undefined)
      return
    case 'changeTyrePositions':
      setOf(m, input.setId)
      m.stretches = changePositions(states(m), m.stretches, str(input.setId), str(input.day), (input.pairOneFront as boolean | null) ?? null, str(input.stretchId) || c.id)
      typed(m, vehicleId, day(input.day), num(input.odometer), input.note ?? undefined)
      return
    case 'addPastTyreSwaps': {
      const rows = (Array.isArray(input.swaps) ? (input.swaps as Record<string, unknown>[]) : []).slice().sort((a, b) => (str(a.day) < str(b.day) ? -1 : 1))
      for (const row of rows) {
        setOf(m, row.setId)
        m.stretches = swap(states(m), m.stretches, str(row.setId), str(row.day), null, { id: str(row.stretchId) || `${c.id}:${str(row.day)}`, replace: input.replace === true, pairsGiven: false })
        typed(m, vehicleId, day(row.day), num(row.odometer))
      }
      return
    }
    case 'addTyreStretch': {
      setOf(m, input.setId)
      if (m.stretches.some((x) => x.id === input.id)) return
      const id = str(input.id) || c.id
      m.stretches = checked(
        states(m),
        [...m.stretches, { id, setId: str(input.setId), from: str(input.from), until: day(input.until), pairOneFront: (input.pairOneFront as boolean | null) ?? null }],
        new Set([id]),
      )
      typed(m, vehicleId, day(input.from), num(input.odometerFrom))
      typed(m, vehicleId, day(input.until), num(input.odometerUntil))
      return
    }
    case 'updateTyreStretch': {
      const old = m.stretches.find((x) => x.id === input.id)
      if (!old) throw new TyreRuleError('tyres.stretchNotFound')
      if (day(input.until) !== null && str(input.until) <= str(input.from)) throw new TyreRuleError('tyres.untilBeforeFrom', { from: str(input.from), until: str(input.until) })
      m.stretches = checked(
        states(m),
        m.stretches.map((x) => (x === old ? { ...old, from: str(input.from), until: day(input.until), pairOneFront: (input.pairOneFront as boolean | null) ?? null } : x)),
        new Set([old.id]),
      )
      typed(m, vehicleId, day(input.from), num(input.odometerFrom))
      typed(m, vehicleId, day(input.until), num(input.odometerUntil))
      return
    }
    case 'deleteTyreStretch':
      if (!m.stretches.some((x) => x.id === input.id)) throw new TyreRuleError('tyres.stretchNotFound')
      m.stretches = m.stretches.filter((x) => x.id !== input.id)
      return
    case 'setTyreLogDay': {
      const on = day(input.day)
      const existing = m.days.find((d) => d.day === on)
      const odometer = num(input.odometer)
      const note = (input.note as string | null | undefined) ?? null
      // Cleared, the line goes with its reading (as clearing an expense's odometer); a reading typed here was the day's own.
      if (odometer === null && !note) m.days = m.days.filter((d) => d !== existing)
      else typed(m, vehicleId, on, odometer, note)
      if (existing?.odometer != null && existing.odometer !== odometer) m.readings = m.readings.filter((r) => !(r.date === on && r.value === existing.odometer))
      return
    }
    case 'restoreTyreSet':
    case 'restoreTyreStretch':
      return // what is in the trash is the server's (online only): shown once synced
  }
}

const sameStretch = (a: StretchState | undefined, b: StretchState) => !!a && a.from === b.from && a.until === b.until && a.pairOneFront === b.pairOneFront && a.setId === b.setId

/** The model as the answer the Tabs tab reads, in the server's order (`TyreService` `Overview`). */
/**
 * A set or stretch a waiting change has changed already carries no version (0, as one added here): the server bumps it once that change is
 * applied, so a later change of it is sent without one (`useTyreMutation`), never parked for a version this device knew before.
 */
function answerOf(base: Tyres, m: Model): { tyres: Tyres; touched: Set<string>; added: Set<string> } {
  const before = modelOf(base)
  const baseDistance = new Map(base.sets.flatMap((s) => s.stretches.map((x) => [x.stretch.id, x] as const)))
  const baseSet = new Map(base.sets.map((s) => [s.set.id, s] as const))
  const touched = new Set<string>()
  const added = new Set<string>()
  const sets = m.sets.map((info): SetInfo => {
    const id = info.set.id
    const own = m.stretches.filter((x) => x.setId === id).sort((a, b) => (a.from < b.from ? 1 : a.from > b.from ? -1 : 0))
    const was = before.stretches.filter((x) => x.setId === id)
    const prior = baseSet.get(id)
    const detailsChanged = !!prior && JSON.stringify({ ...prior.set, version: 0 }) !== JSON.stringify({ ...info.set, version: 0 })
    if (!prior) added.add(id)
    if (!prior || detailsChanged || own.length !== was.length || own.some((x) => !sameStretch(was.find((w) => w.id === x.id), x))) touched.add(id)
    return {
      ...info,
      set: detailsChanged ? { ...info.set, version: 0 } : info.set,
      onTheCar: own.some((x) => x.until === null),
      stretches: own.map((x) => {
        const kept = baseDistance.get(x.id)
        const unchanged = kept && sameStretch(before.stretches.find((w) => w.id === x.id), x)
        return named<SetInfo['stretches'][number]>('TyreStretchInfo', {
          stretch: named<SetInfo['stretches'][number]['stretch']>('TyreStretch', { id: x.id, setId: id, from: x.from, until: x.until, pairOneFront: x.pairOneFront, version: unchanged ? kept.stretch.version : 0 }),
          distance: unchanged ? kept.distance : UNMEASURED,
        })
      }),
    }
  })
  const collator = new Intl.Collator(undefined, { sensitivity: 'base' })
  sets.sort((a, b) => Number(a.set.isRetired) - Number(b.set.isRetired) || collator.compare(a.set.name, b.set.name) || (a.set.id < b.set.id ? -1 : 1))
  const days = [...m.days].sort((a, b) => (a.day < b.day ? 1 : a.day > b.day ? -1 : 0))
  const readings = [...m.readings].sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0))
  return { tyres: { ...base, sets, days, readings, hasReadings: readings.length > 0 }, touched, added }
}

/** One tyre change over the vehicle's tyres as they are shown: what the device checks before it keeps the change (the server's refusals). */
export function applyTyreChange(tyres: Tyres, change: Change): Tyres {
  const m = modelOf(tyres)
  apply(m, change, tyres.vehicleId)
  return answerOf(tyres, m).tyres
}

/** The tyre changes waiting for this vehicle, in the order they were made, over the server's last answer. */
export function replayTyres(base: Tyres, changes: readonly Change[]): Replayed {
  const m = modelOf(base)
  const skipped: string[] = []
  for (const change of [...changes].filter((c) => c.entity === 'tyres' && c.vehicleId === base.vehicleId).sort((a, b) => a.seq - b.seq)) {
    const attempt: Model = { ...m, sets: m.sets.map((s) => ({ ...s, set: { ...s.set } })), stretches: [...m.stretches], days: [...m.days], readings: [...m.readings] }
    try {
      apply(attempt, change, base.vehicleId)
      Object.assign(m, attempt)
    } catch {
      skipped.push(change.id) // no longer fits: the server will say why once it is sent
    }
  }
  return { ...answerOf(base, m), skipped }
}

/** The answer of a vehicle the server has no tyres of yet as far as this device knows (added here, or never downloaded): nothing. */
export const noTyres = (vehicleId: string): Tyres => named<Tyres>('VehicleTyres', { vehicleId, sets: [], days: [], readings: [], hasReadings: false })
