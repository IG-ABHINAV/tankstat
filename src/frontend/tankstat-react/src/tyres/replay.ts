import type { Change } from '../offline/changes.ts'
import { isTyreAction } from '../offline/tyreChanges.ts'
import { checked, moveSwap, TyreRuleError, type SetState, type InstallationState } from './tyreHistory.ts'
import { follow, type Joined, type LineState } from './tyreLog.ts'
import {
  addPlace, breakUp, changePositionsPlaced, follow as followPlaces, editPlace, livePlaces, requireLabelFree, checkedLabel, swapPlaced,
  type Corner, type Corners, type PlaceBook, type PlaceState,
} from './tyrePlaces.ts'
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
   * changed (both sets of one moved), and of every line whose expense an edit, trash or restore waiting on the Expenses tab names; and the
   * single tyres of those lines (#289), with every tyre sharing their expense. The server works the costs out, so these say they are updated
   * once synced.
   */
  costs: Set<string>
  /** The waiting changes that no longer fit (what they were made on is gone, or a rule now refuses them): left out, never thrown. */
  skipped: string[]
  /** The single tyres (#289) a waiting change added or changed (their label, retirement or times on the car): their figures wait for the server. */
  touchedTyres: Set<string>
}

type SetInfo = TyreSetInfo
type Entry = Tyres['entries'][number]
type Distance = SetInfo['installations'][number]['distance']
type TyreInfo = SetInfo['tyres'][number]
type TyreRow = TyreInfo['tyre']

interface Model {
  sets: SetInfo[]
  installations: InstallationState[]
  entries: Entry[]
  /** The single tyres not in the trash (#289), and their live times on the car. */
  tyres: TyreRow[]
  places: PlaceState[]
}

/** An object of the answer with its type's name, which Apollo's cache needs and the generated types leave out. */
const named = <T extends object>(typename: string, value: T): T => ({ __typename: typename, ...value }) as T

const UNMEASURED: Distance = named('InstallationDistance', { distance: 0, approximate: true, measure: 'NOT_MEASURED' })

const modelOf = (tyres: Tyres): Model => ({
  sets: tyres.sets.map((s) => ({ ...s, set: { ...s.set, brokenUpOn: s.set.brokenUpOn ?? null } })), // an older kept answer has none
  installations: tyres.sets.flatMap((s) =>
    s.installations.map((x) => ({ id: x.installation.id, setId: s.set.id, from: x.installation.from, until: x.installation.until ?? null, pairOneFront: x.installation.pairOneFront ?? null })),
  ),
  entries: tyres.entries.map((e) => ({ ...e, tyreId: e.tyreId ?? null, groupId: e.groupId ?? null })), // a kept answer from before #289 has neither
  // A kept answer from before #289 has no tyres: none.
  tyres: tyres.sets.flatMap((s) => (s.tyres ?? []).map((t) => ({ ...t.tyre }))),
  places: tyres.sets.flatMap((s) => (s.tyres ?? []).flatMap((t) => t.places.map((x) => placeStateOf(x.place)))),
})

const placeStateOf = (p: TyreInfo['places'][number]['place']): PlaceState => ({
  id: p.id, tyreId: p.tyreId, installationId: p.installationId, from: p.from, until: p.until ?? null, corner: (p.corner ?? null) as Corner | null,
})

/** What the single tyres' rules read (`tyrePlaces.ts`): the sets, their installations, the tyres and their places. */
const bookOf = (m: Model): PlaceBook => ({
  sets: m.sets.map((s) => ({ id: s.set.id, name: s.set.name, retiredOn: s.set.retiredOn ?? null, brokenUpOn: s.set.brokenUpOn ?? null })),
  installations: m.installations,
  tyres: m.tyres.map((t) => ({ id: t.id, setId: t.setId, label: t.label, retiredOn: t.retiredOn ?? null })),
  places: m.places,
})

/** A swap's or a change of positions' corners as sent (`[{ tyreId, corner }]`), or none: each tyre takes the corner it had before. */
const cornersOf = (v: unknown): Corners | null =>
  Array.isArray(v) ? Object.fromEntries((v as Record<string, unknown>[]).map((x) => [str(x.tyreId), (x.corner ?? null) as Corner | null])) : null

