import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { LabeledSwitch } from './components/LabeledSwitch.tsx'
import { OdometerField } from './components/OdometerField.tsx'
import { PhotoGallery } from './components/PhotoGallery.tsx'
import { PhotosLeftOut } from './components/PhotosLeftOut.tsx'
import type { Saved } from './components/usePhotoQueue.ts'
import { useOdometerLabel } from './components/useOdometerLabel.ts'
import { usePhotoSession } from './components/usePhotoSession.ts'
import { DialogButtons, DialogCancel, DialogFrame } from './dialogs/DialogFrame.tsx'
import { DialogTrigger } from './dialogs/DialogTrigger.tsx'
import { useClientId } from './dialogs/useClientId.ts'
import { useDialogState } from './dialogs/useDialogState.ts'
import { CurrencyInput } from './forms/CurrencyInput.tsx'
import { todayIso } from './forms/dates.ts'
import { Field } from './forms/Field.tsx'
import { FieldDate } from './forms/FieldDate.tsx'
import { FieldInput } from './forms/FieldInput.tsx'
import { Form } from './forms/Form.tsx'
import { LogDefaultsDocument, RefuelingDetailsDocument, type DistanceUnit, type LogValue, type ReadingFieldName, type ReviewState, type VolumeUnit } from './gql/generated.ts'
import { parseDecimal, useFormat } from './i18n/format.ts'
import { ErrorMessage } from './messages.tsx'
import { ReadNote } from './recognition/ReadNote.tsx'
import { ReadingProblems } from './recognition/ReadingProblems.tsx'
import { ReadingProgress } from './recognition/ReadingProgress.tsx'
import { SaveWait } from './components/SaveWait.tsx'
import type { ReadingExplanation } from './recognition/readingIssues.ts'
import { mergeReadings, type ReadValues } from './recognition/readValues.ts'
import { filledFields } from './recognition/review.ts'
import { ReviewCallout } from './recognition/ReviewState.tsx'
import { useDraftReadings } from './recognition/useDraftReadings.ts'
import { useReadFill } from './recognition/useReadFill.ts'
import { keepAmountsInStep, type AmountField } from './refuelingAmounts.ts'
import { Loading } from './components/Loading.tsx'
import { useToast } from './toast/toastContext.ts'
import { OfflineNote, ReadLaterNote } from './components/OfflineNote.tsx'
import type { ChangeEdit, MergeInfo } from './dialogs/changeEdit.ts'
import { useMergeFields } from './dialogs/useMergeFields.tsx'
import { outbox } from './offline/outbox.ts'

/** Volume, total cost and odometer are null only when they were left for a photo that is still being read. */
export interface RefuelingValues {
  /** A new log only: the id it gets on the server, the same for every Save of one opening of the dialog (see `useClientId`). */
  id?: string
  date: string
  volume: number | null
  /** What was paid, after the discount. */
  totalCost: number | null
  /** What the till took off the pump's amount; null when none (also what an edit sends to clear one). */
  discount: number | null
  currency: string
  odometer: number | null
  isFullTank: boolean
  /** A fill-up before this one was not logged: the server works out no consumption across the gap. */
  missedPreviousFillUp: boolean
  note: string | null
}

interface Initial extends Omit<RefuelingValues, 'volume' | 'totalCost' | 'discount' | 'odometer' | 'currency'> {
  volume?: number | null
  totalCost?: number | null
  discount?: number | null
  currency: string | null
  odometer?: number | null
  reviewState?: ReviewState
  filledFromPhoto?: LogValue[]
}

/**
 * The fields of a new log that photos can fill in, and which read value goes into each. The unit price is only a help for entering the
 * amounts (it is never saved): volume, unit price, discount and total follow each other (`keepAmountsInStep`).
 */
