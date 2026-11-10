import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Chip from '@mui/material/Chip'
import Paper from '@mui/material/Paper'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Archive, ArrowLeftRight, ArrowUpDown, CircleDot, CloudUpload, History, Pencil, Plus, Snowflake, Sun, Trash2 } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../../components/ConfirmDialog.tsx'
import { IconAction } from '../../components/IconAction.tsx'
import { Loading } from '../../components/Loading.tsx'
import { DialogButtons, DialogCancel, DialogFrame } from '../../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../../dialogs/DialogTrigger.tsx'
import { useDialogState } from '../../dialogs/useDialogState.ts'
import { todayIso } from '../../forms/dates.ts'
import { Field } from '../../forms/Field.tsx'
import { FieldInput } from '../../forms/FieldInput.tsx'
import { Form } from '../../forms/Form.tsx'
import { RetireTyreSetDocument, SetTyreLogDayDocument, UnretireTyreSetDocument, VehicleTyresDocument, type DistanceUnit, type TyreSeason } from '../../gql/generated.ts'
import { useFormat } from '../../i18n/format.ts'
import { ErrorMessage } from '../../messages.tsx'
import { HistoryDialog, RoughDialog } from '../../tyres/HistoryDialog.tsx'
import { neighbours, odometerOn, sortedReadings } from '../../tyres/odometer.ts'
import { PastSwapsDialog } from '../../tyres/PastSwapsDialog.tsx'
import { SwapDialog } from '../../tyres/SwapDialog.tsx'
import { TyreSetDialog } from '../../tyres/TyreSetDialog.tsx'
import { usePendingTyres } from '../../tyres/usePendingTyres.ts'
import { OfflineNote } from '../../components/OfflineNote.tsx'
import { isLocalLine } from '../../tyres/replay.ts'
import { type Tyres, type TyreSetInfo, useTyreMutation, useTyreToasts, useTyreWords, wholeInvalid, wholeOrNull } from '../../tyres/tyres.ts'

const SEASON_ICON: Record<TyreSeason, ReactNode> = {
  SUMMER: <Sun size={14} aria-hidden />,
  WINTER: <Snowflake size={14} aria-hidden />,
  ALL_SEASON: <CircleDot size={14} aria-hidden />,
}

/**
 * A vehicle's tyres (#287): the sets on the car, in storage and retired, each tracked as one thing with its distance (worked out by the
 * server from the vehicle's readings, ≈ where a day's odometer was worked out) and age, and the tyre log, a line per day a set went on.
 * Whoever may log for the vehicle swaps, changes positions, enters past swaps and changes a set's history; anyone else sees it.
 */