const tyreOf = (m: Model, id: unknown) => {
  const found = m.tyres.find((t) => t.id === id)
  if (!found) throw new TyreRuleError('tyres.tyreNotFound')
  return found
}

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

/** A line's single tyre and its group (#289), which the history's lines (`LineState`) do not carry. */
type Owner = Pick<Entry, 'tyreId' | 'groupId'>
const ownerOf = (e: Entry | undefined): Owner => ({ tyreId: e?.tyreId ?? null, groupId: e?.groupId ?? null })

/**
 * A line the change made or changed: version 0, so a later change of it goes without the version the server will have moved on from. Its
 * amount (#288) is the one it had, unless `money` says otherwise; its tyre and group (#289) too, unless `owner` does.
 */
const entryOf = (l: LineState, was?: Entry, money?: Money, owner?: Owner): Entry =>
  named<Entry>('TyreEntry', { ...(money ?? moneyOf(was)), ...(owner ?? ownerOf(was)), ...l, version: 0 })

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

/**
 * The history after a change, with the tyre log's lines following it (`tyreLog.ts`): made, moved or trashed with their installations; and a
 * broken-up set's places (`tyrePlaces.ts`), worked out here unless the change gives them.
 */
function history(m: Model, after: InstallationState[], joined: Joined[], changeId: string, places: readonly PlaceState[] = followPlaces(bookOf(m), after).places) {
  m.places = [...places]
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
 * day) on a Note line of the set, as the server's `TypedAsync`. A note left out leaves the line's. The Note line takes `id`, the id of the
 * installation the change would have made, when no line has it (the server's choice too, so a later change of the line finds it there);
 * without one its id is the device's own (`entries.ts` `isDeviceLine`: the server gives it one of its own).
 */
function typed(m: Model, setId: string, on: string | null, odometer: number | null, note: string | null, changeId: string, id = '', fee?: unknown) {
  const paid = !!fee && typeof fee === 'object' && num((fee as Record<string, unknown>).amount) !== null
  if (!on || (odometer === null && note === null && !paid)) return
  const starting = m.installations.find((x) => x.from === on)
  const line = starting ? m.entries.find((e) => e.id === starting.id) : undefined
  if (line) {
    // An installation's line: a swap or a change of positions, which takes an amount as it is.
    m.entries = m.entries.map((e) => (e === line ? entryOf({ ...lineOf(e), odometer: odometer ?? e.odometer ?? null, note: note ?? e.note ?? null }, e) : e))
    withFee(m, line.id, fee)
    return
  }
  // No installation starts that day (a join): a line of the set of its own, an Other one when it carries an amount (a note takes none).
  const lineId = id && m.entries.every((e) => e.id !== id) ? id : `${changeId}:note:${on}`
  const made = entryOf({ id: lineId, kind: paid ? 'OTHER' : 'NOTE', setId, installationId: null, day: on, odometer, note })
  m.entries = [...m.entries, made]
  withFee(m, made.id, fee)
}

/** A swap of a set onto the car: the history's rule, the lines that follow it, and a broken-up set's tyres at the corners given (#289). */
function swapped(
  m: Model, c: Change, setId: string, on: string, pairOneFront: boolean | null, options: { id: string; replace: boolean; pairsGiven?: boolean }, corners: Corners | null = null,
) {
  const joined: Joined[] = []
  const placed = swapPlaced(bookOf(m), setId, on, pairOneFront, corners, { ...options, joined: (id, d) => joined.push({ id, day: d }) })
  history(m, [...placed.installations], joined, c.id, placed.places)
}

/** A change the tyres follow: a tyre change, or an edit of an expense (#288: one a line made shares its day and reading with the line). */
export const touchesTyres = (c: Change) => c.entity === 'tyres' || (c.entity === 'expenses' && c.action === 'update')

/**
 * A waiting edit of a line's expense (#288), as the server's `UpdateExpenseAsync`: its date and odometer are the line's (a swap's day moving
 * the swap by the history's rules), its amount the line's to show, on every line holding it (#289). An expense no live line made changes
 * nothing here.
 */
function expenseEdited(m: Model, c: Change) {
  const line = m.entries.find((e) => e.expenseId === c.targetId)
  if (!line) return
  const input = c.input ?? {}
  const on = day(input.date) ?? line.day
  const odometer = 'odometer' in input ? num(input.odometer) : (line.odometer ?? null)
  if (LINE_KINDS.has(line.kind) && on !== line.day) history(m, moveSwap(states(m), m.installations, line.installationId ?? line.id, on), [], c.id)
  // Every line holding it follows (#289, the owner's decision 34): the lines made together for several tyres share one expense.
  m.entries = m.entries.map((e) =>
    e.expenseId === c.targetId
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
          since: str(input.since), madeWeek: num(input.madeWeek), madeYear: num(input.madeYear), roughDistance: rough, roughUntil: rough > 0 ? day(input.day) : null, retiredOn: null, isRetired: false,
          brokenUpOn: null, version: 0,
        }),
        figures: named<SetInfo['figures']>('TyreSetFigures', { distance: rough, approximate: rough > 0, measured: 0, pairOneFront: 0, pairOneRear: 0, pairsUnknown: 0, suggestion: null }),
        spent: [],
        repairs: 0,
        installations: [],
        tyres: [],
      }))
      if (input.fitted === true) {
        swapped(m, c, str(input.id), str(input.day), (input.pairOneFront as boolean | null) ?? null, { id: str(input.installationId) || c.id, replace: input.replace === true })
        typed(m, str(input.id), day(input.day), num(input.odometer), null, c.id, str(input.installationId))
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
      // Its tyres retire with it (#289), so unretiring it brings back these.
      m.tyres = m.tyres.map((t) => (t.setId === s.set.id && !t.isRetired ? { ...t, retiredOn: day(input.day), retiredWhy: 'SET_RETIRED', isRetired: true } : t))
      return
    }
    case 'unretireTyreSet': {
      const s = setOf(m, input.id)
      if (!s.set.isRetired) throw new TyreRuleError('tyres.notRetired')
      s.set = { ...s.set, retiredOn: null, isRetired: false }
      m.tyres = m.tyres.map((t) => (t.setId === s.set.id && t.retiredWhy === 'SET_RETIRED' ? { ...t, retiredOn: null, retiredWhy: null, isRetired: false } : t))
      return
    }
    case 'deleteTyreSet': {
      const s = setOf(m, input.id)
      m.sets = m.sets.filter((x) => x !== s)
      m.installations = m.installations.filter((x) => x.setId !== s.set.id)
      m.entries = m.entries.filter((e) => e.setId !== s.set.id) // hidden through it, as its installations
      const gone = new Set(m.tyres.filter((t) => t.setId === s.set.id).map((t) => t.id))
      m.tyres = m.tyres.filter((t) => !gone.has(t.id))
      m.places = m.places.filter((p) => !gone.has(p.tyreId))
      return
    }
    case 'swapTyres':
      setOf(m, input.setId)
      swapped(m, c, str(input.setId), str(input.day), (input.pairOneFront as boolean | null) ?? null, { id: str(input.installationId) || c.id, replace: input.replace === true },
        cornersOf(input.corners))
      typed(m, str(input.setId), day(input.day), num(input.odometer), text(input.note), c.id, str(input.installationId), input.fee)
      return
    case 'changeTyrePositions': {
      setOf(m, input.setId)
      const placed = changePositionsPlaced(bookOf(m), str(input.setId), str(input.day), (input.pairOneFront as boolean | null) ?? null, cornersOf(input.corners),
        str(input.installationId) || c.id)
      history(m, [...placed.installations], [], c.id, placed.places)
      typed(m, str(input.setId), day(input.day), num(input.odometer), text(input.note), c.id, str(input.installationId), input.fee)
      return
    }
    case 'addPastTyreSwaps': {
      const rows = (Array.isArray(input.swaps) ? (input.swaps as Record<string, unknown>[]) : []).slice().sort((a, b) => (str(a.day) < str(b.day) ? -1 : 1))
      for (const row of rows) {
        setOf(m, row.setId)
        swapped(m, c, str(row.setId), str(row.day), null, { id: str(row.installationId) || `${c.id}:${str(row.day)}`, replace: input.replace === true, pairsGiven: false })
        typed(m, str(row.setId), day(row.day), num(row.odometer), null, c.id, str(row.installationId))
      }
      return
    }
    case 'addTyreInstallation': {
      const s = setOf(m, input.setId)
      if (m.installations.some((x) => x.id === input.id)) return
      const id = str(input.id) || c.id
      // A broken-up set's installation records no pairs (#289, `TyrePlaces.AddInstallation`): its tyres' corners say it.
      const pairOneFront = s.set.brokenUpOn ? null : ((input.pairOneFront as boolean | null) ?? null)
      history(m, checked(
        states(m),
        [...m.installations, { id, setId: str(input.setId), from: str(input.from), until: day(input.until), pairOneFront }],
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
      // For several tyres (#289) a line each, the first one's id the group's; sent before, the server answers it with what it made, so the
      // repeat is found before a break-up that already happened would refuse it.
      const picked = listOf(input.tyres)
      const firstId = str(picked[0]?.id) || str(input.id) || c.id
      if (m.entries.some((e) => e.id === firstId)) return
      const kind = str(input.kind) as Entry['kind']
      if (LINE_KINDS.has(kind)) throw new TyreRuleError('tyres.entryKindNotAllowed')
      if (input.setId) setOf(m, input.setId)
      const breakUpInput = input.breakUp as Record<string, unknown> | null | undefined
      if (breakUpInput) {
        if (breakUpInput.setId !== input.setId) throw new TyreRuleError('tyres.tyreNotOfSet')
        breakUpIn(m, breakUpInput)
      }
      const line = { kind, setId: day(input.setId), installationId: null, day: str(input.day), odometer: num(input.odometer), note: text(input.note) }
      let added: Entry[]
      if (picked.length > 0) {
        const setId = day(input.setId)
        if (!setId) throw new TyreRuleError('tyres.tyreNeedsSet')
        if (new Set(picked.map((p) => p.tyreId)).size !== picked.length) throw new TyreRuleError('tyres.tyreTwice')
        for (const p of picked) if (!m.tyres.some((t) => t.id === p.tyreId && t.setId === setId && !t.isRetired)) throw new TyreRuleError('tyres.tyreNotFound')
        const groupId = picked.length > 1 ? firstId : null
        added = picked.map((p, n) => entryOf({ ...line, id: n === 0 ? firstId : str(p.id) || `${c.id}:line:${n}` }, undefined, NO_MONEY, { tyreId: str(p.tyreId), groupId }))
      } else added = [entryOf({ ...line, id: firstId }, undefined, NO_MONEY)]
      // One expense for all of them, counted once.
      const money = feeOn(added[0], input.fee)
      m.entries = [...m.entries, ...added.map((e) => entryOf(lineOf(e), e, money))]
      return
    }
    case 'updateTyreEntry':
      updateEntry(m, input, c.id)
      return
    case 'deleteTyreEntry': {
      const line = m.entries.find((e) => e.id === input.id)
      if (!line) throw new TyreRuleError('tyres.entryNotFound')
      // A swap's line takes its installation along. Its expense goes along only with the last line holding it (#289), which shows nowhere here.
      if (line.installationId && m.installations.some((x) => x.id === line.installationId))
        history(m, m.installations.filter((x) => x.id !== line.installationId), [], c.id)
      else m.entries = m.entries.filter((e) => e !== line)
      return
    }
    // Single tyres (#289).
    case 'breakUpTyreSet': {
      const given = listOf(input.tyres)
      if (setOf(m, input.setId).set.brokenUpOn && given.length > 0 && m.tyres.some((t) => t.id === given[0].id)) return // sent before: the server answers it with what it made
      breakUpIn(m, input)
      return
    }
    case 'updateTyre': {
      const tyre = tyreOf(m, input.id)
      const label = checkedLabel(str(input.label))
      requireLabelFree(bookOf(m), label, tyre.id)
      m.tyres = m.tyres.map((t) => (t === tyre ? { ...t, label } : t))
      return
    }
    case 'deleteTyre': {
      const tyre = tyreOf(m, input.id)
      m.tyres = m.tyres.filter((t) => t !== tyre)
      m.places = m.places.filter((p) => p.tyreId !== tyre.id) // its corner is empty for that time
      m.entries = m.entries.filter((e) => e.tyreId !== tyre.id) // its lines go with it (#289), as the server hides them
      return
    }
    case 'addTyrePlace': {
      if (m.places.some((p) => p.id === input.id)) return
      const place = (book: PlaceBook) => addPlace(book, str(input.tyreId), str(input.from), day(input.until), (input.corner ?? null) as Corner | null, str(input.id) || c.id)
      tyreOf(m, input.tyreId)
      m.places = [...place(bookOf(m)).places]
      return
    }
    case 'updateTyrePlace': {
      if (!m.places.some((p) => p.id === input.id)) throw new TyreRuleError('tyres.placeNotFound')
      m.places = [...editPlace(bookOf(m), str(input.id), str(input.from), day(input.until), (input.corner ?? null) as Corner | null).places]
      return
    }
    case 'deleteTyrePlace':
      if (!m.places.some((p) => p.id === input.id)) throw new TyreRuleError('tyres.placeNotFound')
      m.places = m.places.filter((p) => p.id !== input.id)
      return
    case 'restoreTyreSet':
    case 'restoreTyreInstallation':
    case 'restoreTyreEntry':
    case 'restoreTyre':
    case 'restoreTyrePlace':
      return // what is in the trash is the server's (online only): shown once synced
  }
}