type FillableField = 'date' | 'volume' | 'unitPrice' | 'discount' | 'totalCost' | 'currency' | 'odometer'
const READ_INTO: Record<FillableField, ReadingFieldName> = {
  date: 'DATE',
  volume: 'VOLUME',
  unitPrice: 'UNIT_PRICE',
  discount: 'DISCOUNT',
  totalCost: 'TOTAL',
  currency: 'CURRENCY',
  odometer: 'ODOMETER',
}
/** The values the server fills in from photos after saving, and their fields (the discount comes with the total it was taken off). */
const FROM_PHOTO: Record<LogValue, FillableField> = { ODOMETER: 'odometer', VOLUME: 'volume', TOTAL: 'totalCost', DISCOUNT: 'discount' }
/** Of those, the ones a new log may be saved without while its photos are read (a discount is never required). */
const WAITS_FOR: readonly FillableField[] = ['odometer', 'volume', 'totalCost']

/**
 * Add a log (no `refuelingId`) or edit one. A new log starts from sensible values: today, the vehicle's latest odometer
 * reading (as a hint, so the number is never entered by accident) and the currency of the latest log. Volume and odometer are
 * entered in the vehicle's own units; the server checks the reading against the neighbouring ones. Of volume, unit price and total,
 * any two give the third (total = volume × unit price − discount); with all three filled a change moves the amount worked out before,
 * else the unit price, else the total (when the unit price changed, unless the user typed the total or a photo read it: then the
 * discount is worked out from the three), and a volume that holds a value never moves by itself
 * (`keepAmountsInStep`). When photo reading is on, the
 * photos of a new log are read on the server and fill in what the user has not typed yet; while one is still being read, volume,
 * total and odometer may be left empty: the server fills them in later and marks the log for review (editing it then checks it).
 */
