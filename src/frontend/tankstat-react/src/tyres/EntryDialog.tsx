import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
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
import { ENTRY_KINDS, isSwapLine, useEntryWords } from './entries.ts'
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
}: {
  trigger: ReactNode
  vehicleId: string
  unit: DistanceUnit
  tyres: Tyres
  /** The line to change; none adds one. */
  entry?: TyreEntry
}) {
  const { t } = useTranslation()
  const words = useTyreWords(unit)
  const entryWords = useEntryWords()
  const notify = useTyreToasts()
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState()
  const id = useClientId(open)
  const locked = !!entry && isSwapLine(entry)
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
      const values = { kind, setId: setId || null, day: text('day'), odometer: wholeOrNull(text('odometer')), note: text('note') || null }
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
                <Field name="day" label={t('tyres.fields.day')} hint={locked ? t('tyres.entries.swapDayHint') : undefined} required>
                  <FieldDate disableFuture value={day} onChange={setDay} />
                </Field>
                <Field
                  name="odometer"
                  label={t('tyres.fields.odometer', { unit: words.unitShort })}
                  hint={`${t('tyres.entries.odometerHint')} ${words.odometerText(tyres.readings, day)}`}
                  invalid={wholeInvalid(t('forms.numberInvalid'))}
                >
                  <FieldInput inputMode="numeric" autoComplete="off" defaultValue={entry?.odometer == null ? '' : String(entry.odometer)} />
                </Field>
              </Stack>
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
 * car no longer counts); for a line with a reading, that the reading no longer counts until the line is restored. Undo restores it.
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
  if (entry.odometer != null) parts.push(t('tyres.entries.trashReading', { value: words.distance(entry.odometer) }))
  parts.push(t('tyres.entries.restorable'))

  const trash = async () => {
    try {
      const saved = await run(DeleteTyreEntryDocument, { id: entry.id })
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
    />
  )
}
