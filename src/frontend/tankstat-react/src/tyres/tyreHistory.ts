/**
 * The rules of a vehicle's tyre history, ported from the server's `TyreHistory` (#287) so the device can show a tyre change made offline as
 * done (the owner's choice) and ask the same questions before it is sent (a second swap on a day). Pure. The cases both sides must agree on
 * are `tests/backend/contracts/tyre-swaps/tyre-swaps.json` (checked by `TyreSwapContractTests` and `TyreHistory.unit`).
 */
export interface InstallationState {
  id: string
  setId: string
  from: string
  until: string | null
  pairOneFront: boolean | null
}

export interface SetState {
  id: string
  name: string
  /** The day it was retired: no installation of it may run past it. */
  retiredOn: string | null
}

/** A rule the change breaks, as the server words it (`tyres.swapExists` with `date` and `set`, ...). */
export class TyreRuleError extends Error {
  readonly key: string
  readonly args: Record<string, string | null>
  constructor(key: string, args: Record<string, string | null> = {}) {
    super(key)
    this.key = key
    this.args = args
  }
}

const covers = (s: InstallationState, day: string) => s.from <= day && (s.until === null || s.until > day)

/** The installations a change created or changed: those of `after` not in `before` as they were. */
function moved(before: readonly InstallationState[], after: readonly InstallationState[]): ReadonlySet<string> {
  const changed = (x: InstallationState) => {
    const was = before.find((b) => b.id === x.id)
    return !was || was.from !== x.from || was.until !== x.until || was.pairOneFront !== x.pairOneFront
  }
  return new Set(after.filter(changed).map((x) => x.id))
}

/**
 * Puts a set on the car on a day (the server's `TyreHistory.Swap`): what covers that day ends there; the set runs until the installation it cut
 * would have ended, else the next start; a second swap on a day is refused unless `replace`; the set's installations that meet with the same
 * pairs are joined. Returns the live installations after it (a new one gets `id`, the change's own: the server keeps it).
 */
export function swap(
  sets: readonly SetState[], installations: readonly InstallationState[], setId: string, day: string, pairOneFront: boolean | null,
  { id, replace = false, pairsGiven = true, joined, sameCorners }: {
    id: string; replace?: boolean; pairsGiven?: boolean
    /** Told of each installation a join takes away from its first day (trashed into the one before it, or moved earlier), with that day. */
    joined?: (id: string, day: string) => void
    /** A set tracked tyre by tyre (#289): whether an installation has every tyre at the corner asked for, which decides a join as the pairs do. */
    sameCorners?: (installation: InstallationState) => boolean
  },
): InstallationState[] {
  // A broken-up set goes by its tyres' corners alone, and an installation from before the break-up keeps the pairs it recorded.
  const same = (s: InstallationState) => (sameCorners ? sameCorners(s) : s.pairOneFront === pairOneFront)
  const pairsOf = (s: InstallationState) => (sameCorners ? s.pairOneFront : pairOneFront)
  const set = sets.find((s) => s.id === setId)
  if (!set) throw new TyreRuleError('tyres.setNotFound')
  if (set.retiredOn) throw new TyreRuleError('tyres.setRetired', { set: set.name })
  let live = installations.map((s) => ({ ...s }))
  const covering = live.find((s) => covers(s, day))
  if (covering?.setId === setId) {
    if (covering.from !== day) throw new TyreRuleError('tyres.alreadyOnCar', { set: set.name, from: covering.from })
    if (pairsGiven) covering.pairOneFront = pairsOf(covering)
    return checked(sets, live, moved(installations, live))
  }
  let until: string | null
  if (covering && covering.from === day) {
    if (!replace) throw new TyreRuleError('tyres.swapExists', { date: day, set: sets.find((s) => s.id === covering.setId)?.name ?? '' })
    until = covering.until
    live = live.filter((s) => s !== covering)
  } else if (covering) {
    until = covering.until
    covering.until = day
  } else until = live.filter((s) => s.from > day).map((s) => s.from).sort()[0] ?? null

  const before = live.find((s) => s.setId === setId && s.until === day && same(s))
  // Into a gap, never the set's next installation: that one began with a swap of its own (only one this swap cut or replaced is carried on).
  const after = covering && until !== null ? live.find((s) => s.setId === setId && s.from === until && same(s)) : undefined
  if (before && after) {
    joined?.(after.id, after.from)
    before.until = after.until
    before.pairOneFront = pairsOf(before)
    live = live.filter((s) => s !== after)
  } else if (before) {
    before.until = until
    before.pairOneFront = pairsOf(before)
  } else if (after) {
    joined?.(after.id, after.from)
    after.from = day
    after.pairOneFront = pairsOf(after)
  }
  else live.push({ id, setId, from: day, until, pairOneFront })
  return checked(sets, live, moved(installations, live))
}