const listOf = (v: unknown): Record<string, unknown>[] => (Array.isArray(v) ? (v as Record<string, unknown>[]) : [])

/** A whole set broken up (#289): its tyres with their labels and corners, each starting with the set's history. */
function breakUpIn(m: Model, input: Record<string, unknown>) {
  const s = setOf(m, input.setId)
  const given = listOf(input.tyres)
  const book = breakUp(bookOf(m), s.set.id, given.map((t) => ({ id: str(t.id), label: str(t.label), corner: (t.corner ?? null) as Corner | null })), str(input.day))
  s.set = { ...s.set, brokenUpOn: str(input.day) }
  m.tyres = [...m.tyres, ...book.tyres.filter((t) => !m.tyres.some((x) => x.id === t.id)).map((t) => tyreRowOf(t.id, t.setId, t.label, s.set, t.retiredOn))]
  m.places = [...book.places]
}

/**
 * An edit of a line, with the other lines made together with it that `also` names (#289), as the server's `UpdateEntryAsync`: a line changed
 * without the others of its group leaves it; with a day or odometer of its own it parts from the expense it shared (no amount, whatever the
 * dialog still sent: decision 29); an amount emptied while other lines share it needs `sharedExpense` (decision 30): `LET_GO` parts the lines
 * changed, `TRASH` puts the expense in the trash for all of them. A typed amount changes the one expense for every line holding it.
 */
