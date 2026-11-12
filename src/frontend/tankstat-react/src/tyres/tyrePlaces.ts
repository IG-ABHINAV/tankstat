import { changePositions, moveSwap, swap, TyreRuleError, type InstallationState, type SetState } from './tyreHistory.ts'

/**
 * The rules of single tyres (#289), ported from the server's `TyrePlaces` so the device shows a change made offline as the server will make
 * it: the break-up, the places that follow the history, and the six checks. Pure; it takes what is live (sets, their installations, the
 * tyres not in the trash and their places) and returns what is live after the change. The cases both sides must agree on are
 * `tests/backend/contracts/tyre-places/tyre-places.json` (checked by `TyrePlacesContractTests` and `TyrePlaces.unit`).
 */
export type Corner = 'FRONT_LEFT' | 'FRONT_RIGHT' | 'REAR_LEFT' | 'REAR_RIGHT' | 'FRONT' | 'REAR'

export const EXACT_CORNERS: readonly Corner[] = ['FRONT_LEFT', 'FRONT_RIGHT', 'REAR_LEFT', 'REAR_RIGHT']
export const isFront = (c: Corner | null) => c === 'FRONT_LEFT' || c === 'FRONT_RIGHT' || c === 'FRONT'
export const isRear = (c: Corner | null) => c === 'REAR_LEFT' || c === 'REAR_RIGHT' || c === 'REAR'
export const isExact = (c: Corner | null): c is Corner => c !== null && EXACT_CORNERS.includes(c)

export interface PlacedSet extends SetState {
  /** The day it was broken up into single tyres; null for a whole set. */
  brokenUpOn: string | null
}

export interface TyreState {
  id: string
  setId: string
  label: string
  retiredOn: string | null
  /** The tyre that replaced it (#289), once it was replaced. */
  replacedById?: string | null
}

export interface PlaceState {
  id: string
  tyreId: string
  installationId: string
  from: string
  until: string | null
  corner: Corner | null
}

/** What is live: the sets, their installations, the tyres not in the trash and their places. */
export interface PlaceBook {
  sets: readonly PlacedSet[]
  installations: readonly InstallationState[]
  tyres: readonly TyreState[]
  places: readonly PlaceState[]
}

/** A new tyre replacing one of a set's (#289): the tyre it replaces, and its own id and label. */
export interface ReplacingTyre {
  oldId: string
  id: string
  label: string
}

/** A tyre of a break-up as the user gave it. */
export interface NewTyre {
  id: string
  label: string
  corner: Corner | null
}

export type Corners = Readonly<Record<string, Corner | null>>

export const MAX_LABEL = 6

/**
 * The id of a place a change makes by itself (`TyreIds.PlaceOf`): the tyre's and the installation's ids, their hex digits exclusive-or'd
 * pairwise, so the device names the same row the server makes.
 */
export function placeOf(tyreId: string, installationId: string): string {
  const [a, b] = [tyreId.replace(/-/g, '').toLowerCase(), installationId.replace(/-/g, '').toLowerCase()]
  const digits = [...a].map((d, i) => (parseInt(d, 16) ^ parseInt(b[i], 16)).toString(16)).join('')
  return `${digits.slice(0, 8)}-${digits.slice(8, 12)}-${digits.slice(12, 16)}-${digits.slice(16, 20)}-${digits.slice(20)}`
}

const covers = (from: string, until: string | null, day: string) => from <= day && (until === null || until > day)
const byFrom = (a: { from: string }, b: { from: string }) => (a.from < b.from ? -1 : a.from > b.from ? 1 : 0)

/** The places that count: of a tyre and an installation that are live. */
export const livePlaces = (book: PlaceBook) =>
  book.places.filter((p) => book.tyres.some((t) => t.id === p.tyreId) && book.installations.some((s) => s.id === p.installationId))

/** The label as stored: trimmed, required, at most six characters. */
export function checkedLabel(label: string): string {
  const trimmed = label.trim()
  if (trimmed.length === 0) throw new TyreRuleError('tyres.labelRequired')
  if (trimmed.length > MAX_LABEL) throw new TyreRuleError('tyres.labelTooLong', { max: String(MAX_LABEL) })
  return trimmed
}

/** A label no other tyre of the vehicle not in the trash has, without case (the owner's decision 27). */
export function requireLabelFree(book: PlaceBook, label: string, exceptId?: string) {
  if (book.tyres.some((t) => t.id !== exceptId && t.label.toUpperCase() === label.toUpperCase())) throw new TyreRuleError('tyres.labelTaken', { label })
}