export function TyresPanel({ vehicle, canLog }: { vehicle: { id: string; units: { distance: DistanceUnit } }; canLog: boolean }) {
  const { t } = useTranslation()
  const unit = vehicle.units.distance
  const { data, error } = useQuery(VehicleTyresDocument, { variables: { vehicleId: vehicle.id }, fetchPolicy: 'cache-and-network' })
  const tyres = data?.vehicleTyres
  const pending = usePendingTyres(vehicle.id)
  if (error && !tyres) return <ErrorMessage error={error} />
  if (!tyres) return <Loading />

  const live = tyres.sets.filter((s) => !s.set.isRetired)
  const onCar = live.filter((s) => s.onTheCar)
  const stored = live.filter((s) => !s.onTheCar)
  const retired = tyres.sets.filter((s) => s.set.isRetired)
  const addSet = (label: string, primary = false) => (
    <TyreSetDialog
      vehicleId={vehicle.id}
      unit={unit}
      fittedFirst={onCar.length === 0}
      trigger={
        <Button variant={primary ? 'contained' : 'soft'} startIcon={<Plus size={16} aria-hidden />}>
          {label}
        </Button>
      }
    />
  )

  if (tyres.sets.length === 0)
    return (
      <Stack sx={{ gap: 1.5, alignItems: 'flex-start', p: 2.5, border: 1, borderStyle: 'dashed', borderColor: 'divider', borderRadius: 3 }}>
        <Typography component="h2" variant="h6">
          {t('tyres.empty.title')}
        </Typography>
        <Typography variant="body2" sx={{ color: 'text.secondary', maxWidth: '52ch' }}>
          {t('tyres.empty.text')}
        </Typography>
        {canLog ? addSet(t('tyres.addOnCar'), true) : <Typography variant="body2">{t('tyres.viewOnly')}</Typography>}
      </Stack>
    )

  const card = (info: TyreSetInfo) => (
    <SetCard
      key={info.set.id}
      info={info}
      tyres={tyres}
      vehicleId={vehicle.id}
      unit={unit}
      canLog={canLog}
      canSwap={stored.length > 0 || !info.onTheCar}
      pending={pending.get(info.set.id) ?? null}
    />
  )
  return (
    <Stack sx={{ gap: 2 }}>
      {!canLog && (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t('tyres.viewOnly')}
        </Typography>
      )}
      <Box sx={{ display: 'grid', gap: 3, gridTemplateColumns: { xs: 'minmax(0,1fr)', md: 'minmax(0,1.1fr) minmax(0,1fr)' }, alignItems: 'start' }}>
        <Stack sx={{ gap: 2.5, minWidth: 0 }}>
          <Section title={t('tyres.onCar')}>
            {onCar.length > 0 ? onCar.map(card) : <Typography variant="body2" sx={{ color: 'text.secondary' }}>{t('tyres.noneOnCar')}</Typography>}
          </Section>
          {stored.length > 0 && <Section title={t('tyres.inStorage')}>{stored.map(card)}</Section>}
          {canLog && (
            <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
              {addSet(t('tyres.addSet'))}
              {live.length > 0 && (
                <PastSwapsDialog
                  vehicleId={vehicle.id}
                  unit={unit}
                  tyres={tyres}
                  trigger={
                    <Button variant="soft" startIcon={<History size={16} aria-hidden />}>
                      {t('tyres.addPast')}
                    </Button>
                  }
                />
              )}
            </Stack>
          )}
          {retired.length > 0 && <Section title={t('tyres.retired')}>{retired.map(card)}</Section>}
        </Stack>
        <TyreLog tyres={tyres} vehicleId={vehicle.id} unit={unit} canLog={canLog} />
      </Box>
    </Stack>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Stack component="section" sx={{ gap: 1.25, minWidth: 0 }}>
      <Typography component="h2" variant="overline" sx={{ color: 'text.secondary', lineHeight: 1.6 }}>
        {title}
      </Typography>
      {children}
    </Stack>
  )
}

