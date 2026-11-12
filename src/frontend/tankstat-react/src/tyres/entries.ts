import { useTranslation } from 'react-i18next'
import type { TyreEntryKind, TyreFeeInput } from '../gql/generated.ts'
import { parseDecimal, useFormat } from '../i18n/format.ts'
import type { TyreEntry, Tyres } from './tyres.ts'

/** The kinds a line is added as; a swap and a change of positions come from those dialogs, with their installation (#288). */
export const ENTRY_KINDS: readonly TyreEntryKind[] = ['PUNCTURE_REPAIRED', 'VALVE_REPLACED', 'WHEEL_BALANCING', 'STORAGE_FEE', 'BOUGHT', 'OTHER', 'NOTE']

/** The line of an installation: its kind and set are locked, its day is the swap's. */
export const isSwapLine = (entry: Pick<TyreEntry, 'kind'>) => entry.kind === 'SWAP' || entry.kind === 'POSITIONS_CHANGED'

/**
 * A line this device made in the server's place while its change waits (`replay.ts`: a Note line that keeps a reading or a note): its id is
 * the device's own (`<change>:note:<day>`, `<change>:kept:<line>`) and the server gives the line one of its own, so a change of it could
 * never be sent. It is shown, and changed once synced.
 */
export const isDeviceLine = (entry: Pick<TyreEntry, 'id'>) => entry.id.includes(':')

/** A row of the tyre log (#289): a line, or the lines made together for several tyres, shown once with the amount once. */
export interface LogRow {
  /** The first of them, as the server lists them: what the row shows and opens. */
  lead: TyreEntry
  lines: TyreEntry[]
}

/** The log's rows, in the server's order: the lines of one group together where the first of them is. */
export function logRows(entries: readonly TyreEntry[]): LogRow[] {
  const rows: LogRow[] = []
  const groups = new Map<string, LogRow>()
  for (const e of entries) {
    const group = e.groupId ? groups.get(e.groupId) : undefined
    if (group) {
      group.lines.push(e)
      continue
    }
    const row = { lead: e, lines: [e] }
    rows.push(row)
    if (e.groupId) groups.set(e.groupId, row)
  }
  return rows
}

/** The lines made together with a line (#289), itself first; just it when it has no group. */
export const groupOf = (tyres: Tyres, entry: TyreEntry): TyreEntry[] =>
  entry.groupId ? [entry, ...tyres.entries.filter((e) => e.groupId === entry.groupId && e.id !== entry.id)] : [entry]

/** The other lines holding a line's live expense (#289: lines made together share one, also after one left the group). */
export const sharersOf = (tyres: Tyres, entry: TyreEntry): TyreEntry[] =>
  entry.expenseId && !entry.expenseTrashed ? tyres.entries.filter((e) => e.expenseId === entry.expenseId && e.id !== entry.id) : []

/** A single tyre's label, from any set. */
export const labelOf = (tyres: Tyres, tyreId: string | null | undefined): string | null =>
  tyreId ? (tyres.sets.flatMap((s) => s.tyres ?? []).find((t) => t.tyre.id === tyreId)?.tyre.label ?? null) : null

/** The labels of the lines' tyres, in the order the server sorts tyres (by label), joined for a sentence ("S3, S4"). */
export function labelsOf(tyres: Tyres, lines: readonly TyreEntry[]): string {
  const collator = new Intl.Collator(undefined, { sensitivity: 'base' })
  return lines.map((l) => labelOf(tyres, l.tyreId)).filter((l): l is string => !!l).sort(collator.compare).join(', ')
}

/** The words of a line: its kind, its set's name (or none) and its day, as the log names it to a screen reader. */
export function useEntryWords() {
  const { t } = useTranslation()
  const format = useFormat()
  const kind = (k: TyreEntryKind) => t(`tyres.entries.kinds.${k}`)
  const setName = (tyres: Tyres, setId: string | null | undefined) => (setId ? (tyres.sets.find((s) => s.set.id === setId)?.set.name ?? null) : null)
  /** Its amount (#288), while its expense is not in the trash. */
  const amount = (entry: TyreEntry) => (entry.amount != null && !entry.expenseTrashed ? format.money(entry.amount, entry.currency ?? '') : null)
  /** A row of lines made together (#289) names its tyres after the set ("Puncture repaired, Michelin · S3, S4, …"). */
  const name = (tyres: Tyres, entry: TyreEntry, lines: readonly TyreEntry[] = [entry]) => {
    const labels = labelsOf(tyres, lines)
    const ofSet = setName(tyres, entry.setId)
    const set = ofSet && labels ? t('tyres.entries.tyresOf', { set: ofSet, tyres: labels }) : ofSet
    const day = format.date(entry.day)
    const named = set ? t('tyres.entries.lineName', { kind: kind(entry.kind), set, day }) : t('tyres.entries.lineNameNoSet', { kind: kind(entry.kind), day })
    const paid = amount(entry)
    return paid ? `${named}, ${paid}` : named
  }
  /** The title of the expense an amount makes: what was done, and the set ("Puncture repaired: Michelin Primacy 4"). */
  const feeTitle = (k: TyreEntryKind, set: string | null) => (set ? `${kind(k)}: ${set}` : kind(k))
  return { kind, setName, name, feeTitle, amount }
}

/**
 * The fee a form's `FeeFields` hold (#288): the amount (none when empty, which takes a line's expense away), its currency and category, the
 * expense's title (worded here, in the user's language: the server has no texts) and the client's id for the expense it would make.
 */
export function feeOf(form: FormData, title: string, expenseId: string): TyreFeeInput {
  const text = (name: string) => String(form.get(name) ?? '').trim()
  const amount = text('feeAmount') === '' ? null : (parseDecimal(text('feeAmount')) ?? null)
  return { amount, currency: text('feeCurrency').toUpperCase() || null, category: text('feeCategory') || null, title, expenseId }
}