/** The tyre a tyre replaced (#289), if it came as a replacement. */
export const predecessorOf = (book: PlaceBook, tyreId: string) => book.tyres.find((t) => t.replacedById === tyreId)

/** The day a tyre that came as a replacement could first be on the car: the day the tyre it replaced was retired. */
export const arrivedOn = (book: PlaceBook, tyreId: string): string | null => predecessorOf(book, tyreId)?.retiredOn ?? null

/** The set's live tyres that could be on the car from a day: not retired by then, and come by then. */
const onTheCarFrom = (book: PlaceBook, setId: string, day: string) =>
  book.tyres.filter((t) => t.setId === setId && (t.retiredOn === null || t.retiredOn > day) && ((arrivedOn(book, t.id) ?? day) <= day))

/** The corner a tyre had the last time before a day; a tyre that came as a replacement and was never on before, its predecessor's. */
const cornerBefore = (book: PlaceBook, tyreId: string, day: string): Corner | null => {
  const own = [...livePlaces(book)].filter((p) => p.tyreId === tyreId && p.from < day).sort(byFrom).at(-1)
  if (own) return own.corner
  const replaced = predecessorOf(book, tyreId)
  return replaced ? cornerBefore(book, replaced.id, day) : null
}

/** Each live tyre's corner for a swap or change of positions on a day: those given (a tyre left out has none), else as before. */
export function cornersFor(book: PlaceBook, setId: string, day: string, given: Corners | null): Record<string, Corner | null> {
  return Object.fromEntries(onTheCarFrom(book, setId, day).map((t) => [t.id, given === null ? cornerBefore(book, t.id, day) : (given[t.id] ?? null)]))
}

/** Whether every live tyre of the installation's set has the corner asked for there (decision 47: the corners decide a join). */
export function sameCorners(book: PlaceBook, installation: InstallationState, day: string, corners: Corners): boolean {
  const anchor = installation.from > day ? installation.from : day
  const live = livePlaces(book)
  return Object.entries(corners).every(([tyreId, corner]) => {
    const own = live.filter((p) => p.tyreId === tyreId && p.installationId === installation.id)
    const near = own.find((p) => covers(p.from, p.until, anchor)) ?? own.find((p) => p.until === anchor)
    return (near?.corner ?? null) === corner
  })
}

/** The refusal of days outside the set's installations: naming the set on the car then, when one was. */
function outside(book: PlaceBook, tyre: TyreState, from: string, until: string | null): TyreRuleError {
  const set = book.sets.find((s) => s.id === tyre.setId)!.name
  const covering = book.installations.find((s) => covers(s.from, s.until, from))
  const other = covering && covering.setId !== tyre.setId ? covering : undefined
  if (!other) return new TyreRuleError('tyres.placeOutsideSet', { set, label: tyre.label, from, until })
  return new TyreRuleError('tyres.placeWhileOtherSet', {
    set, label: tyre.label, other: book.sets.find((s) => s.id === other.setId)?.name ?? '', otherFrom: other.from, otherUntil: other.until,
  })
}

/**
 * The six checks (decision 32): inside its installation, not after its tyre was retired, a tyre on the car once at a time, one tyre per exact
 * corner, at most two at the front and two at the rear, at most four. `moving`: the places the change adds, edits or restores (an overlap
 * names the other one); `restoring`: an overlap is `tyres.tyreRestoreConflict`.
 */