function updateEntry(m: Model, input: Record<string, unknown>, changeId: string) {
  const entry = m.entries.find((e) => e.id === input.id)
  if (!entry) throw new TyreRuleError('tyres.entryNotFound')
  const kind = str(input.kind) as Entry['kind']
  const setId = day(input.setId)
  const on = str(input.day)
  const odometer = num(input.odometer)
  const note = text(input.note)
  const lines = [entry]
  for (const other of Array.isArray(input.also) ? (input.also as unknown[]) : []) {
    const found = m.entries.find((e) => e.id === other && e.groupId != null && e.groupId === entry.groupId)
    if (!found) throw new TyreRuleError('tyres.entryNotFound')
    if (!lines.includes(found)) lines.push(found)
  }
  if (LINE_KINDS.has(entry.kind)) {
    if (kind !== entry.kind || setId !== (entry.setId ?? null)) throw new TyreRuleError('tyres.swapLineLocked')
    if (on !== entry.day) history(m, moveSwap(states(m), m.installations, entry.installationId ?? entry.id, on), [], changeId)
  } else {
    if (LINE_KINDS.has(kind)) throw new TyreRuleError('tyres.entryKindNotAllowed')
    if (setId && setId !== entry.setId) {
      if (entry.tyreId) throw new TyreRuleError('tyres.tyreNotOfSet')
      setOf(m, setId)
    }
  }
  // The history may have moved a swap's line (a new object): the lines as they are now, compared by id.
  const ids = new Set(lines.map((l) => l.id))
  const lines_ = lines.map((l) => m.entries.find((e) => e.id === l.id) ?? l)
  const holders = entry.expenseId ? m.entries.filter((e) => e.expenseId === entry.expenseId) : []
  const allHolders = holders.every((h) => ids.has(h.id))
  const group = entry.groupId ? m.entries.filter((e) => e.groupId === entry.groupId) : []
  const ownDays = on !== entry.day || odometer !== (entry.odometer ?? null)
  const parted = !allHolders && ownDays && entry.expenseId != null
  // An amount typed for these lines alone makes an expense the rest of the group does not hold: a change of their own too.
  const ownAmount = !!input.fee && typeof input.fee === 'object' && num((input.fee as Record<string, unknown>).amount) !== null && !entry.expenseId
  const changesAny = ownDays || ownAmount || kind !== entry.kind || note !== (entry.note ?? null) || setId !== (entry.setId ?? null)
  const leavesGroup = changesAny && group.length > 0 && !group.every((g) => ids.has(g.id))
  const changed = new Map<string, Entry>()
  for (const line of lines_) {
    const owner = { tyreId: line.tyreId ?? null, groupId: parted || leavesGroup ? null : (line.groupId ?? null) }
    const money = parted && line.expenseId ? NO_MONEY : moneyOf(line)
    changed.set(line.id, entryOf({ ...lineOf(line), kind, setId: line.tyreId ? (line.setId ?? null) : setId, day: on, odometer, note }, line, money, owner))
  }
  m.entries = m.entries.map((e) => changed.get(e.id) ?? e)
  if (parted || !input.fee || typeof input.fee !== 'object') return
  const fee = input.fee as Record<string, unknown>
  const others = holders.filter((h) => !ids.has(h.id))
  const now = (e: Entry) => m.entries.find((x) => x.id === e.id)!
  if (num(fee.amount) === null && entry.expenseId && !entry.expenseTrashed && others.length > 0) {
    if (input.sharedExpense === 'LET_GO') {
      m.entries = m.entries.map((e) => (changed.has(e.id) ? entryOf(lineOf(e), e, NO_MONEY, { tyreId: e.tyreId ?? null, groupId: null }) : e))
      return
    }
    if (input.sharedExpense === 'TRASH') {
      m.entries = m.entries.map((e) => (e.expenseId === entry.expenseId ? entryOf(lineOf(e), e, { ...moneyOf(e), amount: null, expenseTrashed: true }) : e))
      return
    }
    throw new TyreRuleError('tyres.sharedExpenseChoice')
  }
  const money = feeOn(now(entry), fee)
  const sharing = new Set([...holders.map((h) => h.id), ...ids])
  m.entries = m.entries.map((e) => (sharing.has(e.id) && (e.expenseId === entry.expenseId || e.expenseId == null) ? entryOf(lineOf(e), e, money) : e))
}

