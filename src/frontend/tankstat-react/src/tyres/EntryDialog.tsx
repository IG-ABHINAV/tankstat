import Button from '@mui/material/Button'
import Checkbox from '@mui/material/Checkbox'
import FormControlLabel from '@mui/material/FormControlLabel'
import Radio from '@mui/material/Radio'
import RadioGroup from '@mui/material/RadioGroup'
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
import { ENTRY_KINDS, feeOf, groupOf, isSwapLine, labelOf, labelsOf, sharersOf, useEntryWords } from './entries.ts'
import { FeeFields } from './FeeFields.tsx'
import { ForTyres } from './ForTyres.tsx'
import { EXACT, liveTyresOf, suggestLabels, type ExactCorner } from './singleTyres.ts'
import { TyreRuleError } from './tyreHistory.ts'
import { type TyreEntry, type Tyres, useTyreMutation, useTyreToasts, useTyreWords, wholeInvalid, wholeOrNull } from './tyres.ts'

/**
 * Adds a line to the tyre log, or changes one (#288): what was done, the set (or none), the day, the day's reading and a note. A swap's line
 * keeps its kind and set (both shown, disabled, with why in the hint); its day can change, which moves the swap, by the history's rules: a
 * refusal (`tyres.swapBeforePrevious`, `tyres.twoSetsAtOnce`, ...) is said here.
 *
 * Single tyres (#289): an add for a set has *For*, the whole set or the tyres tapped (a line each, sharing one expense; a whole set is broken
 * up on save). An edit of a tyre's line has *Also change it for* the other lines made together with it, all ticked when opened from the log
 * row (`group`), none from a tyre's history; an amount shared with lines not changed here, emptied, asks on save whether only these let go
 * of it or the expense goes to the trash.
 */
