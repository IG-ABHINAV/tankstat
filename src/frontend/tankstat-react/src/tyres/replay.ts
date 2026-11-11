import type { Change } from '../offline/changes.ts'
import { isTyreAction } from '../offline/tyreChanges.ts'
import { changePositions, checked, moveSwap, swap, TyreRuleError, type SetState, type InstallationState } from './tyreHistory.ts'
import { follow, type Joined, type LineState } from './tyreLog.ts'
import type { Tyres, TyreSetInfo } from './tyres.ts'

/**
 * A vehicle's tyres with the tyre changes waiting on this device laid over them (#287, the owner's choice Q3: a change made offline shows as
 * done, marked). Pure, never writing into what it was given. The history's rules are the server's (`tyreHistory.ts`, pinned by the shared
 * contract); what only the server works out (an installation's distance, a set's figures) stays as it was for what did not change, and a set the
 * changes touched is listed in `touched`, so the tab says its figures are updated once synced.
 */
export interface Replayed {
  tyres: Tyres
  /** The sets whose details or time on the car the waiting changes changed (or added). */
  touched: Set<string>
  /** The sets the waiting changes added (not in the server's answer). */
  added: Set<string>
  /**
   * Whose costs the waiting changes make out of date (#288 step 4): the sets (`NO_SET` for the lines of no set) of every line added, trashed or
   * changed (both sets of one moved), and of every line whose expense an edit, trash or restore waiting on the Expenses tab names. The server
   * works the costs out, so these rows say they are updated once synced.
   */
  costs: Set<string>
  /** The waiting changes that no longer fit (what they were made on is gone, or a rule now refuses them): left out, never thrown. */
  skipped: string[]
}

type SetInfo = TyreSetInfo
type Entry = Tyres['entries'][number]
type Distance = SetInfo['installations'][number]['distance']

interface Model {
  sets: SetInfo[]
  installations: InstallationState[]
  entries: Entry[]
}

/** An object of the answer with its type's name, which Apollo's cache needs and the generated types leave out. */
const named = <T extends object>(typename: string, value: T): T => ({ __typename: typename, ...value }) as T

const UNMEASURED: Distance = named('InstallationDistance', { distance: 0, approximate: true, measure: 'NOT_MEASURED' })

