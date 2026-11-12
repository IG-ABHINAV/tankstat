import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
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
import { FieldAutocomplete } from './forms/FieldAutocomplete.tsx'
import { FieldDate } from './forms/FieldDate.tsx'
import { FieldInput } from './forms/FieldInput.tsx'
import { Form } from './forms/Form.tsx'
import { ExpenseCategoriesDocument, ExpenseDetailsDocument, LogDefaultsDocument, type DistanceUnit, type LogValue, type ReadingFieldName, type ReviewState } from './gql/generated.ts'
import { parseDecimal } from './i18n/format.ts'
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
import { Loading } from './components/Loading.tsx'
import { useToast } from './toast/toastContext.ts'
import { OfflineNote, ReadLaterNote } from './components/OfflineNote.tsx'
import type { ChangeEdit, MergeInfo } from './dialogs/changeEdit.ts'
import { useMergeFields } from './dialogs/useMergeFields.tsx'
import { outbox } from './offline/outbox.ts'
import { TyreLogBanner } from './tyres/TyreLogBanner.tsx'

/** The amount is null only when it was left for a photo that is still being read. */
export interface ExpenseValues {
  /** A new expense only: the id it gets on the server, the same for every Save of one opening of the dialog (see `useClientId`). */
  id?: string
  date: string
  title: string
  category: string | null
  /** What was paid, after the discount. */
  amount: number | null
  /** What was taken off the amount; null when none on an add, 0 to clear one on an edit (null there keeps the expense's). */
  discount: number | null
  currency: string
  odometer: number | null
  note: string | null
}

interface Initial {
  date: string
  title: string
  category: string | null
  amount?: number | null
  discount?: number | null
  currency: string
  odometer?: number | null
  note: string | null
  reviewState?: ReviewState
  filledFromPhoto?: LogValue[]
}

/** The fields of a new expense that photos can fill in (a receipt's shop becomes the title), and which read value goes into each. */
type FillableField = 'date' | 'title' | 'amount' | 'discount' | 'currency' | 'odometer'
const READ_INTO: Record<FillableField, ReadingFieldName> = { date: 'DATE', title: 'TITLE', amount: 'TOTAL', discount: 'DISCOUNT', currency: 'CURRENCY', odometer: 'ODOMETER' }
/** The values the server fills in from photos after saving, and their fields (the discount comes with the amount it was taken off). */
const FROM_PHOTO: Partial<Record<LogValue, FillableField>> = { ODOMETER: 'odometer', TOTAL: 'amount', DISCOUNT: 'discount' }

/**
 * Add an expense (no `expenseId`) or edit one. Starts from today and the currency of the vehicle's latest log; the category suggests
 * what was used before (any text is fine) and the odometer may be left empty. When photo reading is on, the photos of a new expense
 * are read on the server and fill in what the user has not typed yet; while one is still being read, the amount may be left empty too:
 * the server fills in amount and odometer later and marks the expense for review (editing it then checks it).
 */