/** A set's card; `pending`: a change waiting on this device touched it (shown done, its figures the server's to work out once synced). */
function SetCard({
  info,
  tyres,
  vehicleId,
  unit,
  canLog,
  canSwap,
  pending,
}: {
  info: TyreSetInfo
  tyres: Tyres
  vehicleId: string
  unit: DistanceUnit
  canLog: boolean
  canSwap: boolean
  pending: 'new' | 'changed' | null
}) {
  const { t } = useTranslation()
  const format = useFormat()
  const words = useTyreWords(unit)
  const notify = useTyreToasts()
  const run = useTyreMutation(vehicleId)
  const [error, setError] = useState<unknown>()
  const { set, figures } = info
  const open = info.stretches.find((s) => s.stretch.until == null)
  const last = info.stretches[0]?.stretch
  const measuredFrom = info.stretches.length > 0 ? info.stretches[info.stretches.length - 1].stretch.from : null
  const nothingMeasured = !tyres.hasReadings || (info.stretches.length > 0 && info.stretches.every((s) => s.distance.measure === 'NOT_MEASURED'))

  const retire = async () => {
    setError(undefined)
    try {
      const saved = await run(RetireTyreSetDocument, { id: set.id, day: todayIso() })
      notify.undoable(saved, t('tyres.toast.retired'), () => run(UnretireTyreSetDocument, { id: set.id }))
    } catch (err) {
      setError(err)
    }
  }

  return (
    <Paper variant="outlined" component="article" aria-label={set.name} sx={{ p: 1.75, borderRadius: 3, display: 'flex', flexDirection: 'column', gap: 1, minWidth: 0 }}>
      <Stack direction="row" sx={{ gap: 1, alignItems: 'center', flexWrap: 'wrap' }}>
        <Chip size="small" icon={<>{SEASON_ICON[set.season]}</>} label={t(`tyres.seasons.${set.season}`)} />
        <Typography component="h3" variant="subtitle1" sx={{ fontWeight: 700, minWidth: 0, overflowWrap: 'anywhere' }}>
          {set.name}
        </Typography>
        {set.size && (
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {set.size}
          </Typography>
        )}
        {pending && <Chip size="small" className="tk-fade" icon={<CloudUpload size={14} aria-hidden />} label={t(`offline.pending.${pending}`)} />}
      </Stack>
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {set.isRetired && set.retiredOn
          ? t('tyres.retiredOn', { date: format.date(set.retiredOn) })
          : open
            ? t('tyres.onSince', {
                date: format.date(open.stretch.from),
                distance: pending ? t('tyres.updatedOnSync') : words.distance(open.distance.distance, open.distance.approximate),
                pairs: words.pairs(open.stretch.pairOneFront),
              })
            : t('tyres.storedLast', { pairs: words.pairs(last?.pairOneFront) })}
      </Typography>
      <Box component="dl" sx={{ m: 0, display: 'flex', flexWrap: 'wrap', gap: '6px 24px' }}>
        <Box>
          <Typography component="dt" variant="caption" sx={{ color: 'text.secondary' }}>
            {t('tyres.distance')}
          </Typography>
          <Typography component="dd" variant="body1" sx={{ m: 0, fontWeight: 700, fontVariantNumeric: 'tabular-nums' }}>
            {pending ? t('tyres.updatedOnSync') : words.distance(figures.distance, figures.approximate)}
          </Typography>
        </Box>
        <Box>
          <Typography component="dt" variant="caption" sx={{ color: 'text.secondary' }}>
            {t('tyres.age')}
          </Typography>
          <Typography component="dd" variant="body1" sx={{ m: 0, fontWeight: 700 }}>
            {words.age(set)}
          </Typography>
        </Box>
      </Box>
      {nothingMeasured && set.roughDistance > 0 && (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t('tyres.noReading')}
        </Typography>
      )}
      {figures.suggestion && measuredFrom && (
        <Typography variant="body2" sx={{ p: 1, borderRadius: 2, bgcolor: 'action.hover' }}>
          {t('tyres.roughNote', { date: format.date(figures.suggestion.until) })}{' '}
          {canLog && <RoughDialog vehicleId={vehicleId} unit={unit} info={info} measuredFrom={measuredFrom} trigger={<Button variant="text" size="small">{t('tyres.checkRough')}</Button>} />}
        </Typography>
      )}
      {error !== undefined && <ErrorMessage error={error} />}
      <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
        {canLog && !set.isRetired && info.onTheCar && canSwap && (
          <SwapDialog vehicleId={vehicleId} unit={unit} tyres={tyres} trigger={<Button startIcon={<ArrowLeftRight size={16} aria-hidden />}>{t('tyres.swap')}</Button>} />
        )}
        {canLog && !set.isRetired && info.onTheCar && (
          <SwapDialog vehicleId={vehicleId} unit={unit} tyres={tyres} rotate setId={set.id} trigger={<Button variant="soft" startIcon={<ArrowUpDown size={16} aria-hidden />}>{t('tyres.rotate')}</Button>} />
        )}
        {canLog && !set.isRetired && !info.onTheCar && (
          <SwapDialog
            vehicleId={vehicleId}
            unit={unit}
            tyres={tyres}
            setId={set.id}
            trigger={
              <Button variant="soft" startIcon={<ArrowLeftRight size={16} aria-hidden />}>
                {tyres.sets.some((s) => s.onTheCar) ? t('tyres.swapTo') : t('tyres.fit')}
              </Button>
            }
          />
        )}
        <HistoryDialog vehicleId={vehicleId} unit={unit} tyres={tyres} setId={set.id} canLog={canLog} trigger={<Button variant="soft" startIcon={<History size={16} aria-hidden />}>{t('tyres.history')}</Button>} />
        {canLog && !set.isRetired && !info.onTheCar && (
          <ConfirmDialog
            trigger={
              <Button variant="soft" startIcon={<Archive size={16} aria-hidden />}>
                {t('tyres.retire')}
              </Button>
            }
            title={t('tyres.dialogs.retireTitle')}
            description={t('tyres.dialogs.retireText', { set: set.name })}
            confirmLabel={t('tyres.dialogs.retireSet')}
            onConfirm={() => void retire()}
          />
        )}
      </Stack>
    </Paper>
  )
}

