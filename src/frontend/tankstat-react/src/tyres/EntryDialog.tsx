import Button from '@mui/material/Button'
import Checkbox from '@mui/material/Checkbox'
import FormControlLabel from '@mui/material/FormControlLabel'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Trash2 } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../components/ConfirmDialog.tsx'
import { IconAction } from '../components/IconAction.tsx'
import { OfflineNote } from '../components/OfflineNote.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../dialogs/DialogTrigger.tsx'
import { useClientId } from '../dialogs/useClientId.ts'
import { useDialogState } from '../dialogs/useDialogState.ts'
import { todayIso } from '../forms/dates.ts'
import { Field } from '../forms/Field.tsx'
import { FieldDate } from '../forms/FieldDate.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import { Form } from '../forms/Form.tsx'
import {
  AddTyreEntryDocument,
  DeleteTyreEntryDocument,
  RestoreTyreEntryDocument,
  UpdateTyreEntryDocument,
  type DistanceUnit,
  type TyreEntryKind,
} from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { ErrorMessage } from '../messages.tsx'
import { ENTRY_KINDS, feeOf, isSwapLine, useEntryWords } from './entries.ts'
import { FeeFields } from './FeeFields.tsx'
import { type TyreEntry, type Tyres, useTyreMutation, useTyreToasts, useTyreWords, wholeInvalid, wholeOrNull } from './tyres.ts'

/**
 * Adds a line to the tyre log, or changes one (#288): what was done, the set (or none), the day, the day's reading and a note. A swap's line
 * keeps its kind and set (both shown, disabled, with why in the hint); its day can change, which moves the swap, by the history's rules: a
 * refusal (`tyres.swapBeforePrevious`, `tyres.twoSetsAtOnce`, ...) is said here.
 */
export function EntryDialog({
  trigger,
  vehicleId,
  unit,
  tyres,
  entry,
  open: openAsked,
  onOpenChange,
}: {
  trigger: ReactNode
  vehicleId: string
  unit: DistanceUnit
  tyres: Tyres
  /** The line to change; none adds one. */
  entry?: TyreEntry
  /** Opened by something else (an expense's *Open in the tyre log*). */
  open?: boolean
  onOpenChange?: (open: boolean) => void
}) {
  const { t } = useTranslation()
  const words = useTyreWords(unit)
  const entryWords = useEntryWords()
  const notify = useTyreToasts()
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState({ open: openAsked, onOpenChange })
  const id = useClientId(open)
  const expenseId = useClientId(open)
  const locked = !!entry && isSwapLine(entry)
  // Its expense (#288): the amount, live or in the trash, and whether the day and the reading are shared with one now.
  const linked = !!entry?.expenseId && !entry.expenseTrashed
  const [kind, setKind] = useState<TyreEntryKind>(entry?.kind ?? 'PUNCTURE_REPAIRED')
  const [setId, setSet] = useState(entry?.setId ?? '')
  const [day, setDay] = useState(entry?.day ?? todayIso())
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)

  const reset = () => {
    setKind(entry?.kind ?? 'PUNCTURE_REPAIRED')
    setSet(entry?.setId ?? (tyres.sets.find((s) => s.onTheCar)?.set.id ?? ''))
    setDay(entry?.day ?? todayIso())
    setError(undefined)
  }

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    const text = (name: string) => String(form.get(name) ?? '').trim()
    setBusy(true)
    setError(undefined)
    try {
      // A note takes no amount: one it had (another kind before) goes, with its expense, to the trash.
      const fee = kind === 'NOTE'
        ? entry?.expenseId ? { amount: null } : null
        : feeOf(form, entryWords.feeTitle(kind, entryWords.setName(tyres, setId || null)), entry?.expenseId ?? expenseId) // its own expense, when it has one
      const values = { kind, setId: setId || null, day: text('day'), odometer: wholeOrNull(text('odometer')), note: text('note') || null, ...(fee ? { fee } : {}) }
      const saved = entry
        ? await run(UpdateTyreEntryDocument, { input: { id: entry.id, ...values } })
        : await run(AddTyreEntryDocument, { input: { vehicleId, id, ...values } })
      setOpen(false)
      notify.saved(saved, t('toast.saved'))
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  const kinds = locked ? [entry.kind] : ENTRY_KINDS
  const sets = [
    { value: '', label: t('tyres.entries.noSet') },
    ...tyres.sets.map((s) => ({ value: s.set.id, label: `${t(`tyres.seasons.${s.set.season}`)}: ${s.set.name}` })),
  ]
  return (
    <>
      <DialogTrigger
        trigger={trigger}
        open={open}
        onOpen={() => {
          reset() // the line or the sets may have changed since the dialog was mounted
          setOpen(true)
        }}
      />
      <DialogFrame
        open={open}
        onClose={() => setOpen(false)}
        onClosed={reset}
        busy={busy}
        maxWidth={560}
        title={entry ? t('tyres.entries.editTitle') : t('tyres.entries.addTitle')}
        description={t('tyres.entries.description')}
      >
        {open && (
          <Form onSubmit={submit} refusal={error}>
            <OfflineNote />
            <Stack sx={{ gap: 1.5 }}>
              <Stack sx={{ display: 'grid', gap: 1.5, gridTemplateColumns: { xs: 'minmax(0,1fr)', md: 'repeat(2, minmax(0,1fr))' } }}>
                <LabeledSelect
                  label={t('tyres.entries.kind')}
                  value={kind}
                  disabled={locked}
                  hint={locked ? t('tyres.entries.lockedHint') : undefined}
                  options={kinds.map((k) => ({ value: k, label: entryWords.kind(k) }))}
                  onChange={setKind}
                />
                <LabeledSelect
                  label={t('tyres.entries.set')}
                  value={setId}
                  disabled={locked}
                  hint={locked ? t('tyres.entries.lockedHint') : undefined}
                  options={sets}
                  onChange={setSet}
                />
                <Field
                  name="day"
                  label={t('tyres.fields.day')}
                  hint={[locked ? t('tyres.entries.swapDayHint') : '', linked ? t(locked ? 'tyres.fee.swapDayExpense' : 'tyres.fee.linkedDayHint') : ''].filter(Boolean).join(' ') || undefined}
                  required
                >
                  <FieldDate disableFuture value={day} onChange={setDay} />
                </Field>
                <Field
                  name="odometer"
                  label={t('tyres.fields.odometer', { unit: words.unitShort })}
                  hint={[t(linked ? 'tyres.fee.linkedOdometerHint' : 'tyres.entries.odometerHint'), words.neighbourText(tyres.readings.filter((r) => r.date !== entry?.day || r.value !== entry?.odometer), day)].filter(Boolean).join(' ')}
                  invalid={wholeInvalid(t('forms.numberInvalid'))}
                >
                  <FieldInput inputMode="numeric" autoComplete="off" defaultValue={entry?.odometer == null ? '' : String(entry.odometer)} />
                </Field>
              </Stack>
              {kind !== 'NOTE' && (
                <FeeFields
                  vehicleId={vehicleId}
                  tyres={tyres}
                  label={t('tyres.fee.paid')}
                  amount={entry?.amount}
                  currency={entry?.currency}
                  category={entry?.category}
                  linked={linked}
                />
              )}
              <Field name="note" label={t('tyres.fields.note')}>
                <FieldInput maxLength={500} autoComplete="off" defaultValue={entry?.note ?? ''} />
              </Field>
              {error !== undefined && <ErrorMessage error={error} />}
              <DialogButtons>
                <DialogCancel />
                <Button type="submit" disabled={busy}>
                  {t('tyres.dialogs.save')}
                </Button>
              </DialogButtons>
            </Stack>
          </Form>
        )}
      </DialogFrame>
    </>
  )
}

