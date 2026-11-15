import Box from '@mui/material/Box'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useId, type ReactNode, type Ref } from 'react'
import { useTranslation } from 'react-i18next'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import type { TyreEntryKind } from '../gql/generated.ts'
import { isSwapLine, labelsOf as tyreLabelsOf, sharersOf, useEntryWords } from './entries.ts'
import { ForTyres } from './ForTyres.tsx'
import type { TyreEntry, Tyres } from './tyres.ts'
import { EXPENSE_KINDS, labelsOf, offered } from './expenseTyreInput.ts'
import type { TyreDetailsState } from './useExpenseTyreDetails.ts'
/**
 * The tyre details of the Expense (and Done) dialog (#290), loaded only when a vehicle has tyre sets: *What was done* (*Choose…* at first,
 * the kinds that take an amount), *Set* (those not retired, and *No set*) and *For*, as in *Add entry*: the whole set, or the tyres tapped
 * (a whole set's label per corner, broken up on save). A group named by its heading; the note under it says what the line takes.
 */
export default function ExpenseTyreSection({
  tyres,
  state,
  onChange,
  kindError,
  kindRef,
  note,
  linked,
  kindNote,
}: {
  tyres: Tyres
  state: TyreDetailsState
  onChange: (next: TyreDetailsState) => void
  /** "Choose what was done." after a save without a kind. */
  kindError?: string
  kindRef?: Ref<{ focus: () => void }>
  /** What the line takes (the expense's date, odometer and amount). */
  note: string
  /**
   * An expense the tyre log made (decisions 53, 60): its first live line. What was done changes every live line holding the expense; a
   * whole set's line may move to another set or none; a tyre's line keeps its set and says its tyres; a swap's line is locked whole.
   */
  linked?: TyreEntry | null
  /** Under *What was done*: what a photo says was done (#290, decision 67), "Read from the photo" or offered. */
  kindNote?: ReactNode
}) {
  const { t } = useTranslation()
  const words = useEntryWords()
  const headingId = useId()
  const swap = !!linked && isSwapLine(linked)
  const ofTyre = !!linked?.tyreId
  const holders = linked ? [linked, ...sharersOf(tyres, linked)] : []
  // A line's own set stays offered while it is retired: it is what the line says.
  const sets = offered(tyres).concat(linked?.setId ? tyres.sets.filter((s) => s.set.id === linked.setId && s.set.isRetired) : [])
  const info = linked ? undefined : sets.find((s) => s.set.id === state.setId)
  const kinds = swap ? [linked.kind] : EXPENSE_KINDS
  const lockedHint = swap ? t('tyres.entries.lockedHint') : undefined
  return (
    <Stack role="group" aria-labelledby={headingId} sx={{ gap: 1.5, p: 1.5, borderRadius: 2, bgcolor: 'action.hover' }}>
      <Typography id={headingId} component="h3" variant="subtitle2">
        {t('expenses.tyreDetails.switch')}
      </Typography>
      <Box sx={{ display: 'grid', gap: 1.5, gridTemplateColumns: { xs: 'minmax(0,1fr)', md: 'repeat(2, minmax(0,1fr))' } }}>
        <LabeledSelect
          label={t('tyres.entries.kind')}
          value={state.kind}
          options={[...(linked ? [] : [{ value: '', label: t('expenses.tyreDetails.choose') }]), ...kinds.map((k) => ({ value: k, label: words.kind(k) }))]}
          disabled={swap}
          hint={lockedHint ?? (holders.length > 1 ? t('expenses.tyreDetails.sharedHint', { tyres: tyreLabelsOf(tyres, holders) }) : undefined)}
          error={kindError}
          selectRef={kindRef}
          extra={kindNote}
          onChange={(kind) => onChange({ ...state, kind: kind as TyreEntryKind | '' })}
        />
        <LabeledSelect
          label={t('tyres.entries.set')}
          value={state.setId}
          options={[...sets.map((s) => ({ value: s.set.id, label: `${t(`tyres.seasons.${s.set.season}`)}: ${s.set.name}` })), { value: '', label: t('tyres.entries.noSet') }]}
          disabled={swap || ofTyre}
          hint={lockedHint ?? (ofTyre ? t('tyres.entries.tyreSetHint') : undefined)}
          onChange={(setId) => onChange({ ...state, setId, whole: true, picked: [], labels: [] })}
        />
      </Box>
      {ofTyre && (
        <Typography variant="body2">
          {t('tyres.entries.for')}: {tyreLabelsOf(tyres, holders)}
        </Typography>
      )}
      {info && (
        <ForTyres
          info={info}
          whole={state.whole}
          onWhole={(whole) => onChange({ ...state, whole, picked: [] })}
          picked={state.picked}
          onPicked={(picked) => onChange({ ...state, picked })}
          labels={labelsOf(state, tyres)}
          onLabels={(labels) => onChange({ ...state, labels })}
        />
      )}
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {note}
      </Typography>
    </Stack>
  )
}
