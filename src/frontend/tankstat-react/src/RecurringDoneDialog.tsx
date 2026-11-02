import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Checkbox from '@mui/material/Checkbox'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useId, useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { OdometerField } from './components/OdometerField.tsx'
import { PhotoGallery } from './components/PhotoGallery.tsx'
import { ReadLaterNote } from './components/OfflineNote.tsx'
import { PhotosLeftOut } from './components/PhotosLeftOut.tsx'
import { RecurringStatusBadge } from './components/RecurringStatus.tsx'
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
import { ExpenseCategoriesDocument, LogDefaultsDocument, type DistanceUnit, type LogDefaultsQuery, type ReadingFieldName } from './gql/generated.ts'
import { parseDecimal, useFormat } from './i18n/format.ts'
import { ErrorMessage } from './messages.tsx'
import { ReadNote } from './recognition/ReadNote.tsx'
import { ReadingProblems } from './recognition/ReadingProblems.tsx'
import { ReadingProgress } from './recognition/ReadingProgress.tsx'
import { SaveWait } from './components/SaveWait.tsx'
import type { ReadingExplanation } from './recognition/readingIssues.ts'
import { mergeReadings, type ReadValues } from './recognition/readValues.ts'
import { useDraftReadings } from './recognition/useDraftReadings.ts'
import { useReadFill } from './recognition/useReadFill.ts'
import { defaultCategory, defaultTitle, MAX_EXPENSE_TITLE, usesDistance, type DoneItem } from './recurringDone.ts'
import { Loading } from './components/Loading.tsx'
import { useToast } from './toast/toastContext.ts'
import { outbox } from './offline/outbox.ts'

/** One service visit: the schedules done, and (with an amount) the one expense logged for all of them. */
export interface DoneValues {
  ids: string[]
  date: string
  odometer: number | null
  /** Null: no expense is logged (unless a photo still being read may give it), only the schedules move on. */
  amount: number | null
  /** What was taken off the amount (what was paid), kept with the logged expense; null when none. */
  discount: number | null
  currency: string | null
  title: string
  /** Empty for none. */
  category: string
  /** The id of the expense this visit logs (if it logs one), the same for every Save of one opening of the dialog (see `useClientId`). */
  expenseId?: string
}

/** The fields photos can fill in (a dashboard: the odometer; an invoice: the cost and the day), and which read value goes into each. */
type FillableField = 'date' | 'odometer' | 'amount' | 'discount' | 'currency'
const READ_INTO: Record<FillableField, ReadingFieldName> = { date: 'DATE', odometer: 'ODOMETER', amount: 'TOTAL', discount: 'DISCOUNT', currency: 'CURRENCY' }

/**
 * "Mark as done" for a service visit: the vehicle's schedules are listed, the ones done at this visit ticked (opened from one schedule, that
 * one and the others of its category that need attention start ticked: `preselect`). The next interval of each ticked schedule starts from
 * the day and odometer entered. The amount is optional and covers all of them together, never split: with an amount one expense is logged
 * for the visit (its title and category prefilled from the schedules) and remembers each schedule; without one only the schedules move on.
 * Photos (the dashboard, the invoice) go with the logged expense: an expense is logged when an amount is given or a photo is still being
 * read (it may fill the amount in, under the expense rules); otherwise the photos are not kept.
 */
export function RecurringDoneDialog({
  trigger,
  vehicle,
  items,
  selected,
  openedFrom,
  onSubmit,
  open: openProp,
  onOpenChange,
  onClosed,
}: {
  /** The button that opens it; none when the caller opens it (`open`), e.g. one dialog for all the schedules of a card. */
  trigger?: ReactNode
  vehicle: { id: string; units: { distance: DistanceUnit } }
  /** The vehicle's schedules, most urgent first. */
  items: DoneItem[]
  /** What starts ticked. */
  selected: string[]
  /** The schedule the dialog was opened from (its title names the dialog); absent for "Mark selected as done". */
  openedFrom?: { title: string }
  onSubmit: (values: DoneValues, photoIds: string[]) => Promise<Saved>
  open?: boolean
  onOpenChange?: (open: boolean) => void
  /**
   * Once it is fully closed. The focus goes back to the button that opened it; for a button that is gone by then (its schedule left the
   * list), the caller puts it somewhere else, see `VehicleCard`.
   */
  onClosed?: () => void
}) {
  const { t } = useTranslation()
  const { toast } = useToast()
  const [open, setOpen] = useDialogState({ open: openProp, onOpenChange })
  const clientId = useClientId(open)
  const { queue, leftOut, saving, submit, reset } = usePhotoSession(vehicle.id, 'expense', open)
  const drafts = useDraftReadings(queue.uploaded, open)
  // Photos kept on this device are read once the visit is synced (photo reading on, as last heard): the amount may be left to them.
  const readLater = queue.kept > 0 && drafts.available

  const close = () => {
    setOpen(false)
    reset()
  }
  return (
    <>
      <DialogTrigger trigger={trigger} open={open} onOpen={() => setOpen(true)} />
      {/* busy: closing while it is being saved would lose track of it. */}
      <DialogFrame
        open={open}
        onClose={close}
        onClosed={onClosed}
        busy={saving}
        maxWidth={480}
        title={openedFrom ? t('recurring.doneTitle', { title: openedFrom.title }) : t('recurring.doneTitleMany')}
        description={t('recurring.doneDescription')}
      >
        {leftOut > 0 && <PhotosLeftOut count={leftOut} />}
        {readLater && <ReadLaterNote />}
        {open && leftOut === 0 && (
          <DoneForm
            vehicle={vehicle}
            items={items}
            selected={selected}
            photosBusy={queue.busy || queue.failed > 0}
            photosPicked={queue.items.length > 0}
            read={mergeReadings(drafts.readings.values())}
            readingDone={drafts.done}
            reading={drafts.pending.length > 0}
            readLater={readLater}
            wait={{ since: drafts.waitingSince, until: drafts.waitingUntil }}
            explanation={drafts.explanation}
            gallery={<PhotoGallery kind="expenses" photos={[]} queue={queue} readingIds={drafts.pending} disabled={saving} onChanged={() => undefined} />}
            onSubmit={async (values, logsExpense) => {
              // Without an expense the photos have nothing to belong to: they are not sent, and closing deletes them.
              // The id is only for the expense the visit logs; without one there is nothing it could name.
              if (await submit((photoIds) => onSubmit(logsExpense ? { ...values, expenseId: clientId } : values, photoIds), !logsExpense)) {
                close()
                // Kept on the device for the server (the server was out of reach): the toast says so.
                toast(outbox.markOf('recurring', values.ids[0]) === 'done' ? t('toast.savedOnDevice') : t('toast.saved'))
              }
            }}
          />
        )}
      </DialogFrame>
    </>
  )
}