/** The set on the car on a day changes its pairs from that day (`TyreHistory.ChangePositions`). */
export function changePositions(
  sets: readonly SetState[], installations: readonly InstallationState[], setId: string, day: string, pairOneFront: boolean | null, id: string,
  /** A set tracked tyre by tyre (#289): whether the installation has every tyre at the corner asked for already. Its corners change in
   * place on the installation's first day (the places' to do), else the installation splits as a whole set's does. */
  sameCorners?: (installation: InstallationState) => boolean,
): InstallationState[] {
  const set = sets.find((s) => s.id === setId)
  const live = installations.map((s) => ({ ...s }))
  const covering = live.find((s) => s.setId === setId && covers(s, day))
  if (!set || !covering) throw new TyreRuleError('tyres.notOnCar', { set: set?.name ?? '', date: day })
  if (sameCorners ? sameCorners(covering) : covering.pairOneFront === pairOneFront) throw new TyreRuleError('tyres.positionsUnchanged')
  if (covering.from === day) {
    if (sameCorners) return live
    covering.pairOneFront = pairOneFront
    return checked(sets, live, moved(installations, live))
  }
  const until = covering.until
  covering.until = day
  live.push({ id, setId, from: day, until, pairOneFront })
  return checked(sets, live, moved(installations, live))
}

/**
 * The swap that began an installation moves to another day (`TyreHistory.MoveSwap`, its line's day changed, #288): it starts then, and the
 * installation that came off on its old first day comes off then instead (refused at or before that one's own first day).
 */
export function moveSwap(sets: readonly SetState[], installations: readonly InstallationState[], id: string, day: string): InstallationState[] {
  const live = installations.map((s) => ({ ...s }))
  const moving = live.find((s) => s.id === id)
  if (!moving) throw new TyreRuleError('tyres.installationTrashedCannotEdit')
  if (moving.from === day) return live
  const before = live.find((s) => s !== moving && s.until === moving.from)
  if (before) {
    if (day <= before.from) throw new TyreRuleError('tyres.swapBeforePrevious', { set: sets.find((x) => x.id === before.setId)?.name ?? '', from: before.from })
    before.until = day
  }
  if (moving.until !== null && moving.until <= day) throw new TyreRuleError('tyres.untilBeforeFrom', { from: day, until: moving.until })
  moving.from = day
  return checked(sets, live, moved(installations, live))
}

/**
 * Every installation fits (`TyreHistory.Check`, and the installation's own rule that it lasts a day): a set never twice at once, never two whole sets at
 * once, none of a retired set past the day it was retired. `moving`: the installations the change adds, edits or moves (an overlap names the
 * other one, the one already on the car then).
 */
export function checked(sets: readonly SetState[], installations: InstallationState[], moving?: ReadonlySet<string>): InstallationState[] {
  const ordered = [...installations].sort((a, b) => (a.from < b.from ? -1 : a.from > b.from ? 1 : 0))
  for (const s of ordered) {
    const set = sets.find((x) => x.id === s.setId)
    if (s.until !== null && s.until <= s.from) throw new TyreRuleError('tyres.untilBeforeFrom', { from: s.from, until: s.until })
    if (set?.retiredOn && (s.until === null || s.until > set.retiredOn)) throw new TyreRuleError('tyres.setRetired', { set: set.name })
  }
  for (let i = 0; i + 1 < ordered.length; i++) {
    const [a, b] = [ordered[i], ordered[i + 1]]
    if (a.until !== null && b.from >= a.until) continue
    // An overlap names the installation already on the car, not the one the change adds, edits or moves.
    const other = moving?.has(b.id) && !moving.has(a.id) ? a : b
    const args = { set: sets.find((x) => x.id === other.setId)?.name ?? '', from: other.from, until: other.until }
    throw new TyreRuleError(a.setId === b.setId ? 'tyres.setOnTwice' : 'tyres.twoSetsAtOnce', args)
  }
  return installations
}