export function RefuelingFormDialog({
  trigger,
  vehicle,
  refuelingId,
  change,
  onSubmit,
  open: openProp,
  onOpenChange,
}: {
  /** The button that opens it; none when something else does (`open`, e.g. the floating add button). */
  trigger?: ReactNode
  vehicle: { id: string; units: { distance: DistanceUnit; volume: VolumeUnit } }
  refuelingId?: string
  /**
   * Edits a change instead of a log (Waiting to sync): the values it carries, its own title and Save label. Nothing is loaded and no
   * photos are offered; an error `onSubmit` throws stays in the dialog.
   */
  change?: ChangeEdit<RefuelingValues>
  open?: boolean
  onOpenChange?: (open: boolean) => void
  /** `photoIds`: the drafts uploaded for a new log (always empty when editing: a saved log takes its photos right away). */
  onSubmit: (values: RefuelingValues, photoIds: string[]) => Promise<Saved>
}) {
  const { t, i18n } = useTranslation()
  const { toast } = useToast()
  const [open, setOpen] = useDialogState({ open: openProp, onOpenChange })
  const editing = refuelingId !== undefined || change !== undefined
  const clientId = useClientId(open)
  const { queue, leftOut, saving, submit, reset } = usePhotoSession(vehicle.id, editing ? undefined : 'refueling', open && !change)
  // Photos added to a saved log in this dialog (read like drafts), and whether they are still going up.
  const [added, setAdded] = useState<{ id: string; at: number }[]>([])
  const [adding, setAdding] = useState(false)
  const drafts = useDraftReadings(editing ? added : queue.uploaded, open && !change, refuelingId ? { kind: 'refuelings', id: refuelingId } : undefined)
  const details = useQuery(RefuelingDetailsDocument, { variables: { id: refuelingId ?? '' }, skip: !refuelingId || !!change || !open, fetchPolicy: 'network-only' })
  const defaults = useQuery(LogDefaultsDocument, { variables: { vehicleId: vehicle.id }, skip: !open, fetchPolicy: 'network-only' })
  const error = details.error ?? defaults.error
  const ready = defaults.data && (!refuelingId || change || details.data?.refueling)

  const existing = details.data?.refueling
  // Photos kept on this device are read once the log is synced (photo reading on, as last heard): what they show may be left empty.
  const readLater = !editing && queue.kept > 0 && drafts.available
  const logDefaults = defaults.data?.logDefaults
  const lastReading = logDefaults?.lastOdometer != null && logDefaults.lastDate ? { value: logDefaults.lastOdometer, date: logDefaults.lastDate } : null
  const fresh: Initial = { date: todayIso(), currency: defaults.data?.logDefaults?.currency ?? '', isFullTank: true, missedPreviousFillUp: false, note: null }
  const initial: Initial = change
    ? { ...fresh, ...change.initial }
    : existing
      ? { ...existing, currency: existing.currency ?? defaults.data?.logDefaults?.currency ?? '', note: existing.note ?? null }
      : fresh
  const close = () => {
    setOpen(false)
    reset()
    setAdded([])
  }

  return (
    <>
      <DialogTrigger trigger={trigger} open={open} onOpen={() => setOpen(true)} />
      {/* busy: closing while the log is being saved would lose track of it. */}
      <DialogFrame
        open={open}
        onClose={close}
        busy={saving}
        title={change?.title ?? (editing ? t('refuelings.dialogEdit') : t('refuelings.dialogAdd'))}
        description={editing ? t('refuelings.dialogEditDescription') : t('refuelings.dialogAddDescription')}
      >
        {error && <ErrorMessage error={error} />}
        {!change && <OfflineNote />}
        {readLater && <ReadLaterNote />}
        {!error && !ready && <Loading />}
        {refuelingId && details.data && !existing && <ErrorMessage>{t('errors.refueling.notFound')}</ErrorMessage>}
        {leftOut > 0 && <PhotosLeftOut count={leftOut} />}
        {ready && leftOut === 0 && (
          <RefuelingForm
            initial={initial}
            units={vehicle.units}
            editing={editing}
            last={lastReading}
            photosBusy={queue.busy || queue.failed > 0 || adding}
            read={mergeReadings(drafts.readings.values())}
            readingDone={drafts.done}
            explanation={drafts.explanation}
            // A new log may leave values to a photo that is being read; a saved one still waiting for its photos may stay so.
            mayWait={drafts.pending.length > 0 || readLater || (editing && existing?.reviewState === 'AWAITING_PHOTOS') || !!change?.mayWait}
            readingNow={drafts.pending.length > 0}
            wait={{ since: drafts.waitingSince, until: drafts.waitingUntil }}
            submitLabel={change?.submitLabel}
            merge={change?.merge}
            gallery={
              !change && <PhotoGallery
                kind="refuelings"
                logId={refuelingId}
                vehicleId={vehicle.id}
                photos={existing?.photos ?? []}
                queue={queue}
                readingIds={drafts.pending}
                read={editing ? { purpose: 'refueling', locale: i18n.language, jpeg: !drafts.off } : undefined}
                onAdded={(ids) => setAdded((known) => [...known, ...ids.map((id) => ({ id, at: Date.now() }))])}
                onBusyChange={setAdding}
                disabled={saving}
                onChanged={() => details.refetch()}
              />
            }
            onSubmit={async (values) => {
              if (change) {
                await onSubmit(values, []) // the caller tells what came of it
                close()
                return
              }
              if (await submit((photoIds) => onSubmit(editing ? values : { ...values, id: clientId }, photoIds), editing)) {
                close()
                // Kept on the device for the server (the server was out of reach): the toast says so.
                toast(outbox.markOf('refuelings', editing ? refuelingId! : clientId) ? t('toast.savedOnDevice') : t('toast.saved'))
              }
            }}
          />
        )}
      </DialogFrame>
    </>
  )
}