interface FormProps {
  vehicle: { id: string; units: { distance: DistanceUnit } }
  items: DoneItem[]
  selected: string[]
  photosBusy: boolean
  photosPicked: boolean
  /** What the photos showed so far. */
  read: ReadValues
  readingDone: boolean
  /** A photo is still being read: the amount may be left to it. */
  reading: boolean
  /** Photos kept on this device are read once the visit is synced: the amount may be left to them too. */
  readLater: boolean
  /** The window the dialog waits in for the photos being read (the bar near Save). */
  wait: { since: number | null; until: number | null }
  /** Why the photos gave less than they might have. */
  explanation: ReadingExplanation
  gallery: ReactNode
  onSubmit: (values: DoneValues, logsExpense: boolean) => Promise<unknown>
}

function DoneForm(props: FormProps) {
  const defaults = useQuery(LogDefaultsDocument, { variables: { vehicleId: props.vehicle.id }, fetchPolicy: 'network-only' })
  const categories = useQuery(ExpenseCategoriesDocument, { variables: { vehicleId: props.vehicle.id }, fetchPolicy: 'network-only' })
  const d = defaults.data?.logDefaults
  if (!d) return defaults.error ? <ErrorMessage error={defaults.error} /> : <Loading />
  return <DoneFields {...props} defaults={d} categories={categories.data?.expenseCategories ?? []} />
}