export function checkPlaces(book: PlaceBook, moving?: ReadonlySet<string>, restoring = false): void {
  const label = (p: PlaceState) => book.tyres.find((t) => t.id === p.tyreId)!.label
  const live = [...livePlaces(book)].sort((a, b) => byFrom(a, b) || (label(a) < label(b) ? -1 : label(a) > label(b) ? 1 : a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
  for (const p of live) {
    const installation = book.installations.find((s) => s.id === p.installationId)!
    const tyre = book.tyres.find((t) => t.id === p.tyreId)!
    if (p.until !== null && p.until <= p.from) throw new TyreRuleError('tyres.untilBeforeFrom', { from: p.from, until: p.until })
    if (p.from < installation.from || (installation.until !== null && (p.until === null || p.until > installation.until))) throw outside(book, tyre, p.from, p.until)
    if (tyre.retiredOn !== null && (p.until === null || p.until > tyre.retiredOn)) throw new TyreRuleError('tyres.tyreRetired', { label: tyre.label, date: tyre.retiredOn })
    const arrived = arrivedOn(book, tyre.id)
    if (arrived !== null && p.from < arrived) throw new TyreRuleError('tyres.tyreNotYet', { label: tyre.label, date: arrived })
  }
  const conflict = (key: string, other: PlaceState, args: Record<string, string | null>) =>
    restoring ? new TyreRuleError('tyres.tyreRestoreConflict', { label: label(other), from: other.from, until: other.until }) : new TyreRuleError(key, args)
  for (let i = 0; i < live.length; i++)
    for (let j = i + 1; j < live.length; j++) {
      const [a, b] = [live[i], live[j]]
      if (a.until !== null && b.from >= a.until) continue
      const other = moving?.has(b.id) && !moving.has(a.id) ? a : b
      const args = { label: label(other), from: other.from, until: other.until }
      if (a.tyreId === b.tyreId) throw conflict('tyres.tyreOnTwice', other, args)
      if (isExact(a.corner) && a.corner === b.corner) throw conflict('tyres.cornerTaken', other, args)
    }
  for (const start of live) {
    const on = live.filter((p) => covers(p.from, p.until, start.from))
    const other = on.find((p) => !moving?.has(p.id)) ?? start
    const args = { label: label(other), from: start.from }
    if (on.filter((p) => isFront(p.corner)).length > 2) throw conflict('tyres.tooManyFront', other, args)
    if (on.filter((p) => isRear(p.corner)).length > 2) throw conflict('tyres.tooManyRear', other, args)
    if (on.length > 4) throw conflict('tyres.fifthTyre', other, args)
  }
}

const done = (book: PlaceBook, changed: ReadonlySet<string>): PlaceBook => {
  checkPlaces(book, changed)
  return book
}

/** Each live tyre of a broken-up set gets a place in a new installation (decision 48: the corners given, else as before). */
function placeTyres(book: PlaceBook, installation: InstallationState, corners: Corners | null): PlaceState[] {
  const set = book.sets.find((s) => s.id === installation.setId)
  if (!set?.brokenUpOn) return []
  const given = corners ?? cornersFor(book, set.id, installation.from, null)
  // A tyre retired during the installation comes off then; one that replaced it (#289) goes on then, at its corner.
  const onAtStart = onTheCarFrom(book, set.id, installation.from)
  const arriving = book.tyres.filter((t) => {
    const a = arrivedOn(book, t.id)
    return t.setId === set.id && a !== null && a > installation.from && (installation.until === null || a < installation.until) && (t.retiredOn === null || t.retiredOn > a)
  })
  return [...onAtStart, ...arriving].map((t) => {
    const replaced = predecessorOf(book, t.id)
    return {
      id: placeOf(t.id, installation.id), tyreId: t.id, installationId: installation.id, from: arriving.includes(t) ? arrivedOn(book, t.id)! : installation.from,
      until: t.retiredOn !== null && (installation.until === null || installation.until > t.retiredOn) ? t.retiredOn : installation.until,
      corner: t.id in given ? (given[t.id] ?? null) : replaced ? (given[replaced.id] ?? null) : null,
    }
  })
}

/**
 * The places follow a change of the history (`TyrePlaces.Follow`): those at a changed installation's ends follow its new days, a created
 * one places each tyre, a trashed one hides its places. Returns the book after it, checked.
 */
export function follow(book: PlaceBook, after: readonly InstallationState[], corners: Corners | null = null): PlaceBook {
  const changed = new Set<string>()
  const places = book.places.map((p) => {
    const was = book.installations.find((s) => s.id === p.installationId)
    const now = after.find((s) => s.id === p.installationId)
    if (!was || !now || (was.from === now.from && was.until === now.until)) return p
    const from = p.from === was.from ? now.from : p.from
    const until = p.until === was.until ? now.until : p.until
    if (from === p.from && until === p.until) return p
    if (until !== null && until <= from) throw outside(book, book.tyres.find((t) => t.id === p.tyreId)!, p.from, p.until)
    changed.add(p.id)
    return { ...p, from, until }
  })
  const next: PlaceBook = { ...book, installations: after, places }
  const created = after.filter((s) => !book.installations.some((b) => b.id === s.id)).flatMap((s) => placeTyres(next, s, corners))
  for (const p of created) changed.add(p.id)
  return done({ ...next, places: [...places, ...created] }, changed)
}

/** The corners change in place on an installation's first day (no new places). */
export function setCorners(book: PlaceBook, installation: InstallationState, corners: Corners): PlaceBook {
  const changed = new Set<string>()
  const places = book.places.map((p) => {
    if (p.installationId !== installation.id || p.from !== installation.from || !(p.tyreId in corners) || p.corner === corners[p.tyreId]) return p
    changed.add(p.id)
    return { ...p, corner: corners[p.tyreId] }
  })
  return done({ ...book, places }, changed)
}

/** Breaks a set up into four tyres (`TyrePlaces.BreakUp`): each starts with the set's history, its corner on the installation given for. */
export function breakUp(book: PlaceBook, setId: string, given: readonly NewTyre[], day: string): PlaceBook {
  const set = book.sets.find((s) => s.id === setId)
  if (!set) throw new TyreRuleError('tyres.setNotFound')
  if (set.brokenUpOn) throw new TyreRuleError('tyres.alreadyBrokenUp', { set: set.name })
  if (given.length !== 4) throw new TyreRuleError('tyres.fourTyres')
  const labels = given.map((t) => checkedLabel(t.label))
  if (new Set(labels.map((l) => l.toUpperCase())).size !== labels.length) throw new TyreRuleError('tyres.labelTwice')
  for (const l of labels) requireLabelFree(book, l)
  const exact = given.map((t) => t.corner).filter(isExact)
  if (new Set(exact).size !== exact.length) throw new TyreRuleError('tyres.cornerTwice')
  if (given.some((t) => t.corner !== null && !isExact(t.corner))) throw new TyreRuleError('tyres.unknownCorner')
  const installations = book.installations.filter((s) => s.setId === setId).sort(byFrom)
  const known = installations.find((s) => s.until === null) ?? installations.at(-1)
  const tyres: TyreState[] = given.map((t, i) => ({ id: t.id, setId, label: labels[i], retiredOn: set.retiredOn }))
  const places = given.flatMap((t) => {
    // The corner tells the pair: at the front where pair 1 was at the front, it is pair 1; at the rear there, pair 2.
    const pairOne = t.corner !== null && known?.pairOneFront != null ? isFront(t.corner) === known.pairOneFront : null
    return installations.map<PlaceState>((s) => ({
      id: placeOf(t.id, s.id), tyreId: t.id, installationId: s.id, from: s.from, until: s.until,
      corner: s === known ? t.corner : pairOne !== null && s.pairOneFront !== null ? (pairOne === s.pairOneFront ? 'FRONT' : 'REAR') : null,
    }))
  })
  return done({ ...book, sets: book.sets.map((s) => (s.id === setId ? { ...s, brokenUpOn: day } : s)), tyres: [...book.tyres, ...tyres], places: [...book.places, ...places] },
    new Set(places.map((p) => p.id)))
}

/** A swap with its places (`TyrePlaces.Swap`). */
export function swapPlaced(
  book: PlaceBook, setId: string, day: string, pairOneFront: boolean | null, corners: Corners | null,
  options: { id: string; replace?: boolean; pairsGiven?: boolean; joined?: (id: string, day: string) => void },
): PlaceBook {
  const set = book.sets.find((s) => s.id === setId)
  const given = set?.brokenUpOn ? cornersFor(book, setId, day, corners) : null
  const after = swap(book.sets, book.installations, setId, day, given ? null : pairOneFront, {
    ...options, sameCorners: given ? (s) => sameCorners(book, s, day, given) : undefined,
  })
  const followed = follow(book, after, given)
  const created = after.some((s) => !book.installations.some((b) => b.id === s.id))
  const starting = after.find((s) => s.setId === setId && s.from === day)
  return given && !created && starting ? setCorners(followed, starting, given) : followed
}

/** A change of positions with its places (`TyrePlaces.ChangePositions`): a broken-up set's installation splits, or changes in place on its first day. */
export function changePositionsPlaced(book: PlaceBook, setId: string, day: string, pairOneFront: boolean | null, corners: Corners | null, id: string): PlaceBook {
  const set = book.sets.find((s) => s.id === setId)
  if (!set?.brokenUpOn) return { ...book, installations: changePositions(book.sets, book.installations, setId, day, pairOneFront, id) }
  const given = cornersFor(book, setId, day, corners)
  const after = changePositions(book.sets, book.installations, setId, day, null, id, (s) => sameCorners(book, s, day, given))
  if (after.some((s) => !book.installations.some((b) => b.id === s.id) || book.installations.some((b) => b.id === s.id && (b.from !== s.from || b.until !== s.until))))
    return follow(book, after, given)
  const starting = after.find((s) => s.setId === setId && s.from === day)
  return starting ? setCorners(book, starting, given) : book
}

/** A swap moved by its line's day, the places at the installations' ends with it. */
export function moveSwapPlaced(book: PlaceBook, installationId: string, day: string): PlaceBook {
  return follow(book, moveSwap(book.sets, book.installations, installationId, day))
}

/** An installation's days edited in History, the places at its ends with it (decision 35). */
export function editInstallationPlaced(book: PlaceBook, after: readonly InstallationState[]): PlaceBook {
  return follow(book, after)
}

/** The set's live installation that holds the days; else the refusal (decision 31). */
function holding(book: PlaceBook, tyre: TyreState, from: string, until: string | null): InstallationState {
  const found = book.installations.find((s) => s.setId === tyre.setId && s.from <= from && (s.until === null || (until !== null && until <= s.until)))
  if (!found) throw outside(book, tyre, from, until)
  return found
}

/** A time on the car typed in a tyre's history. */
export function addPlace(book: PlaceBook, tyreId: string, from: string, until: string | null, corner: Corner | null, id: string): PlaceBook {
  const tyre = book.tyres.find((t) => t.id === tyreId)
  if (!tyre) throw new TyreRuleError('tyres.tyreNotFound')
  if (until !== null && until <= from) throw new TyreRuleError('tyres.untilBeforeFrom', { from, until })
  const place: PlaceState = { id, tyreId, installationId: holding(book, tyre, from, until).id, from, until, corner }
  return done({ ...book, places: [...book.places, place] }, new Set([id]))
}

/** A tyre's own time on the car changed: that tyre's alone. */
export function editPlace(book: PlaceBook, placeId: string, from: string, until: string | null, corner: Corner | null): PlaceBook {
  const place = livePlaces(book).find((p) => p.id === placeId)
  if (!place) throw new TyreRuleError('tyres.placeTrashedCannotEdit')
  if (until !== null && until <= from) throw new TyreRuleError('tyres.untilBeforeFrom', { from, until })
  const tyre = book.tyres.find((t) => t.id === place.tyreId)!
  const installationId = holding(book, tyre, from, until).id
  return done({ ...book, places: book.places.map((p) => (p.id === placeId ? { ...p, installationId, from, until, corner } : p)) }, new Set([placeId]))
}

/**
 * Tyres of a broken-up set replaced on a day (#289, `TyrePlaces.Replace`): each old tyre retired then and naming the new one; each new one
 * takes the old one's times on the car from that day on, at the same corner (the one running through the day split, the new part's id
 * `placeOf(new tyre, old place)`; the later ones handed over whole).
 */
export function replace(book: PlaceBook, setId: string, given: readonly ReplacingTyre[], day: string): PlaceBook {
  const set = book.sets.find((s) => s.id === setId)
  if (!set) throw new TyreRuleError('tyres.setNotFound')
  if (!set.brokenUpOn) throw new TyreRuleError('tyres.notBrokenUp', { set: set.name })
  if (set.retiredOn) throw new TyreRuleError('tyres.replaceRetiredSet', { set: set.name })
  if (given.length === 0) throw new TyreRuleError('tyres.tapToReplace')
  if (new Set(given.map((g) => g.oldId)).size !== given.length) throw new TyreRuleError('tyres.tyreTwice')
  const labels = given.map((g) => checkedLabel(g.label))
  if (new Set(labels.map((l) => l.toUpperCase())).size !== labels.length) throw new TyreRuleError('tyres.labelTwice')
  for (const label of labels) requireLabelFree(book, label)
  let { tyres, places } = book
  const changed = new Set<string>()
  given.forEach((g, i) => {
    const old = tyres.find((t) => t.id === g.oldId && t.setId === setId)
    if (!old) throw new TyreRuleError('tyres.tyreNotFound', { id: g.oldId })
    if (old.retiredOn !== null) throw new TyreRuleError('tyres.tyreAlreadyRetired', { label: old.label })
    tyres = [...tyres.map((t) => (t === old ? { ...t, retiredOn: day, replacedById: g.id } : t)), { id: g.id, setId, label: labels[i], retiredOn: null, replacedById: null }]
    const live = new Set(livePlaces({ ...book, tyres, places }).map((p) => p.id))
    places = places.flatMap((p) => {
      if (p.tyreId !== old.id || !live.has(p.id) || (p.until !== null && p.until <= day)) return [p]
      changed.add(p.id)
      if (p.from >= day) return [{ ...p, tyreId: g.id }]
      const rest: PlaceState = { id: placeOf(g.id, p.id), tyreId: g.id, installationId: p.installationId, from: day, until: p.until, corner: p.corner }
      changed.add(rest.id)
      return [{ ...p, until: day }, rest]
    })
  })
  return done({ ...book, tyres, places }, changed)
}
