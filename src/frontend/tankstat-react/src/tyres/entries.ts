import { useTranslation } from 'react-i18next'
import type { TyreEntryKind, TyreFeeInput } from '../gql/generated.ts'
import { parseDecimal, useFormat } from '../i18n/format.ts'
import type { TyreEntry, Tyres } from './tyres.ts'

/** The kinds a line is added as; a swap and a change of positions come from those dialogs, with their installation (#288). */
export const ENTRY_KINDS: readonly TyreEntryKind[] = ['PUNCTURE_REPAIRED', 'VALVE_REPLACED', 'WHEEL_BALANCING', 'STORAGE_FEE', 'BOUGHT', 'OTHER', 'NOTE']

/** The line of an installation: its kind and set are locked, its day is the swap's. */
export const isSwapLine = (entry: Pick<TyreEntry, 'kind'>) => entry.kind === 'SWAP' || entry.kind === 'POSITIONS_CHANGED'

/** The words of a line: its kind, its set's name (or none) and its day, as the log names it to a screen reader. */
export function useEntryWords() {
  const { t } = useTranslation()
  const format = useFormat()
  const kind = (k: TyreEntryKind) => t(`tyres.entries.kinds.${k}`)
  const setName = (tyres: Tyres, setId: string | null | undefined) => (setId ? (tyres.sets.find((s) => s.set.id === setId)?.set.name ?? null) : null)
  /** Its amount (#288), while its expense is not in the trash. */
  const amount = (entry: TyreEntry) => (entry.amount != null && !entry.expenseTrashed ? format.money(entry.amount, entry.currency ?? '') : null)
  const name = (tyres: Tyres, entry: TyreEntry) => {
    const set = setName(tyres, entry.setId)
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