function DoneFields({
  vehicle,
  items,
  selected,
  photosBusy,
  photosPicked,
  read,
  readingDone,
  reading,
  readLater,
  wait,
  explanation,
  gallery,
  defaults: d,
  categories,
  onSubmit,
}: FormProps & { defaults: NonNullable<LogDefaultsQuery['logDefaults']>; categories: string[] }) {
  const { t } = useTranslation()
  const [ticked, setTicked] = useState(() => new Set(selected))
  // The expense's title and category follow the ticked schedules until the person types their own (null: not typed).
  const [title, setTitle] = useState<string | null>(null)
  const [category, setCategory] = useState<string | null>(null)
  const [error, setError] = useState<unknown>()
  const [noneTicked, setNoneTicked] = useState(false)
  const [busy, setBusy] = useState(false)
  const idBase = useId()
  const done = items.filter((i) => ticked.has(i.id))
  const distance = usesDistance(done)
  const last = d.lastOdometer != null && d.lastDate ? { value: d.lastOdometer, date: d.lastDate } : null
  const odometerLabel = useOdometerLabel(vehicle.units.distance, !distance)
  const { distance: formatDistance } = useFormat()
  const labels: Record<FillableField, string> = {
    date: t('recurring.doneDate'),
    odometer: odometerLabel,
    amount: t('recurring.amountOptional'),
    discount: t('recurring.discount'),
    currency: t('recurring.currency'),
  }
  const fill = useReadFill({ date: todayIso(), odometer: d.lastOdometer?.toString() ?? '', amount: '', discount: '', currency: d.currency ?? '' }, READ_INTO, read, labels, readingDone, {
    ...explanation,
    last: last ? formatDistance(last.value, vehicle.units.distance) : undefined,
  })
  const hasAmount = fill.values.amount.trim() !== ''
  const logsExpense = hasAmount || reading || readLater
  const titleValue = title ?? defaultTitle(done)
  const categoryValue = category ?? defaultCategory(done)
  const note = (field: FillableField) => (
    <ReadNote
      filled={fill.isFilled(field)}
      offered={fill.offered(field)}
      waiting={(reading || readLater) && field === 'amount' && !hasAmount}
      field={labels[field]}
      onUse={() => fill.use(field)}
    />
  )

  const toggle = (id: string, on: boolean) => {
    setNoneTicked(false)
    setTicked((current) => {
      const next = new Set(current)
      if (on) next.add(id)
      else next.delete(id)
      return next
    })
  }

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    if (done.length === 0) {
      setNoneTicked(true)
      return
    }
    const form = new FormData(e.currentTarget)
    const text = (name: string) => String(form.get(name) ?? '').trim()
    setBusy(true)
    setError(undefined)
    try {
      await onSubmit(
        {
          ids: done.map((i) => i.id),
          date: text('date'),
          odometer: text('odometer') === '' ? null : Number(text('odometer')),
          amount: hasAmount ? (parseDecimal(text('amount')) ?? null) : null,
          discount: text('discount') === '' ? null : (parseDecimal(text('discount')) ?? null), // without an amount the server refuses it, said at the field
          currency: hasAmount ? text('currency').toUpperCase() : null,
          title: titleValue.trim(),
          category: categoryValue.trim(), // empty: no category (null would mean "the schedules' one" to the server)
        },
        logsExpense,
      )
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Form onSubmit={submit} refusal={error}>
      <Stack sx={{ gap: 1.5 }}>
        <Box component="fieldset" sx={{ border: 0, p: 0, m: 0, minWidth: 0 }}>
          <Typography component="legend" variant="body2" sx={{ fontWeight: 700, p: 0, mb: 0.5 }}>
            {t('recurring.doneItems')}
          </Typography>
          <Stack component="ul" sx={{ listStyle: 'none', p: 0, m: 0 }}>
            {items.map((i) => {
              const id = `${idBase}-${i.id}`
              return (
                <li key={i.id}>
                  <Stack direction="row" sx={{ alignItems: 'center', gap: 1, minHeight: 44 }}>
                    <Checkbox id={id} checked={ticked.has(i.id)} onChange={(e) => toggle(i.id, e.target.checked)} sx={{ ml: -1 }} />
                    <Typography component="label" htmlFor={id} variant="body2" sx={{ flexGrow: 1 }}>
                      {i.title}
                    </Typography>
                    {i.status.state !== 'UPCOMING' && <RecurringStatusBadge state={i.status.state} />}
                  </Stack>
                </li>
              )
            })}
          </Stack>
          <Typography variant="caption" role="status" sx={{ color: 'text.secondary' }}>
            {t('recurring.selectedCount', { count: done.length })}
          </Typography>
        </Box>
        {noneTicked && <ErrorMessage>{t('errors.recurring.noneSelected')}</ErrorMessage>}
        <Field name="date" label={labels.date} required extra={note('date')}>
          <FieldDate disableFuture value={fill.values.date} onChange={(v) => fill.change('date', v)} />
        </Field>
        <OdometerField
          unit={vehicle.units.distance}
          last={last}
          optional={!distance}
          value={fill.values.odometer}
          onChange={(v) => fill.change('odometer', v)}
          extra={note('odometer')}
        />
        <Stack direction="row" sx={{ gap: 1.5, flexWrap: 'wrap' }}>
          <Box sx={{ flex: '2 1 8rem', minWidth: 0 }}>
            <Field
              name="amount"
              label={labels.amount}
              hint={t('recurring.amountHint')}
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
              required={hasAmount}
              invalid={{ message: t('errors.money.currencyInvalid'), test: (v) => v !== '' && !/^[A-Za-z]{3}$/.test(v.trim()) }}
              extra={note('currency')}
            >
              <CurrencyInput value={fill.values.currency} onChange={(v) => fill.change('currency', v)} preferred={d.currency} />
            </Field>
          </Box>
        </Stack>
        <Field name="title" label={t('recurring.expenseTitle')} required={logsExpense}>
          <FieldInput maxLength={MAX_EXPENSE_TITLE} autoComplete="off" value={titleValue} onChange={(e) => setTitle(e.target.value)} />
        </Field>
        <Field name="category" label={t('recurring.expenseCategory')}>
          <FieldAutocomplete options={categories} value={categoryValue} onChange={setCategory} maxLength={60} openOnFocus />
        </Field>
        {gallery}
        {photosPicked && !logsExpense && (
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            {t('recurring.photosNotKept')}
          </Typography>
        )}
        <div role="status" aria-label={t('a11y.readingStatus')}>
          <ReadingProgress active={reading} canSave={logsExpense} since={wait.since} until={wait.until} />
          {fill.announcement && <Typography variant="body2">{fill.announcement}</Typography>}
          <ReadingProblems problems={fill.problems} />
        </div>
        {error !== undefined && <ErrorMessage error={error} />}
        <DialogButtons>
          <SaveWait waiting={photosBusy && !busy} />
          <DialogCancel disabled={busy} />
          <Button type="submit" loading={busy} disabled={photosBusy}>
            {t('recurring.doneSave')}
          </Button>
        </DialogButtons>
      </Stack>
    </Form>
  )
}
