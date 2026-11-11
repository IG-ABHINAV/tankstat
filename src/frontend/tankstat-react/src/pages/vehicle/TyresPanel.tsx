import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import ButtonBase from '@mui/material/ButtonBase'
import Button from '@mui/material/Button'
import Chip from '@mui/material/Chip'
import Paper from '@mui/material/Paper'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Archive, ArrowLeftRight, ArrowUpDown, CircleDot, CloudUpload, History, NotebookPen, Plus, Snowflake, Sun } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../../components/ConfirmDialog.tsx'
import { Loading } from '../../components/Loading.tsx'
import { todayIso } from '../../forms/dates.ts'
import { RetireTyreSetDocument, UnretireTyreSetDocument, VehicleTyresDocument, type DistanceUnit, type TyreSeason } from '../../gql/generated.ts'
import { useFormat } from '../../i18n/format.ts'
import { ErrorMessage } from '../../messages.tsx'
import { HistoryDialog, RoughDialog } from '../../tyres/HistoryDialog.tsx'
import { odometerOn, sortedReadings } from '../../tyres/odometer.ts'
import { PastSwapsDialog } from '../../tyres/PastSwapsDialog.tsx'
import { SwapDialog } from '../../tyres/SwapDialog.tsx'
import { TyreSetDialog } from '../../tyres/TyreSetDialog.tsx'
import { usePendingTyres } from '../../tyres/usePendingTyres.ts'
import { noTyres } from '../../tyres/replay.ts'
import { EntryDialog, TrashEntryDialog } from '../../tyres/EntryDialog.tsx'
import { isSwapLine, useEntryWords } from '../../tyres/entries.ts'
import { type Tyres, type TyreSetInfo, useTyreMutation, useTyreToasts, useTyreWords } from '../../tyres/tyres.ts'

const SEASON_ICON: Record<TyreSeason, ReactNode> = {
  SUMMER: <Sun size={14} aria-hidden />,
  WINTER: <Snowflake size={14} aria-hidden />,
  ALL_SEASON: <CircleDot size={14} aria-hidden />,
}

/**
 * A vehicle's tyres (#287): the sets on the car, in storage and retired, each tracked as one thing with its distance (worked out by the
 * server from the vehicle's readings, ≈ where a day's odometer was worked out) and age, and the tyre log, a line per thing done (#288).
 * Whoever may log for the vehicle swaps, changes positions, enters past swaps and changes a set's history; anyone else sees it.
 */