export function EntryDialog({
  trigger,
  vehicleId,
  unit,
  tyres,
  entry,
  group,
  tyreId,
  open: openAsked,
  onOpenChange,
}: {
  trigger: ReactNode
  vehicleId: string
  unit: DistanceUnit
  tyres: Tyres
  /** The line to change; none adds one. */
  entry?: TyreEntry
  /** The log row's lines made together with `entry` (#289): opened from the row, every one of them ticked under *Also change it for*. */
  group?: readonly TyreEntry[]
  /** An add for this single tyre (its dialog's *Add entry*). */
  tyreId?: string
  /** Opened by something else (an expense's *Open in the tyre log*). */
  open?: boolean
  onOpenChange?: (open: boolean) => void
}) {
  const { t } = useTranslation()
  const format = useFormat()
  const words = useTyreWords(unit)
  const entryWords = useEntryWords()
  const notify = useTyreToasts()
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState({ open: openAsked, onOpenChange })
  const id = useClientId(open)
  const expenseId = useClientId(open)
  const lineIds = [useClientId(open), useClientId(open), useClientId(open), useClientId(open)]
  const newTyreIds = [useClientId(open), useClientId(open), useClientId(open), useClientId(open)]
  const locked = !!entry && isSwapLine(entry)
  // Its expense (#288): the amount, live or in the trash, and whether the day and the reading are shared with one now.
  const linked = !!entry?.expenseId && !entry.expenseTrashed
  const ownSet = (id: string | undefined) => (id ? tyres.sets.find((s) => s.set.id === id) : undefined)
  const setOfTyre = tyreId ? tyres.sets.find((s) => (s.tyres ?? []).some((x) => x.tyre.id === tyreId)) : undefined
  const startSet = () => entry?.setId ?? setOfTyre?.set.id ?? (tyres.sets.find((s) => s.onTheCar)?.set.id ?? '')
  const [kind, setKind] = useState<TyreEntryKind>(entry?.kind ?? 'PUNCTURE_REPAIRED')
  const [setId, setSet] = useState(startSet)
  const [day, setDay] = useState(entry?.day ?? todayIso())
  // *For* (#289): the whole set, or the tyres picked (ids, or a whole set's corners), with a whole set's labels.
  const [whole, setWhole] = useState(!tyreId)
  const [picked, setPicked] = useState<string[]>(tyreId ? [tyreId] : [])
  const [labels, setLabels] = useState<string[]>([])
  // *Also change it for* (#289), and the ask on save of a shared amount emptied.
  const others = entry ? groupOf(tyres, entry).slice(1) : []
  const alsoAtFirst = () => (group ? group.filter((l) => l.id !== entry?.id).map((l) => l.id) : [])
  const [also, setAlso] = useState<string[]>(alsoAtFirst) // also when opened by something else, without the trigger's reset
  const [asking, setAsking] = useState<null | ((choice: 'LET_GO' | 'TRASH') => Promise<void>)>(null)
  const [choice, setChoice] = useState<'LET_GO' | 'TRASH'>('LET_GO')
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)

  const startFor = (forSet: string) => {
    setWhole(!tyreId)
    setPicked(tyreId ? [tyreId] : [])
    const info = ownSet(forSet)
    setLabels(info && !info.set.brokenUpOn ? suggestLabels(tyres, info.set.season) : [])
  }
  const reset = () => {
    setKind(entry?.kind ?? 'PUNCTURE_REPAIRED')
    const first = startSet()
    setSet(first)
    setDay(entry?.day ?? todayIso())
    startFor(first)
    setAlso(alsoAtFirst())
    setAsking(null)
    setChoice('LET_GO')
    setError(undefined)
  }
  const info = ownSet(setId)
  const forTyres = !entry && !!info && !info.set.isRetired

  async function send(make: () => Promise<void>) {
    setBusy(true)
    setError(undefined)
    try {
      await make()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    const text = (name: string) => String(form.get(name) ?? '').trim()
    // A note takes no amount: one it had (another kind before) goes, with its expense, to the trash.
    const fee = kind === 'NOTE'
      ? entry?.expenseId ? { amount: null } : null
      : feeOf(form, entryWords.feeTitle(kind, entryWords.setName(tyres, setId || null)), entry?.expenseId ?? expenseId) // its own expense, when it has one
    const values = { kind, setId: setId || null, day: text('day'), odometer: wholeOrNull(text('odometer')), note: text('note') || null, ...(fee ? { fee } : {}) }
    const paid = !!fee && fee.amount != null
    if (!entry) {
      void send(async () => {
        let lines: { tyreId: string; id: string }[] | undefined
        let breakUp: { setId: string; day: string; tyres: { id: string; label: string; corner: ExactCorner }[] } | undefined
        let named = ''
        if (forTyres && !whole) {
          if (picked.length === 0) throw new TyreRuleError('tyres.tapTyres')
          if (info.set.brokenUpOn) {
            const ordered = liveTyresOf(info).filter((x) => picked.includes(x.tyre.id))
            lines = ordered.map((x, n) => ({ tyreId: x.tyre.id, id: lineIds[n] }))
            named = ordered.map((x) => x.tyre.label).join(', ')
          } else {
            // A whole set: broken up first (the owner's decision 19), each tyre at its corner, then a line for each tyre tapped.
            breakUp = { setId, day: values.day, tyres: EXACT.map((corner, n) => ({ id: newTyreIds[n], label: (labels[n] ?? '').trim(), corner })) }
            const ordered = EXACT.filter((c) => picked.includes(c))
            lines = ordered.map((c, n) => ({ tyreId: newTyreIds[EXACT.indexOf(c)], id: lineIds[n] }))
            named = ordered.map((c) => (labels[EXACT.indexOf(c)] ?? '').trim()).join(', ')
          }
        }
        const saved = await run(AddTyreEntryDocument, { input: { vehicleId, id: lines?.[0]?.id ?? id, ...values, ...(lines ? { tyres: lines } : {}), ...(breakUp ? { breakUp } : {}) } })
        setOpen(false)
        notify.saved(saved, named ? t(paid ? 'tyres.entries.savedFor' : 'tyres.entries.savedForNoFee', { tyres: named }) : t('toast.saved'))
      })
      return
    }
    const changing = [entry, ...others.filter((o) => also.includes(o.id))]
    const keeping = sharersOf(tyres, entry).filter((s) => !changing.some((c) => c.id === s.id))
    const ownDays = values.day !== entry.day || values.odometer !== (entry.odometer ?? null)
    // A day or odometer of its own parts it from the expense the others keep (the owner's decision 29): no amount goes, so a change kept
    // on the device never lays its day over that expense (`tyreFeeExpense`). The server drops it too.
    if (keeping.length > 0 && ownDays) delete (values as { fee?: unknown }).fee
    // An amount shared with lines not changed here, emptied: the person says what it does (the owner's decision 30). A line that parts
    // (a day or odometer of its own) keeps no amount anyway.
    const emptiesShared = keeping.length > 0 && !ownDays && !!fee && fee.amount == null && entry.amount != null
    const save = (sharedExpense?: 'LET_GO' | 'TRASH') => send(async () => {
      const input = { id: entry.id, ...values, ...(also.length ? { also: changing.slice(1).map((c) => c.id) } : {}), ...(sharedExpense ? { sharedExpense } : {}) }
      const saved = await run(UpdateTyreEntryDocument, { input })
      setOpen(false)
      notify.saved(saved, changing.length > 1 ? t('tyres.entries.changedFor', { tyres: labelsOf(tyres, changing) }) : t('toast.saved'))
    })
    if (emptiesShared) {
      setAsking(() => (picked_: 'LET_GO' | 'TRASH') => save(picked_))
      return
    }
    void save()
  }

  const kinds = locked ? [entry.kind] : ENTRY_KINDS
  const sets = [
    { value: '', label: t('tyres.entries.noSet') },
    ...tyres.sets.map((s) => ({ value: s.set.id, label: `${t(`tyres.seasons.${s.set.season}`)}: ${s.set.name}` })),
  ]
  // A tyre's line (#289): its set stays; its amount may be one expense shared with other tyres' lines.
  const ofTyre = !!entry?.tyreId
  const sharers = entry ? sharersOf(tyres, entry) : []
  const sharedHint = sharers.length > 0
    ? t('tyres.entries.sharedAmountHint', { tyres: labelsOf(tyres, sharers) })
    : forTyres && !whole && picked.length > 1 ? t('tyres.entries.oneExpense') : undefined
  const amountText = entry?.amount != null ? format.money(entry.amount, entry.currency ?? '') : ''
  const thisTyre = entry ? (labelOf(tyres, entry.tyreId) ?? '') : ''
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
                  disabled={locked || ofTyre || !!tyreId}
                  hint={locked ? t('tyres.entries.lockedHint') : ofTyre ? t('tyres.entries.tyreSetHint') : undefined}
                  options={sets}
                  onChange={(next) => {
                    setSet(next)
                    startFor(next)
                  }}
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
              {forTyres && info && (
                <ForTyres
                  info={info}
                  whole={whole}
                  onWhole={(next) => {
                    setWhole(next)
                    setPicked([])
                  }}
                  picked={picked}
                  onPicked={setPicked}
                  labels={labels}
                  onLabels={setLabels}
                  locked={!!tyreId}
                />
              )}
              {others.length > 0 && (
                <Stack component="fieldset" sx={{ gap: 0.5, border: 0, p: 0, m: 0, minWidth: 0 }}>
                  <Typography component="legend" variant="body2" sx={{ fontWeight: 600, p: 0, mb: 0.5 }}>
                    {t('tyres.entries.alsoFor')}
                  </Typography>
                  <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
                    {others.map((o) => (
                      <FormControlLabel
                        key={o.id}
                        control={<Checkbox checked={also.includes(o.id)} onChange={(e) => setAlso(e.target.checked ? [...also, o.id] : also.filter((a) => a !== o.id))} />}
                        label={t('tyres.single.tyreTitle', { label: labelOf(tyres, o.tyreId) ?? '' })}
                      />
                    ))}
                  </Stack>
                  <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                    {[group ? t('tyres.entries.alsoFromLog') : '', t('tyres.entries.alsoHint'), sharers.length > 0 ? t('tyres.entries.partsHint') : ''].filter(Boolean).join(' ')}
                  </Typography>
                </Stack>
              )}
              {kind !== 'NOTE' && (
                <FeeFields
                  vehicleId={vehicleId}
                  tyres={tyres}
                  label={t('tyres.fee.paid')}
                  amount={entry?.amount}
                  currency={entry?.currency}
                  category={entry?.category}
                  linked={linked}
                  shared={sharedHint}
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
      {entry && (
        <DialogFrame
          open={asking !== null}
          onClose={() => setAsking(null)}
          maxWidth={480}
          title={t('tyres.entries.ask.title')}
          description={t('tyres.entries.ask.text', { amount: amountText, tyres: labelsOf(tyres, [entry, ...sharers]) })}
        >
          <RadioGroup value={choice} onChange={(e) => setChoice(e.target.value as 'LET_GO' | 'TRASH')} aria-label={t('tyres.entries.ask.title')}>
            {(['LET_GO', 'TRASH'] as const).map((value) => {
              const keepers = sharers.filter((s) => !also.includes(s.id))
              const [label, hint] = value === 'LET_GO'
                ? [t('tyres.entries.ask.letGo', { tyre: also.length ? labelsOf(tyres, [entry, ...others.filter((o) => also.includes(o.id))]) : thisTyre }), t('tyres.entries.ask.letGoHint', { tyres: labelsOf(tyres, keepers) })]
                : [t('tyres.entries.ask.trash'), t('tyres.entries.ask.trashHint')]
              return (
                <FormControlLabel
                  key={value}
                  value={value}
                  control={<Radio />}
                  sx={{ alignItems: 'flex-start', mb: 1 }}
                  label={
                    <Stack sx={{ pt: 1 }}>
                      <Typography variant="body2" sx={{ fontWeight: 600 }}>{label}</Typography>
                      <Typography variant="body2" sx={{ color: 'text.secondary' }}>{hint}</Typography>
                    </Stack>
                  }
                />
              )
            })}
          </RadioGroup>
          <DialogButtons>
            <DialogCancel />
            <Button
              onClick={() => {
                const go = asking
                setAsking(null)
                void go?.(choice)
              }}
            >
              {t('tyres.dialogs.save')}
            </Button>
          </DialogButtons>
        </DialogFrame>
      )}
    </>
  )
}

/**
 * Moves a line to the trash, asking first (#288): the line in one sentence; for a swap's line, that the swap goes with it (its time on the
 * car no longer counts); with an expense, a box (ticked at first) to move that along too, else it stays with the reading; and when the
 * reading goes with them, that it no longer counts until the line is restored. Undo restores it (its expense with it when it went along).
 *
 * A row of lines made together (#289, `lines`) asks which tyres' lines go, every tyre ticked at first. The box shows only while every line
 * holding the expense goes (the last holder takes it along, the owner's decision 33); otherwise the expense stays with the others. Each
 * ticked line is its own `deleteTyreEntry`; Undo restores every one.
 */
export function TrashEntryDialog({
  vehicleId,
  unit,
  tyres,
  entry,
  lines = [entry],
  onError,
}: {
  vehicleId: string
  unit: DistanceUnit
  tyres: Tyres
  entry: TyreEntry
  /** The log row's lines (#289): the lines made together with `entry`, itself first. */
  lines?: readonly TyreEntry[]
  onError: (error: unknown) => void
}) {
  const { t } = useTranslation()
  const format = useFormat()
  const words = useTyreWords(unit)
  const entryWords = useEntryWords()
  const notify = useTyreToasts()
  const run = useTyreMutation(vehicleId)
  // The lines unticked: what goes is worked out from the row's lines as they are now, so a line that left the row since is never counted.
  const [left, setLeft] = useState<string[]>([])
  const going = lines.filter((l) => !left.includes(l.id)).map((l) => l.id)
  const [withExpense, setWithExpense] = useState(true)
  const several = lines.length > 1
  const installation = tyres.sets.flatMap((s) => s.installations).find((x) => x.installation.id === entry.installationId)?.installation
  const set = entryWords.setName(tyres, entry.setId) ?? ''
  const parts = [`${entryWords.name(tyres, entry, lines)}${entry.note ? ` · ${entry.note}` : ''}.`]
  if (installation)
    parts.push(
      installation.until
        ? t('tyres.entries.trashSwapUntil', { set, from: format.date(installation.from), until: format.date(installation.until) })
        : t('tyres.entries.trashSwap', { set, from: format.date(installation.from) }),
    )
  const paid = !!entry.expenseId && !entry.expenseTrashed && entry.amount != null
  // Who holds the expense besides the lines going: while any does, it stays with them.
  const keepers = paid ? [entry, ...sharersOf(tyres, entry)].filter((h) => !going.includes(h.id)) : []
  const expense = paid && keepers.length === 0
  const amount = paid ? format.money(entry.amount!, entry.currency ?? '') : ''
  const reading = entry.odometer != null ? words.distance(entry.odometer) : null
  if (!several && !paid && reading) parts.push(t('tyres.entries.trashReading', { value: reading }))
  parts.push(t('tyres.entries.restorable'))

  const trash = async () => {
    const chosen = lines.filter((l) => going.includes(l.id))
    if (chosen.length === 0) return
    try {
      const saved = []
      for (const line of chosen) saved.push(await run(DeleteTyreEntryDocument, { input: { id: line.id, withExpense: expense && withExpense } }))
      notify.undoableAll(saved, t('tyres.toast.trashed'), (n) => run(RestoreTyreEntryDocument, { id: chosen[n].id }))
    } catch (err) {
      onError(err)
    }
  }
  const tick = (id: string, on: boolean) => setLeft(on ? left.filter((l) => l !== id) : [...left, id])
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
      confirmDisabled={going.length === 0}
      onClosed={() => {
        setWithExpense(true)
        setLeft([])
      }}
    >
      <Stack sx={{ gap: 0.5, mb: 1 }}>
        {several && (
          <Stack component="fieldset" sx={{ gap: 0.25, border: 0, p: 0, m: 0, minWidth: 0 }}>
            <Typography component="legend" variant="body2" sx={{ fontWeight: 600, p: 0, mb: 0.5 }}>
              {t('tyres.entries.bin.which')}
            </Typography>
            <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
              {lines.map((l) => (
                <FormControlLabel
                  key={l.id}
                  control={<Checkbox checked={going.includes(l.id)} onChange={(e) => tick(l.id, e.target.checked)} />}
                  label={t('tyres.single.tyreTitle', { label: labelOf(tyres, l.tyreId) ?? '' })}
                />
              ))}
            </Stack>
            <Typography variant="body2" sx={{ color: 'text.secondary' }}>
              {t('tyres.entries.bin.whichHint')}
            </Typography>
            {going.length === 0 && (
              <Typography variant="body2" role="alert" sx={{ color: 'error.main' }}>
                {t('tyres.entries.bin.tickOne')}
              </Typography>
            )}
          </Stack>
        )}
        {expense && (
          <>
            <FormControlLabel
              control={<Checkbox checked={withExpense} onChange={(e) => setWithExpense(e.target.checked)} />}
              label={t('tyres.fee.alsoExpense', { amount })}
            />
            <Typography variant="body2" sx={{ color: 'text.secondary' }} aria-live="polite">
              {withExpense
                ? reading && t('tyres.fee.readingWithExpense', { value: reading })
                : t(reading ? 'tyres.fee.staysWithReading' : 'tyres.fee.stays')}
            </Typography>
          </>
        )}
        {paid && !expense && going.length > 0 && (
          <Typography variant="body2" sx={{ color: 'text.secondary' }} aria-live="polite">
            {t('tyres.entries.bin.stays', { amount, tyres: labelsOf(tyres, keepers) })}
          </Typography>
        )}
        {several && reading && going.length > 0 && !expense && (
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {t('tyres.entries.bin.reading')}
          </Typography>
        )}
      </Stack>
    </ConfirmDialog>
  )
}
