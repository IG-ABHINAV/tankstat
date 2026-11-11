import { useTranslation } from 'react-i18next'
import type { TyreEntryKind } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
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

/** The words of a line: its kind, its set's name (or none) and its day, as the log names it to a screen reader. */
export function useEntryWords() {
  const { t } = useTranslation()
  const format = useFormat()
  const kind = (k: TyreEntryKind) => t(`tyres.entries.kinds.${k}`)
  const setName = (tyres: Tyres, setId: string | null | undefined) => (setId ? (tyres.sets.find((s) => s.set.id === setId)?.set.name ?? null) : null)
  const name = (tyres: Tyres, entry: TyreEntry) => {
    const set = setName(tyres, entry.setId)
    const day = format.date(entry.day)
    return set ? t('tyres.entries.lineName', { kind: kind(entry.kind), set, day }) : t('tyres.entries.lineNameNoSet', { kind: kind(entry.kind), day })
  }
  return { kind, setName, name }
}