export function ExpenseFormDialog({
  trigger,
  vehicle,
  expenseId,
  change,
  onSubmit,
  open: openProp,
  onOpenChange,
}: {
  /** The button that opens it; none when something else does (`open`, e.g. the floating add button). */
  trigger?: ReactNode
  vehicle: { id: string; units: { distance: DistanceUnit } }
  expenseId?: string
  /** Edits a change instead of an expense (Waiting to sync; see `ChangeEdit`). */
  change?: ChangeEdit<ExpenseValues>
  open?: boolean
  onOpenChange?: (open: boolean) => void
  /** `photoIds`: the drafts uploaded for a new expense (always empty when editing: a saved expense takes its photos right away). */
  onSubmit: (values: ExpenseValues, photoIds: string[]) => Promise<Saved>
}) {
  const { t, i18n } = useTranslation()
  const { toast } = useToast()
  const [open, setOpen] = useDialogState({ open: openProp, onOpenChange })
  const clientId = useClientId(open)
  const editing = expenseId !== undefined || change !== undefined
  const { queue, leftOut, saving, submit, reset } = usePhotoSession(vehicle.id, editing ? undefined : 'expense', open && !change)
  // Photos added to a saved log in this dialog (read like drafts), and whether they are still going up.
  const [added, setAdded] = useState<{ id: string; at: number }[]>([])
  const [adding, setAdding] = useState(false)
  const drafts = useDraftReadings(editing ? added : queue.uploaded, open && !change, expenseId ? { kind: 'expenses', id: expenseId } : undefined)
  const details = useQuery(ExpenseDetailsDocument, { variables: { id: expenseId ?? '' }, skip: !expenseId || !!change || !open, fetchPolicy: 'network-only' })
  const defaults = useQuery(LogDefaultsDocument, { variables: { vehicleId: vehicle.id }, skip: !open, fetchPolicy: 'network-only' })
  const categories = useQuery(ExpenseCategoriesDocument, { variables: { vehicleId: vehicle.id }, skip: !open, fetchPolicy: 'network-only' })
  const error = details.error ?? defaults.error
  const existing = details.data?.expense
  // Photos kept on this device are read once the expense is synced (photo reading on, as last heard): its amount may be left empty.
  const readLater = !editing && queue.kept > 0 && drafts.available
  const ready = defaults.data && (!expenseId || change || existing)
  const currency = defaults.data?.logDefaults?.currency ?? ''
  const fresh: Initial = { date: todayIso(), title: '', category: null, currency, note: null }
  const initial: Initial = change ? { ...fresh, ...change.initial } : existing ? { ...existing, currency: existing.currency ?? currency } : fresh
  const close = () => {
    setOpen(false)
    reset()
    setAdded([])
  }

  return (
    <>
      <DialogTrigger trigger={trigger} open={open} onOpen={() => setOpen(true)} />
      {/* busy: closing while the expense is being saved would lose track of it. */}
      <DialogFrame
        open={open}
        onClose={close}
        busy={saving}
        title={change?.title ?? (editing ? t('expenses.dialogEdit') : t('expenses.dialogAdd'))}
        description={editing ? t('expenses.dialogEditDescription') : t('expenses.dialogAddDescription')}
      >
        {error && <ErrorMessage error={error} />}
        {!change && <OfflineNote />}
        {existing?.tyreEntry && <TyreLogBanner vehicleId={vehicle.id} line={existing.tyreEntry} />}
        {readLater && <ReadLaterNote />}
        {!error && !ready && (
          <Loading />
        )}
        {expenseId && details.data && !existing && <ErrorMessage>{t('errors.expense.notFound')}</ErrorMessage>}
        {leftOut > 0 && <PhotosLeftOut count={leftOut} />}
        {ready && leftOut === 0 && (
          <ExpenseForm
            initial={initial}
            unit={vehicle.units.distance}
            editing={editing}
            categories={categories.data?.expenseCategories ?? []}
            photosBusy={queue.busy || queue.failed > 0 || adding}
            read={mergeReadings(drafts.readings.values(), { DISCOUNT: 'TOTAL' })}
            readingDone={drafts.done}
            explanation={drafts.explanation}
            // A new expense may leave its amount to a photo that is being read; a saved one still waiting for its photos may stay so.
            mayWait={drafts.pending.length > 0 || readLater || (editing && existing?.reviewState === 'AWAITING_PHOTOS') || !!change?.mayWait}
            readingNow={drafts.pending.length > 0}
            wait={{ since: drafts.waitingSince, until: drafts.waitingUntil }}
            submitLabel={change?.submitLabel}
            merge={change?.merge}
            gallery={
              !change && <PhotoGallery
                kind="expenses"
                logId={expenseId}
                vehicleId={vehicle.id}
                photos={existing?.photos ?? []}
                queue={queue}
                readingIds={drafts.pending}
                read={editing ? { purpose: 'expense', locale: i18n.language, jpeg: !drafts.off } : undefined}
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
                toast(outbox.markOf('expenses', editing ? expenseId! : clientId) ? t('toast.savedOnDevice') : t('toast.saved'))
              }
            }}
          />
        )}
      </DialogFrame>
    </>
  )
}