const modelOf = (tyres: Tyres): Model => ({
  sets: tyres.sets.map((s) => ({ ...s, set: { ...s.set } })),
  installations: tyres.sets.flatMap((s) =>
    s.installations.map((x) => ({ id: x.installation.id, setId: s.set.id, from: x.installation.from, until: x.installation.until ?? null, pairOneFront: x.installation.pairOneFront ?? null })),
  ),
  entries: tyres.entries.map((e) => ({ ...e })),
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
const text = (v: unknown) => (typeof v === 'string' && v.trim() !== '' ? v.trim() : null)

const lineOf = (e: Entry): LineState => ({ id: e.id, kind: e.kind, setId: e.setId ?? null, installationId: e.installationId ?? null, day: e.day, odometer: e.odometer ?? null, note: e.note ?? null })

type Money = Pick<Entry, 'expenseId' | 'amount' | 'currency' | 'category' | 'expenseTrashed'>
const NO_MONEY: Money = { expenseId: null, amount: null, currency: null, category: null, expenseTrashed: false }
const moneyOf = (e: Entry | undefined): Money =>
  e ? { expenseId: e.expenseId ?? null, amount: e.amount ?? null, currency: e.currency ?? null, category: e.category ?? null, expenseTrashed: e.expenseTrashed } : NO_MONEY

/**
 * A line the change made or changed: version 0, so a later change of it goes without the version the server will have moved on from. Its
 * amount (#288) is the one it had, unless `money` says otherwise.
 */
const entryOf = (l: LineState, was?: Entry, money?: Money): Entry => named<Entry>('TyreEntry', { ...(money ?? moneyOf(was)), ...l, version: 0 })

/**
 * The amount a change gives a line (#288), as the server's `ApplyFeeAsync`: none sent leaves it; an empty one puts its expense in the trash
 * (the line shows none); one typed makes or changes it (the expense's id the change's own when it makes one).
 */
function feeOn(line: Entry, fee: unknown): Money {
  const money = moneyOf(line)
  if (!fee || typeof fee !== 'object') return money
  const f = fee as Record<string, unknown>
  if (num(f.amount) === null) return { ...money, amount: null, expenseTrashed: money.expenseId !== null }
  if (line.kind === 'NOTE') throw new TyreRuleError('tyres.noteTakesNoAmount')
  return { expenseId: money.expenseId ?? (day(f.expenseId) || null), amount: num(f.amount), currency: str(f.currency) || money.currency, category: text(f.category), expenseTrashed: false }
}

const withFee = (m: Model, id: string, fee: unknown) => {
  m.entries = m.entries.map((e) => (e.id === id ? entryOf(lineOf(e), e, feeOn(e, fee)) : e))
}

const LINE_KINDS = new Set(['SWAP', 'POSITIONS_CHANGED'])

/** The history after a change, with the tyre log's lines following it (`tyreLog.ts`): made, moved or trashed with their installations. */
function history(m: Model, after: InstallationState[], joined: Joined[], changeId: string) {
  const before = m.entries
  const followed = follow(m.installations, after, before.map(lineOf), joined, (line) => `${changeId}:kept:${line.id}`)
  m.installations = after
  m.entries = followed.lines.map((l) => {
    const was = before.find((e) => e.id === l.id)
    if (was && JSON.stringify(lineOf(was)) === JSON.stringify(l)) return was
    // A swap the history trashes takes its amount along (the owner's choice): it is gone from the log with it; a moved one keeps it.
    return entryOf(l, was) // a kept line is new: no amount
  })
}

/**
 * What was typed with a change (a reading, a note) goes on the line of the installation that starts that day, else (a join made none that
 * day) on a Note line of the set, as the server's `TypedAsync`. A note left out leaves the line's.
 */
function typed(m: Model, setId: string, on: string | null, odometer: number | null, note: string | null, changeId: string, fee?: unknown, id?: string) {
  const paid = !!fee && typeof fee === 'object' && num((fee as Record<string, unknown>).amount) !== null
  if (!on || (odometer === null && note === null && !paid)) return
  const starting = m.installations.find((x) => x.from === on)
  const line = starting ? m.entries.find((e) => e.id === starting.id) : undefined
  if (line) {
    const kind = paid && line.kind === 'NOTE' ? 'OTHER' : line.kind
    m.entries = m.entries.map((e) => (e === line ? entryOf({ ...lineOf(e), kind, odometer: odometer ?? e.odometer ?? null, note: note ?? e.note ?? null }, e) : e))
    withFee(m, line.id, fee)
    return
  }
  // No installation starts that day (a join): a line of the set of its own, an Other one when it carries an amount (a note takes none).
  const made = entryOf({ id: id && !m.entries.some((e) => e.id === id) ? id : `${changeId}:note:${on}`, kind: paid ? 'OTHER' : 'NOTE', setId, installationId: null, day: on, odometer, note })
  m.entries = [...m.entries, made]
  withFee(m, made.id, fee)
}

/** A swap of a set onto the car: the history's rule, and the lines that follow it. */
function swapped(m: Model, c: Change, setId: string, on: string, pairOneFront: boolean | null, options: { id: string; replace: boolean; pairsGiven?: boolean }) {
  const joined: Joined[] = []
  history(m, swap(states(m), m.installations, setId, on, pairOneFront, { ...options, joined: (id, d) => joined.push({ id, day: d }) }), joined, c.id)
}

/** A change the tyres follow: a tyre change, or an edit of an expense (#288: one a line made shares its day and reading with the line). */
export const touchesTyres = (c: Change) => c.entity === 'tyres' || (c.entity === 'expenses' && c.action === 'update')

/**
 * A waiting edit of a line's expense (#288), as the server's `UpdateExpenseAsync`: its date and odometer are the line's (a swap's day moving
 * the swap by the history's rules), its amount the line's to show. An expense no live line made changes nothing here.
 */
function expenseEdited(m: Model, c: Change) {
  const line = m.entries.find((e) => e.expenseId === c.targetId)
  if (!line) return
  const input = c.input ?? {}
  const on = day(input.date) ?? line.day
  const odometer = 'odometer' in input ? num(input.odometer) : (line.odometer ?? null)
  if (LINE_KINDS.has(line.kind) && on !== line.day) history(m, moveSwap(states(m), m.installations, line.installationId ?? line.id, on), [], c.id)
  m.entries = m.entries.map((e) =>
    e.id === line.id
      ? entryOf({ ...lineOf(e), day: on, odometer }, e, { ...moneyOf(e), amount: num(input.amount) ?? e.amount ?? null, currency: str(input.currency) || (e.currency ?? null) })
      : e,
  )
}

/** Applies one tyre change to the model; a rule it breaks is thrown (`TyreRuleError`), as the server would refuse it. */
function apply(m: Model, c: Change, vehicleId: string) {
  if (c.entity === 'expenses') return expenseEdited(m, c)
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
          since: str(input.since), madeWeek: num(input.madeWeek), madeYear: num(input.madeYear), roughDistance: rough, roughUntil: rough > 0 ? day(input.day) : null, retiredOn: null, isRetired: false, version: 0,
        }),
        figures: named<SetInfo['figures']>('TyreSetFigures', { distance: rough, approximate: rough > 0, measured: 0, pairOneFront: 0, pairOneRear: 0, pairsUnknown: 0, suggestion: null }),
        spent: [],
        repairs: 0,
        installations: [],
      }))
      if (input.fitted === true) {
        swapped(m, c, str(input.id), str(input.day), (input.pairOneFront as boolean | null) ?? null, { id: str(input.installationId) || c.id, replace: input.replace === true })
        typed(m, str(input.id), day(input.day), num(input.odometer), null, c.id)
      }
      // Its price (#288): a Bought line on the day it was bought, with the amount.
      if (input.price && num((input.price as Record<string, unknown>).amount) !== null) {
        const bought = entryOf({ id: str(input.boughtEntryId) || `${c.id}:bought`, kind: 'BOUGHT', setId: str(input.id), installationId: null, day: day(input.boughtOn) ?? str(input.day), odometer: null, note: null })
        m.entries = [...m.entries, bought]
        withFee(m, bought.id, input.price)
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
      // Without a day it keeps the one it had, else the set's first installation (the server's `SetRoughAsync`; `RoughDialog` sends a day then).
      const first = m.installations.filter((x) => x.setId === s.set.id).map((x) => x.from).sort()[0]
      s.set = { ...s.set, roughDistance: num(input.distance) ?? 0, roughUntil: day(input.until) ?? s.set.roughUntil ?? first ?? null }
      return
    }
    case 'retireTyreSet': {
      const s = setOf(m, input.id)
      const on = str(input.day)
      // On the car that day or later (the server's `RetireAsync`): no installation of it may run past the day it is retired.
      if (m.installations.some((x) => x.setId === s.set.id && (x.until === null || x.until > on))) throw new TyreRuleError('tyres.retireOnCar', { set: s.set.name })
      if (s.set.isRetired) throw new TyreRuleError('tyres.alreadyRetired')
      s.set = { ...s.set, retiredOn: day(input.day), isRetired: true }
      return
    }
    case 'unretireTyreSet': {
      const s = setOf(m, input.id)
      if (!s.set.isRetired) throw new TyreRuleError('tyres.notRetired')
      s.set = { ...s.set, retiredOn: null, isRetired: false }
      return
    }
    case 'deleteTyreSet': {
      const s = setOf(m, input.id)
      m.sets = m.sets.filter((x) => x !== s)
      m.installations = m.installations.filter((x) => x.setId !== s.set.id)
      m.entries = m.entries.filter((e) => e.setId !== s.set.id) // hidden through it, as its installations
      return
    }
    case 'swapTyres':
      setOf(m, input.setId)
      swapped(m, c, str(input.setId), str(input.day), (input.pairOneFront as boolean | null) ?? null, { id: str(input.installationId) || c.id, replace: input.replace === true })
      typed(m, str(input.setId), day(input.day), num(input.odometer), text(input.note), c.id, input.fee, str(input.installationId) || undefined)
      return
    case 'changeTyrePositions':
      setOf(m, input.setId)
      history(m, changePositions(states(m), m.installations, str(input.setId), str(input.day), (input.pairOneFront as boolean | null) ?? null, str(input.installationId) || c.id), [], c.id)
      typed(m, str(input.setId), day(input.day), num(input.odometer), text(input.note), c.id, input.fee, str(input.installationId) || undefined)
      return
    case 'addPastTyreSwaps': {
      const rows = (Array.isArray(input.swaps) ? (input.swaps as Record<string, unknown>[]) : []).slice().sort((a, b) => (str(a.day) < str(b.day) ? -1 : 1))
      for (const row of rows) {
        setOf(m, row.setId)
        swapped(m, c, str(row.setId), str(row.day), null, { id: str(row.installationId) || `${c.id}:${str(row.day)}`, replace: input.replace === true, pairsGiven: false })
        typed(m, str(row.setId), day(row.day), num(row.odometer), null, c.id)
      }
      return
    }
    case 'addTyreInstallation': {
      setOf(m, input.setId)
      if (m.installations.some((x) => x.id === input.id)) return
      const id = str(input.id) || c.id
      history(m, checked(
        states(m),
        [...m.installations, { id, setId: str(input.setId), from: str(input.from), until: day(input.until), pairOneFront: (input.pairOneFront as boolean | null) ?? null }],
        new Set([id]),
      ), [], c.id)
      typed(m, str(input.setId), day(input.from), num(input.odometerFrom), null, c.id)
      typed(m, str(input.setId), day(input.until), num(input.odometerUntil), null, c.id)
      return
    }
    case 'updateTyreInstallation': {
      const old = m.installations.find((x) => x.id === input.id)
      if (!old) throw new TyreRuleError('tyres.installationNotFound')
      if (day(input.until) !== null && str(input.until) <= str(input.from)) throw new TyreRuleError('tyres.untilBeforeFrom', { from: str(input.from), until: str(input.until) })
      history(m, checked(
        states(m),
        m.installations.map((x) => (x === old ? { ...old, from: str(input.from), until: day(input.until), pairOneFront: (input.pairOneFront as boolean | null) ?? null } : x)),
        new Set([old.id]),
      ), [], c.id)
      typed(m, old.setId, day(input.from), num(input.odometerFrom), null, c.id)
      typed(m, old.setId, day(input.until), num(input.odometerUntil), null, c.id)
      return
    }
    case 'deleteTyreInstallation':
      if (!m.installations.some((x) => x.id === input.id)) throw new TyreRuleError('tyres.installationNotFound')
      history(m, m.installations.filter((x) => x.id !== input.id), [], c.id)
      return
    case 'addTyreEntry': {
      if (m.entries.some((e) => e.id === input.id)) return // sent before: the server answers it with what it made
      const kind = str(input.kind) as Entry['kind']
      if (LINE_KINDS.has(kind)) throw new TyreRuleError('tyres.entryKindNotAllowed')
      if (input.setId) setOf(m, input.setId)
      const added = entryOf({ id: str(input.id) || c.id, kind, setId: day(input.setId), installationId: null, day: str(input.day), odometer: num(input.odometer), note: text(input.note) })
      m.entries = [...m.entries, added]
      withFee(m, added.id, input.fee)
      return
    }
    case 'updateTyreEntry': {
      const line = m.entries.find((e) => e.id === input.id)
      if (!line) throw new TyreRuleError('tyres.entryNotFound')
      const kind = str(input.kind) as Entry['kind']
      const setId = day(input.setId)
      const on = str(input.day)
      if (LINE_KINDS.has(line.kind)) {
        if (kind !== line.kind || setId !== (line.setId ?? null)) throw new TyreRuleError('tyres.swapLineLocked')
        if (on !== line.day) history(m, moveSwap(states(m), m.installations, line.installationId ?? line.id, on), [], c.id)
      } else {
        if (LINE_KINDS.has(kind)) throw new TyreRuleError('tyres.entryKindNotAllowed')
        if (setId && setId !== line.setId) setOf(m, setId)
      }
      m.entries = m.entries.map((e) => (e.id === line.id ? entryOf({ ...lineOf(e), kind, setId, day: on, odometer: num(input.odometer), note: text(input.note) }, e) : e))
      withFee(m, line.id, input.fee)
      return
    }
    case 'deleteTyreEntry': {
      const line = m.entries.find((e) => e.id === input.id)
      if (!line) throw new TyreRuleError('tyres.entryNotFound')
      // A swap's line takes its installation along.
      if (line.installationId && m.installations.some((x) => x.id === line.installationId))
        history(m, m.installations.filter((x) => x.id !== line.installationId), [], c.id)
      else m.entries = m.entries.filter((e) => e !== line)
      return
    }
    case 'restoreTyreSet':
    case 'restoreTyreInstallation':
    case 'restoreTyreEntry':
      return // what is in the trash is the server's (online only): shown once synced
  }
}