/** A line per day a set went on (from the stretches) and per day with only a reading left (its stretches in the trash), newest first. */
function TyreLog({ tyres, vehicleId, unit, canLog }: { tyres: Tyres; vehicleId: string; unit: DistanceUnit; canLog: boolean }) {
  const { t } = useTranslation()
  const format = useFormat()
  const words = useTyreWords(unit)
  const sorted = sortedReadings(tyres.readings)
  const lines = new Map<string, { day: string; title: string; sub: string }>()
  for (const info of tyres.sets)
    for (const s of info.stretches) {
      const key = `${s.stretch.from}|${info.set.id}`
      if (lines.has(key)) continue
      const pairs = s.stretch.pairOneFront == null ? t('tyres.wholeSet') : t('tyres.wholeSetPairs', { pairs: words.pairs(s.stretch.pairOneFront) })
      lines.set(key, { day: s.stretch.from, title: t('tyres.onTheCarLine', { set: info.set.name }), sub: pairs })
    }
  for (const d of tyres.days)
    if (![...lines.values()].some((l) => l.day === d.day)) lines.set(`${d.day}|`, { day: d.day, title: t('tyres.readingOnly'), sub: d.note ?? '' })
  const sortedLines = [...lines.values()].sort((a, b) => (a.day < b.day ? 1 : a.day > b.day ? -1 : 0))

  return (
    <Stack component="section" aria-labelledby="tyre-log-title" sx={{ gap: 1, minWidth: 0 }}>
      <Typography id="tyre-log-title" component="h2" variant="overline" sx={{ color: 'text.secondary', lineHeight: 1.6 }}>
        {t('tyres.log')}
      </Typography>
      {sortedLines.length === 0 ? (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t('tyres.empty.log')}
        </Typography>
      ) : (
        <Box component="ol" sx={{ listStyle: 'none', m: 0, p: 0 }}>
          {sortedLines.map((line) => {
            const day = tyres.days.find((d) => d.day === line.day)
            const on = odometerOn(sorted, line.day)
            const odometer = day?.odometer != null ? words.distance(day.odometer) : on ? words.distance(on.value, on.approximate) : t('tyres.beforeLog')
            return (
              <Box component="li" key={`${line.day}${line.title}`} sx={{ display: 'flex', gap: 1, alignItems: 'center', py: 1, borderTop: 1, borderColor: 'divider', '&:first-of-type': { borderTop: 0 } }}>
                <Box sx={{ flex: 1, minWidth: 0 }}>
                  <Typography variant="body2" sx={{ fontWeight: 600, overflowWrap: 'anywhere' }}>
                    {line.title}
                  </Typography>
                  {line.sub && (
                    <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                      {line.sub}
                    </Typography>
                  )}
                  <Typography variant="caption" sx={{ color: 'text.secondary', fontVariantNumeric: 'tabular-nums' }}>
                    {format.date(line.day)} · {odometer}
                    {day?.odometer != null && ` · ${t('tyres.readingTyped')}`}
                  </Typography>
                </Box>
                {canLog && day?.odometer != null && <ReadingDialog vehicleId={vehicleId} unit={unit} tyres={tyres} day={line.day} lineId={day.id} value={day.odometer} note={day.note ?? null} />}
              </Box>
            )
          })}
        </Box>
      )}
    </Stack>
  )
}

