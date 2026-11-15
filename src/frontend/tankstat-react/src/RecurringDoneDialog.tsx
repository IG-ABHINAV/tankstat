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
import {
  ExpenseCategoriesDocument,
  LogDefaultsDocument,
  VehicleTyresDocument,
  type DistanceUnit,
  type ExpenseTyreInput,
  type LogDefaultsQuery,
  type ReadingFieldName,
  type VehicleTyresFieldsFragment,
} from './gql/generated.ts'
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
import type { ChangeEdit } from './dialogs/changeEdit.ts'
import { visuallyHidden } from './components/visuallyHidden.ts'
import { TyreDetailsFields } from './tyres/TyreDetailsFields.tsx'
import { suggestedKind, useExpenseTyreDetails, useSwitchHint, useTyreDetailsIds, type TyreDetailsIds } from './tyres/useExpenseTyreDetails.ts'

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
  /**
   * Its tyre details (#290, the owner's decisions 57 and 63): the visit's expense is the lines', or without an amount the line (or lines)
   * alone, on the visit's day with its odometer.
   */
  tyre?: ExpenseTyreInput | null
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
  change,
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
  /**
   * Edits a visit waiting on this device, or parked by the server, instead of making one (Waiting to sync, #290's decision 71): its
   * schedules, values and tyre details; no photos, as every change's edit. `onSubmit` decides what saving does.
   */
  change?: ChangeEdit<DoneValues>
}) {
  const { t } = useTranslation()
  const { toast } = useToast()
  const [open, setOpen] = useDialogState({ open: openProp, onOpenChange })
  const clientId = useClientId(open)
  // The ids of what its tyre details make (#290); a visit edited on Waiting to sync keeps the ones they gave.
  const waitingTyre = change?.initial.tyre ?? null
  const tyreIds = useTyreDetailsIds(open, waitingTyre)
  const { queue, leftOut, saving, submit, reset } = usePhotoSession(vehicle.id, 'expense', open && !change)
  const drafts = useDraftReadings(queue.uploaded, open && !change)
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
        title={change?.title ?? (openedFrom ? t('recurring.doneTitle', { title: openedFrom.title }) : t('recurring.doneTitleMany'))}
        description={t('recurring.doneDescription')}
      >
        {leftOut > 0 && <PhotosLeftOut count={leftOut} />}
        {readLater && <ReadLaterNote />}
        {open && leftOut === 0 && (
          <DoneForm
            vehicle={vehicle}
            items={items}
            selected={change?.initial.ids ?? selected}
            tyreIds={tyreIds}
            initial={change?.initial}
            waitingTyre={waitingTyre}
            waitingParked={!!change?.parked}
            submitLabel={change?.submitLabel}
            photosBusy={queue.busy || queue.failed > 0}
            photosPicked={queue.items.length > 0}
            read={mergeReadings(drafts.readings.values(), { DISCOUNT: 'TOTAL' })}
            readingDone={drafts.done}
            reading={drafts.pending.length > 0}
            readLater={readLater || !!change?.mayWait}
            wait={{ since: drafts.waitingSince, until: drafts.waitingUntil }}
            explanation={drafts.explanation}
            gallery={!change && <PhotoGallery kind="expenses" photos={[]} queue={queue} readingIds={drafts.pending} disabled={saving} onChanged={() => undefined} />}
            onSubmit={async (values, logsExpense) => {
              if (change) {
                // The visit's expense keeps its id (its kept photos and later edits name it); one it now logs takes the change's own.
                await onSubmit(logsExpense ? { ...values, expenseId: change.initial.expenseId ?? change.targetId ?? clientId } : values, [])
                close() // the caller tells what came of it
                return
              }
              // Without an expense the photos have nothing to belong to: they are not sent, and closing deletes them.
              // The id is only for the expense the visit logs; without one there is nothing it could name.
              if (await submit((photoIds) => onSubmit(logsExpense ? { ...values, expenseId: clientId } : values, photoIds), !logsExpense)) {
                close()
                // Kept on the device for the server (the server was out of reach): the toast says so. A line made without an amount (#290)
                // says it is on the tyre log alone.
                toast(
                  outbox.markOf('recurring', values.ids[0]) === 'done'
                    ? t('toast.savedOnDevice')
                    : values.tyre && !logsExpense
                      ? t('recurring.doneTyreLineAlone')
                      : t('toast.saved'),
                )
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
  tyreIds: TyreDetailsIds
  /** A visit edited on Waiting to sync: its values to start from. */
  initial?: Partial<DoneValues>
  /** Its tyre details, shown to fix or switch off (decision 71); switched off they are sent as none. */
  waitingTyre?: ExpenseTyreInput | null
  /** `waitingTyre` is a parked visit's: shown also with no set left. */
  waitingParked?: boolean
  submitLabel?: string
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
  // The vehicle's tyres (#290): its sets for the tyre details, and the last tyre cost's category; kept on the device. Without them (an
  // answer that failed) there is no switch, never an error of the dialog.
  const tyres = useQuery(VehicleTyresDocument, { variables: { vehicleId: props.vehicle.id }, fetchPolicy: 'network-only' })
  const d = defaults.data?.logDefaults
  if (!d || !(tyres.data || tyres.error)) return defaults.error ? <ErrorMessage error={defaults.error} /> : <Loading />
  return <DoneFields {...props} defaults={d} categories={categories.data?.expenseCategories ?? []} tyres={tyres.data?.vehicleTyres ?? null} />
}

function DoneFields({
  vehicle,
  items,
  selected,
  tyreIds,
  initial,
  waitingTyre = null,
  waitingParked = false,
  submitLabel,
  tyres,
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
}: FormProps & { defaults: NonNullable<LogDefaultsQuery['logDefaults']>; categories: string[]; tyres: VehicleTyresFieldsFragment | null }) {
  const { t } = useTranslation()
  const [ticked, setTicked] = useState(() => new Set(selected))
  // The expense's title and category follow the ticked schedules until the person types their own (null: not typed).
  const [title, setTitle] = useState<string | null>(initial?.title ?? null)
  const [category, setCategory] = useState<string | null>(initial?.category ?? null)
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
  const fill = useReadFill(
    initial
      ? {
          date: initial.date ?? todayIso(),
          odometer: initial.odometer?.toString() ?? '',
          amount: initial.amount?.toString() ?? '',
          discount: initial.discount?.toString() ?? '',
          currency: initial.currency ?? d.currency ?? '',
        }
      : { date: todayIso(), odometer: d.lastOdometer?.toString() ?? '', amount: '', discount: '', currency: d.currency ?? '' },
    READ_INTO,
    read,
    labels,
    readingDone,
    { ...explanation, last: last ? formatDistance(last.value, vehicle.units.distance) : undefined },
    undefined,
    false,
    { discount: 'amount' }, // a photo's discount was taken off its own total: next to an amount the user typed, it is only offered
  )
  const hasAmount = fill.values.amount.trim() !== ''
  // The amount is optional here: a discount without it would stop the whole visit on the server, so it is said at the field first.
  const discountWithoutAmount = !hasAmount && fill.values.discount.trim() !== '' && parseDecimal(fill.values.discount) !== undefined
  const logsExpense = hasAmount || reading || readLater
  // The title stays the schedules' with tyre details on (the owner's decision 64); the category opens them as in the Expense dialog.
  const titleValue = title ?? defaultTitle(done)
  const categoryValue = category ?? defaultCategory(done)
  const tyre = useExpenseTyreDetails({ tyres, category: categoryValue, setCategory, initial: waitingTyre, parked: waitingParked, suggested: suggestedKind(read.TYRE_WORK?.value) })
  const switchHint = useSwitchHint(tyre, false)
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
      const tyreInput = await tyre.inputOf(text('date'), tyreIds)
      if (tyreInput === 'invalid') return // said at What was done, which has the focus
      const values: DoneValues = {
        ids: done.map((i) => i.id),
        date: text('date'),
        odometer: text('odometer') === '' ? null : Number(text('odometer')),
        amount: hasAmount ? (parseDecimal(text('amount')) ?? null) : null,
        discount: hasAmount && text('discount') !== '' ? (parseDecimal(text('discount')) ?? null) : null, // without an amount the field says so before this
        currency: hasAmount ? text('currency').toUpperCase() : null,
        title: titleValue.trim(),
        category: categoryValue.trim(), // empty: no category (null would mean "the schedules' one" to the server)
        // Switched off, a change's tyre details go as none; without the tyres to show them (no answer) they stay as the change has them.
        ...(tyreInput ? { tyre: tyreInput.tyre } : waitingTyre && tyre.available ? { tyre: null } : {}),
      }
      // A visit with tyre details that will wait on this device (#290, decision 70): its tyre part replayed first, so a refusal the device
      // can foresee is said here. A visit whose amount is left to its photos logs an expense: a stand-in photo tells the replay so.
      if (tyreInput && tyres && !initial) {
        const { checkKept } = await import('./tyres/expenseTyreInput.ts')
        checkKept(vehicle.id, tyres, { entity: 'recurring', action: 'markDone', targetId: 'visit' }, { ...values, ...(logsExpense ? { expenseId: 'visit', photoIds: ['visit'] } : {}) })
      }
      await onSubmit(values, logsExpense)
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
              invalid={{
                message: discountWithoutAmount ? t('errors.cost.discountWithoutAmount') : t('forms.numberInvalid'),
                test: (v) => v.trim() !== '' && (parseDecimal(v) === undefined || !hasAmount),
              }}
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
        {tyres && (
          <TyreDetailsFields
            details={tyre}
            tyres={tyres}
            hint={switchHint}
            note={logsExpense ? t('expenses.tyreDetails.lineNote') : t('expenses.tyreDetails.lineAloneNote')}
          />
        )}
        {gallery}
        {photosPicked && !logsExpense && (
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            {t('recurring.photosNotKept')}
          </Typography>
        )}
        <div role="status" aria-label={t('a11y.readingStatus')}>
          <ReadingProgress active={reading} canSave={logsExpense} since={wait.since} until={wait.until} />
          {fill.announcement && <Typography variant="body2">{fill.announcement}</Typography>}
          {tyre.announcement && (
            <Typography variant="body2" style={visuallyHidden}>
              {tyre.announcement}
            </Typography>
          )}
          <ReadingProblems problems={fill.problems} />
        </div>
        {error !== undefined && <ErrorMessage error={error} />}
        <DialogButtons>
          <SaveWait waiting={photosBusy && !busy} />
          <DialogCancel disabled={busy} />
          <Button type="submit" loading={busy} disabled={photosBusy}>
            {submitLabel ?? t('recurring.doneSave')}
          </Button>
        </DialogButtons>
      </Stack>
    </Form>
  )
}