const sameInstallation = (a: InstallationState | undefined, b: InstallationState) => !!a && a.from === b.from && a.until === b.until && a.pairOneFront === b.pairOneFront && a.setId === b.setId

/**
 * The model as the answer the Tyres tab reads, in the server's order (`TyreService` `Overview`). A set or installation a waiting change has changed already carries no version (0, as one added here): the server bumps it once that change is
 * applied, so a later change of it is sent without one (`useTyreMutation`), never parked for a version this device knew before.
 */
function answerOf(base: Tyres, m: Model): { tyres: Tyres; touched: Set<string>; added: Set<string> } {
  const before = modelOf(base)
  const baseDistance = new Map(base.sets.flatMap((s) => s.installations.map((x) => [x.installation.id, x] as const)))
  const baseSet = new Map(base.sets.map((s) => [s.set.id, s] as const))
  const touched = new Set<string>()
  const added = new Set<string>()
  const sets = m.sets.map((info): SetInfo => {
    const id = info.set.id
    const own = m.installations.filter((x) => x.setId === id).sort((a, b) => (a.from < b.from ? 1 : a.from > b.from ? -1 : 0))
    const was = before.installations.filter((x) => x.setId === id)
    const prior = baseSet.get(id)
    const detailsChanged = !!prior && JSON.stringify({ ...prior.set, version: 0 }) !== JSON.stringify({ ...info.set, version: 0 })
    if (!prior) added.add(id)
    if (!prior || detailsChanged || own.length !== was.length || own.some((x) => !sameInstallation(was.find((w) => w.id === x.id), x))) touched.add(id)
    return {
      ...info,
      set: detailsChanged ? { ...info.set, version: 0 } : info.set,
      onTheCar: own.some((x) => x.until === null),
      installations: own.map((x) => {
        const kept = baseDistance.get(x.id)
        const unchanged = kept && sameInstallation(before.installations.find((w) => w.id === x.id), x)
        return named<SetInfo['installations'][number]>('TyreInstallationInfo', {
          installation: named<SetInfo['installations'][number]['installation']>('TyreInstallation', { id: x.id, setId: id, from: x.from, until: x.until, pairOneFront: x.pairOneFront, version: unchanged ? kept.installation.version : 0 }),
          distance: unchanged ? kept.distance : UNMEASURED,
        })
      }),
    }
  })
  const collator = new Intl.Collator(undefined, { sensitivity: 'base' })
  sets.sort((a, b) => Number(a.set.isRetired) - Number(b.set.isRetired) || collator.compare(a.set.name, b.set.name) || (a.set.id < b.set.id ? -1 : 1))
  // The server's order (`TyreService` `Overview`): newest day first, a swap's line before the others of its day, then by id.
  const swapFirst = (e: Entry) => (LINE_KINDS.has(e.kind) ? 0 : 1)
  const entries = [...m.entries].sort((a, b) => (a.day < b.day ? 1 : a.day > b.day ? -1 : swapFirst(a) - swapFirst(b) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)))
  // The car's readings: the server's, less those of the lines as they were, plus those of the lines as they are now (moved, trashed, typed).
  const readings = [...base.readings]
  for (const e of base.entries) {
    const at = e.odometer == null ? -1 : readings.findIndex((r) => r.date === e.day && r.value === e.odometer)
    if (at >= 0) readings.splice(at, 1)
  }
  for (const e of entries) if (e.odometer != null) readings.push(named('OdometerPoint', { date: e.day, value: e.odometer }))
  readings.sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : a.value - b.value))
  return { tyres: { ...base, sets, entries, readings, hasReadings: readings.length > 0 }, touched, added }
}