function ExpenseForm({
  initial,
  unit,
  editing,
  categories,
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
  /** The Save button's text, when it is not the add or edit one. */
  submitLabel?: string
  /** Merging a parked edit with what is on the server now: what to say (and offer) under each field. */
  merge?: MergeInfo
  initial: Initial
  unit: DistanceUnit
  editing: boolean
  categories: string[]
  gallery: ReactNode
  /** Chosen photos are still being prepared: saving now would leave them out. */
  photosBusy: boolean
  /** What the photos showed so far. */
  read: ReadValues
  readingDone: boolean
  /** Why the photos gave less than they might have. */
  explanation: ReadingExplanation
  /** A photo is still being read: the amount may be left empty. */
  mayWait: boolean
  /** A photo is being read right now (the dialog says so near Save). */
  readingNow: boolean
  /** The window the dialog waits in for the photos being read (the bar near Save). */
  wait: { since: number | null; until: number | null }
  onSubmit: (values: ExpenseValues) => Promise<unknown>
}) {
  const { t } = useTranslation()
  const odometerLabel = useOdometerLabel(unit, true)
  const labels: Record<FillableField, string> = {
    date: t('expenses.fields.date'),
    title: t('expenses.fields.title'),
    amount: t('expenses.fields.amount'),
    discount: t('expenses.fields.discount'),
    currency: t('expenses.fields.currency'),
    odometer: odometerLabel,
  }
  const fill = useReadFill(
    {
      date: initial.date,
      title: initial.title,
      amount: initial.amount?.toString() ?? '',
      discount: initial.discount?.toString() ?? '',
      currency: initial.currency,
      odometer: initial.odometer?.toString() ?? '',
    },
    READ_INTO,
    read,
    labels,
    readingDone,
    explanation,
    undefined,
    editing,
    { discount: 'amount' }, // a photo's discount was taken off its own total: next to an amount the user entered, it is only offered
  )
  const merging = useMergeFields(merge)
  const fromPhoto = filledFields(initial.filledFromPhoto, FROM_PHOTO)
  const amountOptional = mayWait
  const note = (field: FillableField) => (
    <>
      <ReadNote
        filled={fill.isFilled(field) || fromPhoto.has(field)}
        offered={fill.offered(field)}
        waiting={mayWait && (field === 'amount' || field === 'odometer') && fill.values[field].trim() === ''}
        field={labels[field]}
        onUse={() => fill.use(field)}
      />
      {merging.note(field, labels[field], (value) => fill.change(field, value == null ? '' : String(value)))}
    </>
  )
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    const text = (name: string) => String(form.get(name) ?? '').trim()
    setBusy(true)
    setError(undefined)
    try {
      await onSubmit({
        date: text('date'),
        title: text('title'),
        category: text('category') || null,
        amount: text('amount') === '' ? null : parseDecimal(text('amount'))!,
        // Emptied in an edit: 0, which clears it (left out, the server keeps the expense's). With the amount left to a photo: none, the
        // photo brings both.
        discount: text('discount') === '' || text('amount') === '' ? (editing ? 0 : null) : parseDecimal(text('discount'))!,
        currency: text('currency').toUpperCase(),
        odometer: text('odometer') === '' ? null : Number(text('odometer')),
        note: text('note') || null,
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
        <Field name="title" label={labels.title} required extra={note('title')}>
          <FieldInput maxLength={120} autoComplete="off" value={fill.values.title} onChange={(e) => fill.change('title', e.target.value)} />
        </Field>
        <Field
          name="category"
          label={t('expenses.fields.category')}
          hint={categories.length > 0 ? t('expenses.hints.categories', { list: categories.join(', ') }) : undefined}
          extra={merging.note('category', t('expenses.fields.category'))}
        >
          <FieldAutocomplete key={merging.key('category')} options={categories} defaultValue={merging.value('category', initial.category) ?? ''} maxLength={60} openOnFocus />
        </Field>
        <Stack direction="row" sx={{ gap: 1.5, flexWrap: 'wrap' }}>
          <Box sx={{ flex: '2 1 8rem', minWidth: 0 }}>
            <Field
              name="amount"
              label={labels.amount}
              required={!amountOptional}
              invalid={{ message: t('forms.numberInvalid'), test: (v) => v !== '' && parseDecimal(v) === undefined }}
              extra={note('amount')}
            >
              <FieldInput inputMode="decimal" autoComplete="off" value={fill.values.amount} onChange={(e) => fill.change('amount', e.target.value)} />
            </Field>
          </Box>
          <Box sx={{ flex: '1 1 6rem', minWidth: 0 }}>
            <Field
              name="discount"
              label={labels.discount}
              invalid={{ message: t('forms.numberInvalid'), test: (v) => v !== '' && parseDecimal(v) === undefined }}
              extra={note('discount')}
            >
              <FieldInput inputMode="decimal" autoComplete="off" value={fill.values.discount} onChange={(e) => fill.change('discount', e.target.value)} />
            </Field>
          </Box>
          <Box sx={{ flex: '1 1 6rem', minWidth: 0 }}>
            <Field
              name="currency"
              label={labels.currency}
              required
              invalid={{ message: t('errors.money.currencyInvalid'), test: (v) => v !== '' && !/^[A-Za-z]{3}$/.test(v.trim()) }}
              extra={note('currency')}
            >
              <CurrencyInput value={fill.values.currency} onChange={(v) => fill.change('currency', v)} preferred={initial.currency} />
            </Field>
          </Box>
        </Stack>
        <OdometerField unit={unit} optional value={fill.values.odometer} onChange={(v) => fill.change('odometer', v)} extra={note('odometer')} />
        <Field name="note" label={t('expenses.fields.note')} extra={merging.note('note', t('expenses.fields.note'))}>
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
            {submitLabel ?? (editing ? t('expenses.save') : t('expenses.saveAdd'))}
          </Button>
        </DialogButtons>
      </Stack>
    </Form>
  )
}