/** A tyre a break-up made here (#289): the set's made date and rough figure, as the server copies them; version 0 until synced. */
const tyreRowOf = (id: string, setId: string, label: string, set: SetInfo['set'], retiredOn: string | null): TyreRow => named<TyreRow>('Tyre', {
  id, setId, label, name: null, madeWeek: set.madeWeek ?? null, madeYear: set.madeYear ?? null, since: set.since, roughDistance: set.roughDistance,
  retiredOn, retiredWhy: retiredOn ? 'SET_RETIRED' : null, replacedById: null, isRetired: retiredOn !== null, version: 0,
})

const NO_FIGURES: TyreInfo['figures'] = named('TyreFigures', { distance: 0, approximate: true, measured: 0, front: 0, rear: 0, unknown: 0 })
const samePlace = (a: PlaceState | undefined, b: PlaceState) =>
  !!a && a.installationId === b.installationId && a.from === b.from && a.until === b.until && a.corner === b.corner

/** A broken-up set's tyres in the answer (the server's order: by label), each a waiting change touched at version 0, its figures the server's to work out. */
function tyresOf(base: Tyres, m: Model, setId: string, touched: Set<string>): TyreInfo[] {
  const baseTyres = new Map(base.sets.flatMap((s) => (s.tyres ?? []).map((t) => [t.tyre.id, t] as const)))
  const basePlaces = new Map(base.sets.flatMap((s) => (s.tyres ?? []).flatMap((t) => t.places.map((x) => [x.place.id, x] as const))))
  const live = livePlaces(bookOf(m))
  const collator = new Intl.Collator(undefined, { sensitivity: 'base' })
  return m.tyres.filter((t) => t.setId === setId).sort((a, b) => collator.compare(a.label, b.label) || (a.id < b.id ? -1 : 1)).map((t) => {
    const was = baseTyres.get(t.id)
    const own = live.filter((p) => p.tyreId === t.id).sort((a, b) => (a.from < b.from ? 1 : a.from > b.from ? -1 : 0))
    const places = own.map((p) => {
      const kept = basePlaces.get(p.id)
      const unchanged = kept && samePlace(placeStateOf(kept.place), p)
      return named<TyreInfo['places'][number]>('TyrePlaceInfo', {
        place: named<TyreInfo['places'][number]['place']>('TyrePlace', { ...p, version: unchanged ? kept.place.version : 0 }),
        distance: unchanged ? kept.distance : UNMEASURED,
      })
    })
    const rowChanged = !was || was.tyre.label !== t.label || (was.tyre.retiredOn ?? null) !== (t.retiredOn ?? null)
    const placesChanged = !was || was.places.length !== own.length || places.some((p) => p.place.version === 0)
    if (rowChanged || placesChanged) touched.add(t.id)
    return named<TyreInfo>('TyreInfo', {
      tyre: rowChanged ? { ...t, version: 0 } : t,
      figures: was?.figures ?? NO_FIGURES,
      places,
      onTheCar: own.some((p) => p.until === null),
      corner: own[0]?.corner ?? null,
      spent: was?.spent ?? [], // the server's to work out once synced (`Replayed.costs`)
      repairs: was?.repairs ?? 0,
    })
  })
}

