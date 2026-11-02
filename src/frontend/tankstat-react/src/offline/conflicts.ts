import type { ApolloClient, DocumentNode } from '@apollo/client'
import {
  ExpenseChangeValuesFragmentDoc,
  RefuelingChangeValuesFragmentDoc,
  ScheduleChangeValuesFragmentDoc,
  VehicleChangeValuesFragmentDoc,
  type ParkedChangeFieldsFragment,
} from '../gql/generated.ts'
import { UPDATABLE, type Change, type ChangeEntity } from './changes.ts'

/** The type and the fragment of the values a change of each entity can set (`graphql/conflicts.graphql`). */
const SHAPES: Record<ChangeEntity, { typename: string; fragment: DocumentNode }> = {
  refuelings: { typename: 'Refueling', fragment: RefuelingChangeValuesFragmentDoc },
  expenses: { typename: 'Expense', fragment: ExpenseChangeValuesFragmentDoc },
  vehicles: { typename: 'Vehicle', fragment: VehicleChangeValuesFragmentDoc },
  recurring: { typename: 'RecurringExpenseInfo', fragment: ScheduleChangeValuesFragmentDoc },
}

export type ChangeValues = Record<string, unknown>

/**
 * What a change of `entity` can set, as a loaded entry holds it, in the shape of the change's input (the entries name their fields as the
 * inputs do; `units` without its `__typename`). Only the fields the entry holds: a grid row may lack some.
 */
export function valuesOf(entity: ChangeEntity, loaded: Record<string, unknown> | null | undefined): ChangeValues {
  if (!loaded) return {}
  return Object.fromEntries(
    UPDATABLE[entity].filter((field) => loaded[field] !== undefined).map((field) => [field, withoutTypename(loaded[field])]),
  )
}

const withoutTypename = (value: unknown): unknown => {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return value
  return Object.fromEntries(Object.entries(value).filter(([key]) => key !== '__typename'))
}

/**
 * The values a change kept on this device is made from: the entry as this screen holds it (the cache, filled by the dialog that loaded it
 * or the list it is in), only when it is the version the change is made from, else none (a change without a base counts every field
 * that differs from the server's as changed on both sides).
 */
export function baseOf(client: ApolloClient, entity: ChangeEntity, id: string, version: number | null | undefined): ChangeValues | undefined {
  if (version == null) return undefined
  const { typename, fragment } = SHAPES[entity]
  const cacheId = client.cache.identify({ __typename: typename, id })
  if (!cacheId) return undefined
  const loaded = client.cache.readFragment<Record<string, unknown>>({ fragment, id: cacheId, returnPartialData: true })
  if (!loaded || loaded.version !== version) return undefined
  const values = valuesOf(entity, loaded)
  return Object.keys(values).length > 0 ? values : undefined
}

/** What a parked change concerns as it is on the server now (`ParkedChangeFields` `current`); null when it is gone or not to be seen. */
export type Current = ParkedChangeFieldsFragment['current']

/** The values of what is on the server now, in the shape of the change's input. */
export const currentValues = (entity: ChangeEntity, current: Current): ChangeValues =>
  current ? valuesOf(entity, current.refueling ?? current.expense ?? current.vehicle ?? current.schedule) : {}

/**
 * The fields an edit leaves as they are when it carries null for them (the server keeps what is there: `?? existing` in the services), so
 * a null there is "not touched", never a change and never a clear. Elsewhere null clears the value (a note, a category, a plate).
 */
export const KEEP_WHEN_NULL: Record<ChangeEntity, readonly string[]> = {
  refuelings: ['currency', 'missedPreviousFillUp', 'discount'],
  expenses: ['currency'],
  vehicles: ['units'],
  recurring: ['lastDoneDate', 'warnDays', 'warnDistance'],
}

/** Whether two values of a field are the same value: blank and null alike, "40" and 40 alike, objects by their fields. */
export function same(a: unknown, b: unknown): boolean {
  const x = normalise(a)
  const y = normalise(b)
  if (x && y && typeof x === 'object' && typeof y === 'object') {
    const keys = new Set([...Object.keys(x), ...Object.keys(y)].filter((k) => k !== '__typename'))
    return [...keys].every((k) => same((x as Record<string, unknown>)[k], (y as Record<string, unknown>)[k]))
  }
  return x === y
}