export function TyresPanel({ vehicle, canLog }: { vehicle: { id: string; units: { distance: DistanceUnit } }; canLog: boolean }) {
  const { t } = useTranslation()
  const unit = vehicle.units.distance
  const { data, error } = useQuery(VehicleTyresDocument, { variables: { vehicleId: vehicle.id }, fetchPolicy: 'cache-and-network' })
  const pending = usePendingTyres(vehicle.id)
  if (error && !data?.vehicleTyres) return <ErrorMessage error={error} />
  if (!data) return <Loading />
  // Null for someone who may see the vehicle but not its logs: no tyres, as the Recurring tab shows no schedules then.
  const tyres: Tyres = data.vehicleTyres ?? noTyres(vehicle.id)

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
              <EntryDialog
                vehicleId={vehicle.id}
                unit={unit}
                tyres={tyres}
                trigger={
                  <Button variant="soft" startIcon={<NotebookPen size={16} aria-hidden />}>
                    {t('tyres.entries.add')}
                  </Button>
                }
              />
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
  const open = info.installations.find((s) => s.installation.until == null)
  const last = info.installations[0]?.installation
  const measuredFrom = info.installations.length > 0 ? info.installations[info.installations.length - 1].installation.from : null
  const nothingMeasured = !tyres.hasReadings || (info.installations.length > 0 && info.installations.every((s) => s.distance.measure === 'NOT_MEASURED'))

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
                date: format.date(open.installation.from),
                distance: pending ? t('tyres.updatedOnSync') : words.distance(open.distance.distance, open.distance.approximate),
                pairs: words.pairs(open.installation.pairOneFront),
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

/**
 * The tyre log (#288): its lines, newest day first, each what was done (a chip), the set and the day, then the reading and the note. A swap's
 * line without a typed reading shows the day's odometer worked out from the fuel log (≈). Whoever may log for the vehicle opens a line to
 * change it and has its bin beside it.
 */
function TyreLog({ tyres, vehicleId, unit, canLog }: { tyres: Tyres; vehicleId: string; unit: DistanceUnit; canLog: boolean }) {
  const { t } = useTranslation()
  const format = useFormat()
  const words = useTyreWords(unit)
  const entryWords = useEntryWords()
  const [error, setError] = useState<unknown>()
  const sorted = sortedReadings(tyres.readings)

  return (
    <Stack component="section" aria-labelledby="tyre-log-title" sx={{ gap: 1, minWidth: 0 }}>
      <Typography id="tyre-log-title" component="h2" variant="overline" sx={{ color: 'text.secondary', lineHeight: 1.6 }}>
        {t('tyres.log')}
      </Typography>
      {error !== undefined && <ErrorMessage error={error} />}
      {tyres.entries.length === 0 ? (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t('tyres.empty.log')}
        </Typography>
      ) : (
        <Box component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
          {tyres.entries.map((entry) => {
            const set = entryWords.setName(tyres, entry.setId)
            // A swap's line without a typed reading says the day's odometer worked out from the fuel log, or that the day is before it.
            const worked = entry.odometer == null && isSwapLine(entry)
            const on = worked ? odometerOn(sorted, entry.day) : null
            const odometer = entry.odometer != null ? words.distance(entry.odometer) : on ? words.distance(on.value, on.approximate) : worked ? t('tyres.beforeLog') : null
            const body = (
              <Box sx={{ flex: 1, minWidth: 0, textAlign: 'start' }}>
                <Stack direction="row" sx={{ gap: 1, alignItems: 'center', flexWrap: 'wrap' }}>
                  <Typography component="span" variant="body2" sx={{ fontWeight: 700, fontVariantNumeric: 'tabular-nums' }}>
                    {format.date(entry.day)}
                  </Typography>
                  <Chip size="small" variant="soft" label={entryWords.kind(entry.kind)} />
                  {set && (
                    <Typography component="span" variant="body2" sx={{ overflowWrap: 'anywhere' }}>
                      {set}
                    </Typography>
                  )}
                </Stack>
                {(odometer || entry.note) && (
                  <Typography component="span" variant="body2" sx={{ display: 'block', color: 'text.secondary', overflowWrap: 'anywhere' }}>
                    {[odometer, entry.note].filter(Boolean).join(' · ')}
                  </Typography>
                )}
              </Box>
            )
            return (
              <Box component="li" key={entry.id} sx={{ display: 'flex', gap: 1, alignItems: 'center', py: 0.5, borderTop: 1, borderColor: 'divider', '&:first-of-type': { borderTop: 0 } }}>
                {canLog ? (
                  <EntryDialog
                    vehicleId={vehicleId}
                    unit={unit}
                    tyres={tyres}
                    entry={entry}
                    trigger={
                      <ButtonBase
                        aria-label={entryWords.name(tyres, entry)}
                        sx={{ flex: 1, minWidth: 0, minHeight: 44, justifyContent: 'flex-start', borderRadius: 2, px: 1, py: 0.75, '&:hover': { bgcolor: 'action.hover' } }}
                      >
                        {body}
                      </ButtonBase>
                    }
                  />
                ) : (
                  <Box sx={{ flex: 1, minWidth: 0, px: 1, py: 0.75 }}>{body}</Box>
                )}
                {canLog && <TrashEntryDialog vehicleId={vehicleId} unit={unit} tyres={tyres} entry={entry} onError={setError} />}
              </Box>
            )
          })}
        </Box>
      )}
    </Stack>
  )
}