const sameInstallation = (a: InstallationState | undefined, b: InstallationState) => !!a && a.from === b.from && a.until === b.until && a.pairOneFront === b.pairOneFront && a.setId === b.setId

/**
 * The model as the answer the Tyres tab reads, in the server's order (`TyreService` `Overview`). A set or installation a waiting change has changed already carries no version (0, as one added here): the server bumps it once that change is
 * applied, so a later change of it is sent without one (`useTyreMutation`), never parked for a version this device knew before.
 */
function answerOf(base: Tyres, m: Model): { tyres: Tyres; touched: Set<string>; added: Set<string>; touchedTyres: Set<string> } {
  const touchedTyres = new Set<string>()
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
    const detailsChanged = !!prior && JSON.stringify({ ...prior.set, brokenUpOn: prior.set.brokenUpOn ?? null, version: 0 }) !== JSON.stringify({ ...info.set, version: 0 })
    if (!prior) added.add(id)
    const tyres = tyresOf(base, m, id, touchedTyres)
    const tyresChanged = tyres.some((t) => touchedTyres.has(t.tyre.id)) || (prior?.tyres ?? []).length !== tyres.length
    if (!prior || detailsChanged || tyresChanged || own.length !== was.length || own.some((x) => !sameInstallation(was.find((w) => w.id === x.id), x))) touched.add(id)
    return {
      ...info,
      tyres,
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
  // The lines made together for several tyres, and those still sharing their expense, hold one reading row (#289): counted once.
  const readings = [...base.readings]
  const once = (list: readonly Entry[]) => [...new Map(list.filter((e) => e.odometer != null).map((e) => [`${e.expenseId ?? e.groupId ?? e.id}|${e.day}|${e.odometer}`, e] as const)).values()]
  for (const e of once(base.entries)) {
    const at = readings.findIndex((r) => r.date === e.day && r.value === e.odometer)
    if (at >= 0) readings.splice(at, 1)
  }
  for (const e of once(entries)) readings.push(named('OdometerPoint', { date: e.day, value: e.odometer! }))
  readings.sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : a.value - b.value))
  return { tyres: { ...base, sets, entries, readings, hasReadings: readings.length > 0 }, touched, added, touchedTyres }
}