const normalise = (value: unknown): unknown => {
  if (value === undefined || value === null) return null
  if (typeof value === 'string') {
    const text = value.trim()
    if (text === '') return null
    const number = Number(text)
    return /^-?\d+(\.\d+)?$/.test(text) && Number.isFinite(number) ? number : text
  }
  return value
}

/**
 * The fields what is on the server now has changed since the values a change was made from (its `base`): only fields both know. None
 * without a base.
 */
export function changedOnServer(entity: ChangeEntity, base: ChangeValues | undefined, current: Current): string[] {
  if (!base || !current) return []
  const theirs = currentValues(entity, current)
  return UPDATABLE[entity].filter((field) => field in base && field in theirs && !same(base[field], theirs[field]))
}

/**
 * Where a parked edit or trash stands, against what is on the server now:
 * - a trash: `changed` (still there, changed meanwhile), `restored` (trashed and brought back meanwhile), `alreadyTrashed` (nothing left to
 *   do), `gone` (purged, deleted for good, or not to be seen);
 * - an edit: `trashedMeanwhile` (it can come back with the edit), `gone`, or `edited` (changed meanwhile: to be merged);
 * - null for anything else (adds, restores, visits, photos, an edit a rule refused), which is decided as it always was.
 */
export type Situation = 'changed' | 'restored' | 'alreadyTrashed' | 'gone' | 'trashedMeanwhile' | 'edited'

export function situationOf(change: Pick<Change, 'action' | 'entity'>, current: Current, reasonKey: string | null | undefined): Situation | null {
  if (change.action === 'trash') {
    if (!current) return 'gone'
    if (current.state === 'TRASHED') return 'alreadyTrashed'
    return current.lastChange === 'RESTORED' ? 'restored' : 'changed'
  }
  if (change.action === 'update') {
    if (!current) return reasonKey?.endsWith('.notFound') ? 'gone' : null
    if (current.state === 'TRASHED') return 'trashedMeanwhile'
    return reasonKey === 'sync.versionMismatch' ? 'edited' : null
  }
  return null
}

/** How a field of an edit and what is on the server now came together. */
export type MergedAs = 'both' | 'mine' | 'theirs' | 'same'

export interface Merged {
  /** The merged values: the one side's where only one changed, this device's where both did (the person picks), the shared one else. */
  values: ChangeValues
  /** Per field the edit sets, in `UPDATABLE` order, how it came together (fields the edit leaves as they are are not listed). */
  fields: { field: string; as: MergedAs }[]
}

/**
 * Merges an edit made on this device (`mine`, its input) with what is on the server now (`theirs`), from the values it was made from
 * (`base`): a field changed on one side only takes that side's value; one changed on both sides (or whose starting value is unknown) is
 * `both` and starts with this device's, for the person to decide. A null on a field the server keeps when it gets null
 * (`KEEP_WHEN_NULL`) is no change at all: it stays as the server has it.
 */
export function merge(entity: ChangeEntity, base: ChangeValues | undefined, mine: ChangeValues, theirs: ChangeValues): Merged {
  const values: ChangeValues = {}
  const fields: Merged['fields'] = []
  for (const field of UPDATABLE[entity]) {
    if (!(field in mine)) {
      if (field in theirs) values[field] = theirs[field]
      continue
    }
    const m = mine[field]
    if (!(field in theirs)) {
      values[field] = m
      continue
    }
    const th = theirs[field]
    if (m == null && KEEP_WHEN_NULL[entity].includes(field)) {
      values[field] = th
      continue
    }
    let as: MergedAs
    if (same(m, th)) as = 'same'
    else if (base && field in base) {
      const mineChanged = !same(m, base[field])
      const theirsChanged = !same(th, base[field])
      as = mineChanged && !theirsChanged ? 'mine' : !mineChanged && theirsChanged ? 'theirs' : 'both'
    } else as = 'both'
    values[field] = as === 'theirs' ? th : m
    fields.push({ field, as })
  }
  return { values, fields }
}