/** The reading typed with a swap on a day: one of the car's readings, changed here or removed for good (#287 Q1). */
/**
 * A tyre log day's reading. The change names the line (its id, and through it its version, `useTyreMutation`), so one made from a line the
 * server has since changed is parked, never written over it; a line a waiting change wrote on this device has no id the server knows yet,
 * so it is sent without one.
 */
function ReadingDialog({
  vehicleId,
  unit,
  tyres,
  day,
  lineId,
  value,
  note,
}: {
  vehicleId: string
  unit: DistanceUnit
  tyres: Tyres
  day: string
  lineId: string
  value: number
  note: string | null
}) {
  const { t } = useTranslation()
  const format = useFormat()
  const notify = useTyreToasts()
  const words = useTyreWords(unit)
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState()
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  const { previous, next } = neighbours(sortedReadings(tyres.readings), day)
  const hint =
    previous && next
      ? t('tyres.odoHint.neighbours', { previous: t('tyres.readingOn', { value: words.distance(previous.value), date: format.date(previous.date) }), next: t('tyres.readingOn', { value: words.distance(next.value), date: format.date(next.date) }) })
      : previous
        ? t('tyres.odoHint.after', { previous: t('tyres.readingOn', { value: words.distance(previous.value), date: format.date(previous.date) }) })
        : next
          ? t('tyres.odoHint.beforeOnly', { next: t('tyres.readingOn', { value: words.distance(next.value), date: format.date(next.date) }) })
          : undefined

  const save = async (odometer: number | null) => {
    setBusy(true)
    setError(undefined)
    try {
      const saved = await run(SetTyreLogDayDocument, { input: { vehicleId, day, odometer, note, ...(isLocalLine(lineId) ? {} : { id: lineId }) } })
      setOpen(false)
      notify.saved(saved, t('toast.saved'))
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }
  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    void save(wholeOrNull(String(new FormData(e.currentTarget).get('odometer') ?? '')))
  }

  return (
    <>
      <DialogTrigger
        trigger={
          <IconAction label={t('tyres.editReading', { date: format.date(day) })}>
            <Pencil size={16} aria-hidden />
          </IconAction>
        }
        open={open}
        onOpen={() => setOpen(true)}
      />
      <DialogFrame open={open} onClose={() => setOpen(false)} onClosed={() => setError(undefined)} busy={busy} maxWidth={460} title={t('tyres.dialogs.readingTitle', { date: format.date(day) })} description={t('tyres.dialogs.readingText')}>
        {open && (
          <Form onSubmit={submit} refusal={error}>
            <OfflineNote />
            <Stack sx={{ gap: 1.5 }}>
              <Field name="odometer" label={t('tyres.fields.odometerPlain', { unit: words.unitShort })} hint={hint} required invalid={wholeInvalid(t('forms.numberInvalid'))}>
                <FieldInput inputMode="numeric" autoComplete="off" defaultValue={String(value)} />
              </Field>
              {error !== undefined && <ErrorMessage error={error} />}
              <DialogButtons>
                <ConfirmDialog
                  trigger={
                    <Button variant="soft" color="error" startIcon={<Trash2 size={16} aria-hidden />} sx={{ mr: 'auto' }}>
                      {t('tyres.dialogs.removeReading')}
                    </Button>
                  }
                  title={t('tyres.dialogs.removeReadingTitle')}
                  description={t('tyres.dialogs.removeReadingText', { date: format.date(day), value: words.distance(value) })}
                  confirmLabel={t('tyres.dialogs.removeReading')}
                  onConfirm={() => void save(null)}
                />
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