/** A kept answer of an older build's shape (#288 renamed a set's stretches to installations, and made the tyre log's days lines) counts as none, never throws. */
export const isCurrentShape = (tyres: Tyres): boolean => Array.isArray(tyres.entries) && tyres.sets.every((s) => Array.isArray(s.installations))

const current = (tyres: Tyres): Tyres => (isCurrentShape(tyres) ? tyres : noTyres(tyres.vehicleId))

/** One tyre change over the vehicle's tyres as they are shown: what the device checks before it keeps the change (the server's refusals). */
export function applyTyreChange(given: Tyres, change: Change): Tyres {
  const tyres = current(given)
  const m = modelOf(tyres)
  apply(m, change, tyres.vehicleId)
  return answerOf(tyres, m).tyres
}

/** The key of the lines of no set in `Replayed.costs`. */
export const NO_SET = ''

const costKeys = (e: Entry) => [e.setId ?? NO_SET, ...(e.tyreId ? [e.tyreId] : [])]
const costShape = (e: Entry) =>
  JSON.stringify([e.kind, e.setId ?? null, e.tyreId ?? null, e.amount ?? null, e.currency ?? null, e.expenseId ?? null, e.expenseTrashed ?? false])

/** The sets whose costs differ between the server's lines and the replayed ones, or whose line's expense a waiting change names. */
function costsOf(base: Tyres, entries: readonly Entry[], changes: readonly Change[]): Set<string> {
  const costs = new Set<string>()
  const was = new Map(base.entries.map((e) => [e.id, e] as const))
  const now = new Map(entries.map((e) => [e.id, e] as const))
  const moved = [
    ...[...was].filter(([id, e]) => !now.has(id) || costShape(now.get(id)!) !== costShape(e)).map(([, e]) => e),
    ...[...now].filter(([id, e]) => !was.has(id) || costShape(was.get(id)!) !== costShape(e)).map(([, e]) => e),
  ]
  // A line's expense another line shares (#289): every holder's even share moves with it; so does one an Expenses tab change names.
  const expenses = new Set([
    ...moved.map((e) => e.expenseId).filter(Boolean),
    ...changes.filter((c) => c.entity === 'expenses' && c.vehicleId === base.vehicleId).map((c) => c.targetId),
  ])
  for (const e of [...moved, ...[...base.entries, ...entries].filter((x) => x.expenseId && expenses.has(x.expenseId))]) for (const key of costKeys(e)) costs.add(key)
  return costs
}

/** The tyre changes waiting for this vehicle, in the order they were made, over the server's last answer. */
export function replayTyres(given: Tyres, changes: readonly Change[]): Replayed {
  const base = current(given)
  const m = modelOf(base)
  const skipped: string[] = []
  for (const change of [...changes].filter((c) => touchesTyres(c) && c.vehicleId === base.vehicleId).sort((a, b) => a.seq - b.seq)) {
    const attempt: Model = {
      ...m, sets: m.sets.map((s) => ({ ...s, set: { ...s.set } })), installations: [...m.installations], entries: [...m.entries], tyres: [...m.tyres], places: [...m.places],
    }
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
