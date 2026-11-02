import type { ReadingFieldName } from '../gql/generated.ts'
import { parseDecimal } from '../i18n/format.ts'

export interface ReadValue {
  value: string
  confidence: number
}

/** What the photos of the open dialog showed: the surest value of each name. */
export type ReadValues = Partial<Record<ReadingFieldName, ReadValue>>

interface ReadingLike {
  status: string
  values: readonly { name: ReadingFieldName; value: string; confidence: number }[]
}

/** The values of every finished reading, keeping the surest one when several photos show the same thing. */
export function mergeReadings(readings: Iterable<ReadingLike | null | undefined>, follows: FollowsRead = {}): ReadValues {
  const merged: ReadValues = {}
  const from = new Map<ReadingFieldName, ReadingLike>()
  const read = [...readings].filter((r): r is ReadingLike => r?.status === 'READ')
  for (const reading of read) {
    for (const v of reading.values) {
      const known = merged[v.name]
      if (!known || known.confidence < v.confidence) {
        merged[v.name] = { value: v.value, confidence: v.confidence }
        from.set(v.name, reading)
      }
    }
  }
  // A value that follows another comes from the photo that gave that one, or not at all: a discount was taken off its own total.
  for (const [name, leader] of Object.entries(follows) as [ReadingFieldName, ReadingFieldName][]) {
    const own = from.get(leader)?.values.find((v) => v.name === name)
    if (own) merged[name] = { value: own.value, confidence: own.confidence }
    else delete merged[name]
  }
  return merged
}

/** Read values that only ever come with another one, from the same photo: `{ DISCOUNT: 'TOTAL' }`. */
export type FollowsRead = Partial<Record<ReadingFieldName, ReadingFieldName>>

/** Whether what is typed already says what the photo shows ("38,52" and "38.52" do, so do "huf" and "HUF"). */
export function sameValue(typed: string, read: string): boolean {
  const a = typed.trim()
  const b = read.trim()
  if (a.toLowerCase() === b.toLowerCase()) return true
  const x = parseDecimal(a)
  const y = parseDecimal(b)
  return x !== undefined && y !== undefined && x === y
}

export interface FillState<F extends string> {
  values: Record<F, string>
  /** Fields the user typed in (or took a photo's value for): a photo never overwrites them, it only offers its value. */
  touched: ReadonlySet<F>
  /** Fields that hold what a photo showed (until the user changes them). */
  filled: Partial<Record<F, string>>
  /** What a photo showed where it differs from what the user typed: offered with "Use". */
  offered: Partial<Record<F, string>>
  /** Fields that hold a value worked out from other fields (see `calculateField`): a photo only offers its value there too. */
  calculated: ReadonlySet<F>
  /**
   * Fields that hold a saved value (an edit dialog): a photo only offers its value there while they are not empty. Kept apart from
   * `touched`: a saved value is not one the user typed (it may still give way when amounts are kept in step).
   */
  kept?: ReadonlySet<F>
}

const without = <F extends string>(set: ReadonlySet<F>, field: F) => {
  const next = new Set(set)
  next.delete(field)
  return next
}

/**
 * Puts read values into the fields the user has not changed (their starting values, such as today's date or the last currency, count as
 * unchanged) and into fields left empty (the user cleared them, so the photo may give the value); for fields the user typed in, and for
 * calculated ones (they follow what the user typed), a different value is only offered. Returns the new state and the fields filled just now.
 */
export function applyRead<F extends string>(
  state: FillState<F>,
  fields: Partial<Record<F, ReadingFieldName>>,
  read: ReadValues,
  follows: Partial<Record<F, F>> = {},
): { state: FillState<F>; newly: F[] } {
  const values = { ...state.values }
  const filled = { ...state.filled }
  const offered = { ...state.offered }
  let touched = state.touched
  const newly: F[] = []
  const theirs = (field: F) => (state.touched.has(field) || state.kept?.has(field) === true) && state.values[field].trim() !== ''
  for (const field of Object.keys(fields) as F[]) {
    const value = read[fields[field]!]?.value
    if (value === undefined) continue
    // A field that follows another (the discount, the total) is the user's while the other is: next to an amount they entered, a photo's
    // discount is only offered, empty or not, since it was taken off the photo's own total.
    const leader = follows[field]
    const holdsTheirs = theirs(field) || (leader !== undefined && theirs(leader))
    if (holdsTheirs || state.calculated.has(field)) {
      if (sameValue(values[field], value)) delete offered[field]
      else offered[field] = value
      continue
    }
    if (touched.has(field)) touched = without(touched, field) // emptied by the user: filled like an untouched field
    if (filled[field] === value && values[field] === value) continue
    values[field] = value
    filled[field] = value
    delete offered[field]
    newly.push(field)
  }
  return { state: { ...state, values, filled, offered, touched }, newly }
}

/** The user typed in a field: it is theirs now, and the photo's mark goes once it no longer says what the photo showed. */
export function changeField<F extends string>(state: FillState<F>, field: F, value: string): FillState<F> {
  const filled = { ...state.filled }
  const offered = { ...state.offered }
  if (filled[field] !== value) delete filled[field]
  if (offered[field] !== undefined && sameValue(value, offered[field])) delete offered[field]
  const touched = new Set(state.touched).add(field)
  return { ...state, values: { ...state.values, [field]: value }, touched, filled, offered, calculated: without(state.calculated, field) }
}

/** "Use": the field takes the value the photo showed, as if the user had typed it (and it still says what the photo showed). */
export function takeOffered<F extends string>(state: FillState<F>, field: F): FillState<F> {
  const value = state.offered[field]
  if (value === undefined) return state
  const typed = changeField(state, field, value)
  return { ...typed, filled: { ...typed.filled, [field]: value } }
}

/**
 * A value worked out from other fields: it is no longer the user's (nor what a photo showed), so it gives way when they change again. A
 * photo's value that differs stays offered.
 */
export function calculateField<F extends string>(state: FillState<F>, field: F, value: string): FillState<F> {
  const filled = { ...state.filled }
  const offered = { ...state.offered }
  delete filled[field]
  if (offered[field] !== undefined && sameValue(value, offered[field])) delete offered[field]
  const calculated = new Set(state.calculated).add(field)
  return { ...state, values: { ...state.values, [field]: value }, touched: without(state.touched, field), filled, offered, calculated }
}

/** Empties a calculated field whose value can no longer be worked out (what it came from is gone), so it never shows a stale value. */
export function clearCalculated<F extends string>(state: FillState<F>, field: F): FillState<F> {
  if (!state.calculated.has(field)) return state
  return { ...state, values: { ...state.values, [field]: '' }, calculated: without(state.calculated, field) }
}