function RefuelingForm({
  initial,
  units,
  editing,
  last,
  gallery,
  photosBusy,
  read,
  readingDone,
  explanation,
  mayWait,
  readingNow,
  wait,
  submitLabel,
  merge,
  onSubmit,
}: {
  initial: Initial
  units: { distance: DistanceUnit; volume: VolumeUnit }
  editing: boolean
  /** The Save button's text, when it is not the add or edit one. */
  submitLabel?: string
  /** Merging a parked edit with what is on the server now: what to say (and offer) under each field. */
  merge?: MergeInfo
  last: { value: number; date: string } | null
  gallery: ReactNode
  /** Chosen photos are still being prepared: saving now would leave them out. */
  photosBusy: boolean
  /** What the photos showed so far. */
  read: ReadValues
  readingDone: boolean
  /** Why the photos gave less than they might have. */
  explanation: ReadingExplanation
  /** A photo is still being read: values it can provide may be left empty. */
  mayWait: boolean
  /** A photo is being read right now (the dialog says so near Save). */
  readingNow: boolean
  /** The window the dialog waits in for the photos being read (the bar near Save). */
  wait: { since: number | null; until: number | null }
  onSubmit: (values: RefuelingValues) => Promise<unknown>
}) {
  const { t } = useTranslation()
  const odometerLabel = useOdometerLabel(units.distance)
  const { distance } = useFormat()
  const labels: Record<FillableField, string> = {
    date: t('refuelings.fields.date'),
    volume: t('refuelings.fields.volume', { unit: t(`units.volumeShort.${units.volume}`) }),
    unitPrice: t('refuelings.fields.unitPrice', { unit: t(`units.volumeShort.${units.volume}`) }),
    discount: t('refuelings.fields.discount'),
    totalCost: t('refuelings.fields.cost'),
    currency: t('refuelings.fields.currency'),
    odometer: odometerLabel,
  }
  const fill = useReadFill(
    {
      date: initial.date,
      volume: initial.volume?.toString() ?? '',
      unitPrice: '', // worked out from the others (keepAmountsInStep)
      discount: initial.discount?.toString() ?? '',
      totalCost: initial.totalCost?.toString() ?? '',
      currency: initial.currency ?? '',
      odometer: initial.odometer?.toString() ?? '',
    },
    READ_INTO,
    read,
    labels,
    readingDone,
    { ...explanation, last: last ? distance(last.value, units.distance) : undefined },
    keepAmountsInStep,
    editing,
  )
  const merging = useMergeFields(merge)
  const fromPhoto = filledFields(initial.filledFromPhoto, FROM_PHOTO)
  const waits = new Set(WAITS_FOR)
  const optional = (field: FillableField) => mayWait && waits.has(field)
  const note = (field: FillableField) => (
    <>
      <ReadNote
        filled={fill.isFilled(field) || fromPhoto.has(field)}
        offered={fill.offered(field)}
        waiting={optional(field) && fill.values[field].trim() === ''}
        field={labels[field]}
        onUse={() => fill.use(field)}
      />
      {merging.note(field, labels[field], (value) => fill.change(field, value == null ? '' : String(value)))}
    </>
  )
  /** Volume, unit price, discount or total: a number that may be worked out from the others (the unit price, only a help, and the discount are never required). */
  const amount = (field: AmountField) => {
    const required = field !== 'unitPrice' && field !== 'discount' && !optional(field)
    return (
      <Field
        name={field}
        label={labels[field]}
        hint={fill.isCalculated(field) ? t('refuelings.hints.calculated') : undefined}
        required={required}
        invalid={decimalInvalid}
        extra={note(field)}
      >
        <FieldInput inputMode="decimal" autoComplete="off" value={fill.values[field]} onChange={(e) => fill.change(field, e.target.value)} />
      </Field>
    )
  }
  const [full, setFull] = useState(initial.isFullTank)
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  const [missed, setMissed] = useState(initial.missedPreviousFillUp)
  const decimalInvalid = { message: t('forms.numberInvalid'), test: (v: string) => v !== '' && parseDecimal(v) === undefined }

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    const note = String(form.get('note') ?? '').trim()
    const text = (name: string) => String(form.get(name) ?? '').trim()
    setBusy(true)
    setError(undefined)
    try {
      await onSubmit({
        date: text('date'),
        volume: text('volume') === '' ? null : parseDecimal(text('volume'))!,
        totalCost: text('totalCost') === '' ? null : parseDecimal(text('totalCost'))!,
        // Emptied in an edit: 0, which clears it (left out, the server keeps the log's: what a build from before discounts sends).
        discount: text('discount') === '' ? (editing ? 0 : null) : parseDecimal(text('discount'))!,
        currency: text('currency').toUpperCase(),
        odometer: text('odometer') === '' ? null : Number(text('odometer')),
        isFullTank: full,
        missedPreviousFillUp: missed,
        note: note === '' ? null : note,
      })
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Form onSubmit={submit} refusal={error}>
      <ReviewCallout state={initial.reviewState} />
      <Stack sx={{ gap: 1.5 }}>
        <Field name="date" label={labels.date} required extra={note('date')}>
          <FieldDate disableFuture value={fill.values.date} onChange={(v) => fill.change('date', v)} />
        </Field>
        <Stack direction="row" sx={{ gap: 1.5, flexWrap: 'wrap' }}>
          <Box sx={{ flex: '1 1 8rem', minWidth: 0 }}>{amount('volume')}</Box>
          <Box sx={{ flex: '1 1 8rem', minWidth: 0 }}>{amount('unitPrice')}</Box>
        </Stack>
        <Stack direction="row" sx={{ gap: 1.5, flexWrap: 'wrap' }}>
          <Box sx={{ flex: '1 1 6rem', minWidth: 0 }}>{amount('discount')}</Box>
          <Box sx={{ flex: '2 1 8rem', minWidth: 0 }}>{amount('totalCost')}</Box>
          <Box sx={{ flex: '1 1 6rem', minWidth: 0 }}>
            <Field
              name="currency"
              label={labels.currency}
              required
              hint={t('refuelings.hints.currency')}
              invalid={{ message: t('errors.money.currencyInvalid'), test: (v) => v !== '' && !/^[A-Za-z]{3}$/.test(v.trim()) }}
              extra={note('currency')}
            >
              <CurrencyInput value={fill.values.currency} onChange={(v) => fill.change('currency', v)} preferred={initial.currency} />
            </Field>
          </Box>
        </Stack>
        <OdometerField
          unit={units.distance}
          last={last}
          required={!optional('odometer')}
          value={fill.values.odometer}
          onChange={(v) => fill.change('odometer', v)}
          extra={note('odometer')}
        />
        <LabeledSwitch label={t('refuelings.fields.fullTank')} hint={t('refuelings.hints.fullTankHelp')} checked={full} onChange={setFull} />
        {merging.note('isFullTank', t('refuelings.fields.fullTank'), (value) => setFull(!!value))}
        <LabeledSwitch label={t('refuelings.fields.missedPrevious')} hint={t('refuelings.hints.missedPreviousHelp')} checked={missed} onChange={setMissed} />
        {merging.note('missedPreviousFillUp', t('refuelings.fields.missedPrevious'), (value) => setMissed(!!value))}
        <Field name="note" label={t('refuelings.fields.note')} extra={merging.note('note', t('refuelings.fields.note'))}>
          <FieldInput key={merging.key('note')} multiline minRows={2} maxLength={500} defaultValue={merging.value('note', initial.note) ?? ''} />
        </Field>
        {gallery}
        <div role="status" aria-label={t('a11y.readingStatus')}>
          <ReadingProgress active={readingNow} canSave={mayWait} since={wait.since} until={wait.until} />
          {fill.announcement && <Typography variant="body2">{fill.announcement}</Typography>}
          <ReadingProblems problems={fill.problems} />
        </div>
        {error !== undefined && <ErrorMessage error={error} />}
        <DialogButtons>
          <SaveWait waiting={photosBusy && !busy} />
          <DialogCancel disabled={busy} />
          <Button type="submit" loading={busy} disabled={photosBusy}>
            {submitLabel ?? (editing ? t('refuelings.save') : t('refuelings.saveAdd'))}
          </Button>
        </DialogButtons>
      </Stack>
    </Form>
  )
}