/** One tyre change over the vehicle's tyres as they are shown: what the device checks before it keeps the change (the server's refusals). */
/** A kept answer of an older build's shape (#288 renamed a set's stretches to installations, and made the tyre log's days lines) counts as none, never throws. */
export const isCurrentShape = (tyres: Tyres): boolean => Array.isArray(tyres.entries) && tyres.sets.every((s) => Array.isArray(s.installations))

const current = (tyres: Tyres): Tyres => (isCurrentShape(tyres) ? tyres : noTyres(tyres.vehicleId))

export function applyTyreChange(given: Tyres, change: Change): Tyres {
  const tyres = current(given)
  const m = modelOf(tyres)
  apply(m, change, tyres.vehicleId)
  return answerOf(tyres, m).tyres
}

/** The tyre changes waiting for this vehicle, in the order they were made, over the server's last answer. */
/** The key of the lines of no set in `Replayed.costs`. */
export const NO_SET = ''

const costKey = (e: Entry) => e.setId ?? NO_SET
const costShape = (e: Entry) => JSON.stringify([e.kind, e.setId ?? null, e.amount ?? null, e.currency ?? null, e.expenseId ?? null, e.expenseTrashed ?? false])

/** The sets whose costs differ between the server's lines and the replayed ones, or whose line's expense a waiting change names. */
function costsOf(base: Tyres, entries: readonly Entry[], changes: readonly Change[]): Set<string> {
  const costs = new Set<string>()
  const was = new Map(base.entries.map((e) => [e.id, e] as const))
  const now = new Map(entries.map((e) => [e.id, e] as const))
  for (const [id, e] of was) {
    const after = now.get(id)
    if (!after || costShape(after) !== costShape(e)) costs.add(costKey(e))
  }
  for (const [id, e] of now) {
    const before = was.get(id)
    if (!before || costShape(before) !== costShape(e)) costs.add(costKey(e))
  }
  const expenses = new Set(changes.filter((c) => c.entity === 'expenses' && c.vehicleId === base.vehicleId).map((c) => c.targetId))
  for (const e of [...base.entries, ...entries]) if (e.expenseId && expenses.has(e.expenseId)) costs.add(costKey(e))
  return costs
}

export function replayTyres(given: Tyres, changes: readonly Change[]): Replayed {
  const base = current(given)
  const m = modelOf(base)
  const skipped: string[] = []
  for (const change of [...changes].filter((c) => touchesTyres(c) && c.vehicleId === base.vehicleId).sort((a, b) => a.seq - b.seq)) {
    const attempt: Model = { ...m, sets: m.sets.map((s) => ({ ...s, set: { ...s.set } })), installations: [...m.installations], entries: [...m.entries] }
    try {
      apply(attempt, change, base.vehicleId)
      Object.assign(m, attempt)
    } catch {
      skipped.push(change.id) // no longer fits: the server will say why once it is sent
    }
  }
  const answer = answerOf(base, m)
  return { ...answer, skipped, costs: costsOf(base, answer.tyres.entries, changes) }
}

/** The answer of a vehicle the server has no tyres of yet as far as this device knows (added here, or never downloaded): nothing. */
export const noTyres = (vehicleId: string): Tyres => named<Tyres>('VehicleTyres', { vehicleId, sets: [], entries: [], readings: [], hasReadings: false, expenseCategory: null, spentAll: [], spentNoSet: [] })