/**
 * Moves a line to the trash, asking first (#288): the line in one sentence; for a swap's line, that the swap goes with it (its time on the
 * car no longer counts); with an expense, a box (ticked at first) to move that along too, else it stays with the reading; and when the
 * reading goes with them, that it no longer counts until the line is restored. Undo restores it (its expense with it when it went along).
 */
export function TrashEntryDialog({
  vehicleId,
  unit,
  tyres,
  entry,
  onError,
}: {
  vehicleId: string
  unit: DistanceUnit
  tyres: Tyres
  entry: TyreEntry
  onError: (error: unknown) => void
}) {
  const { t } = useTranslation()
  const format = useFormat()
  const words = useTyreWords(unit)
  const entryWords = useEntryWords()
  const notify = useTyreToasts()
  const run = useTyreMutation(vehicleId)
  const installation = tyres.sets.flatMap((s) => s.installations).find((x) => x.installation.id === entry.installationId)?.installation
  const set = entryWords.setName(tyres, entry.setId) ?? ''
  const parts = [`${entryWords.name(tyres, entry)}${entry.note ? ` · ${entry.note}` : ''}.`]
  if (installation)
    parts.push(
      installation.until
        ? t('tyres.entries.trashSwapUntil', { set, from: format.date(installation.from), until: format.date(installation.until) })
        : t('tyres.entries.trashSwap', { set, from: format.date(installation.from) }),
    )
  const expense = entry.expenseId && !entry.expenseTrashed && entry.amount != null
  const [withExpense, setWithExpense] = useState(true)
  if (!expense && entry.odometer != null) parts.push(t('tyres.entries.trashReading', { value: words.distance(entry.odometer) }))
  parts.push(t('tyres.entries.restorable'))
  const reading = entry.odometer != null ? words.distance(entry.odometer) : null

  const trash = async () => {
    try {
      const saved = await run(DeleteTyreEntryDocument, { input: { id: entry.id, withExpense: !!expense && withExpense } })
      notify.undoable(saved, t('tyres.toast.trashed'), () => run(RestoreTyreEntryDocument, { id: entry.id }))
    } catch (err) {
      onError(err)
    }
  }
  return (
    <ConfirmDialog
      trigger={
        <IconAction label={t('tyres.entries.trashName', { kind: entryWords.kind(entry.kind), day: format.date(entry.day) })} tone="error">
          <Trash2 size={16} aria-hidden />
        </IconAction>
      }
      title={t('tyres.entries.trashTitle')}
      description={parts.join(' ')}
      confirmLabel={t('tyres.dialogs.toTrash')}
      onConfirm={() => void trash()}
      onClosed={() => setWithExpense(true)}
    >
      {expense && (
        <Stack sx={{ gap: 0.5, mb: 1 }}>
          <FormControlLabel
            control={<Checkbox checked={withExpense} onChange={(e) => setWithExpense(e.target.checked)} />}
            label={t('tyres.fee.alsoExpense', { amount: format.money(entry.amount!, entry.currency ?? '') })}
          />
          <Typography variant="body2" sx={{ color: 'text.secondary' }} aria-live="polite">
            {withExpense
              ? reading && t('tyres.fee.readingWithExpense', { value: reading })
              : t(reading ? 'tyres.fee.staysWithReading' : 'tyres.fee.stays')}
          </Typography>
        </Stack>
      )}
    </ConfirmDialog>
  )
}
