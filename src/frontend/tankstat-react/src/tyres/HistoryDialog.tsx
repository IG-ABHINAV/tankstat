import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Pencil, Plus, Trash2 } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { BreakUpDialog } from './BreakUpDialog.tsx'
import { ConfirmDialog } from '../components/ConfirmDialog.tsx'
import { IconAction } from '../components/IconAction.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../dialogs/DialogTrigger.tsx'
import { useClientId } from '../dialogs/useClientId.ts'
import { useDialogState } from '../dialogs/useDialogState.ts'
import { Field } from '../forms/Field.tsx'
import { FieldDate } from '../forms/FieldDate.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import { Form } from '../forms/Form.tsx'
import {
  AddTyreInstallationDocument,
  DeleteTyreSetDocument,
  RestoreTyreSetDocument,
  SetTyreRoughDistanceDocument,
  UpdateTyreInstallationDocument,
  type DistanceUnit,
} from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { ErrorMessage } from '../messages.tsx'
import { TyreSetDialog } from './TyreSetDialog.tsx'
import { TrashEntryDialog } from './EntryDialog.tsx'
import { OfflineNote } from '../components/OfflineNote.tsx'
import { todayIso } from '../forms/dates.ts'
import { type Tyres, type TyreSetInfo, type TyreInstallationInfo, useTyreMutation, useTyreToasts, useTyreWords, wholeInvalid, wholeOrNull } from './tyres.ts'

/**
 * A set's history (#287): its figures (the distance and how it is made up, each pair's front and rear share) and every installation on the car
 * with its distance; whoever may log changes, adds or moves an installation to the trash (its day's reading stays: one of the car's readings),
 * changes the rough figure, or moves the set to the trash. Every change is checked by the server; a refusal says why.
 */
export function HistoryDialog({
  trigger,
  vehicleId,
  unit,
  tyres,
  setId,
  canLog,
}: {
  trigger: ReactNode
  vehicleId: string
  unit: DistanceUnit
  tyres: Tyres
  setId: string
  canLog: boolean
}) {
  const { t } = useTranslation()
  const format = useFormat()
  const words = useTyreWords(unit)
  const installationDistance = useInstallationDistance(unit)
  const notify = useTyreToasts()
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState()
  const [error, setError] = useState<unknown>()
  const info = tyres.sets.find((s) => s.set.id === setId)
  if (!info) return <DialogTrigger trigger={trigger} open={false} onOpen={() => undefined} />
  const { set, figures } = info
  const measuredFrom = info.installations.length > 0 ? info.installations[info.installations.length - 1].installation.from : null

  /** The installation's line in the tyre log (#288): its bin is the line's, which takes the installation along. */
  const lineOf = (installationId: string) => tyres.entries.find((e) => e.id === installationId)

  return (
    <>
      <DialogTrigger trigger={trigger} open={open} onOpen={() => setOpen(true)} />
      <DialogFrame open={open} onClose={() => setOpen(false)} onClosed={() => setError(undefined)} maxWidth={560} title={set.name} description={t('tyres.dialogs.historyDescription')}>
        {open && (
          <Stack sx={{ gap: 2 }}>
            <Box component="dl" sx={{ m: 0, display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '6px 16px' }}>
              <Typography component="dt" variant="body2" sx={{ color: 'text.secondary' }}>
                {t('tyres.distance')}
              </Typography>
              <Box component="dd" sx={{ m: 0 }}>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>
                  {words.distance(figures.distance, figures.approximate)}
                </Typography>
                {set.roughDistance > 0 && (
                  <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                    {t('tyres.distanceMadeUp', { measured: words.distance(figures.measured), rough: words.distance(set.roughDistance) })}
                  </Typography>
                )}
                {canLog && <RoughDialog vehicleId={vehicleId} unit={unit} info={info} measuredFrom={measuredFrom} trigger={<Button variant="text" size="small">{t('tyres.changeRough')}</Button>} />}
              </Box>
              {figures.pairOneFront + figures.pairOneRear > 0 && (
                <>
                  <Typography component="dt" variant="body2" sx={{ color: 'text.secondary' }}>
                    {t('tyres.fields.pairs')}
                  </Typography>
                  <Box component="dd" sx={{ m: 0 }}>
                    <Typography variant="body2">{t('tyres.frontRear', { pair: 1, front: words.distance(figures.pairOneFront), rear: words.distance(figures.pairOneRear) })}</Typography>
                    <Typography variant="body2">{t('tyres.frontRear', { pair: 2, front: words.distance(figures.pairOneRear), rear: words.distance(figures.pairOneFront) })}</Typography>
                    <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                      {t('tyres.pairOneIs')}
                    </Typography>
                  </Box>
                </>
              )}
              <Typography component="dt" variant="body2" sx={{ color: 'text.secondary' }}>
                {t('tyres.age')}
              </Typography>
              <Typography component="dd" variant="body2" sx={{ m: 0 }}>
                {words.age(set)}
              </Typography>
            </Box>

            {set.brokenUpOn ? (
              <Typography variant="body2" sx={{ p: 1, borderRadius: 2, bgcolor: 'action.hover' }}>
                {t('tyres.single.historyNote', { date: format.date(set.brokenUpOn) })}
              </Typography>
            ) : (
              canLog && !set.isRetired && (
                <Box>
                  <BreakUpDialog vehicleId={vehicleId} unit={unit} tyres={tyres} setId={set.id} trigger={<Button variant="soft">{t('tyres.single.trackOneByOne')}</Button>} />
                </Box>
              )
            )}
            <Box>
              <Typography component="h3" variant="subtitle2" sx={{ mb: 0.5 }}>
                {t('tyres.history')}
              </Typography>
              <Box component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
                {info.installations.map((s) => (
                  <Box component="li" key={s.installation.id} sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 1, borderTop: 1, borderColor: 'divider', '&:first-of-type': { borderTop: 0 } }}>
                    <Box sx={{ flex: 1, minWidth: 0 }}>
                      <Typography variant="body2" sx={{ fontWeight: 600 }}>
                        {t('tyres.dialogs.installationLine', { pairs: words.pairs(s.installation.pairOneFront) })}
                      </Typography>
                      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                        {t('tyres.dialogs.installationRange', {
                          from: format.date(s.installation.from),
                          until: s.installation.until ? format.date(s.installation.until) : t('tyres.dialogs.now'),
                          distance: installationDistance(s, format.date(tyres.readings[0]?.date ?? s.installation.from)),
                        })}
                      </Typography>
                    </Box>
                    {canLog && (
                      <>
                        <InstallationDialog
                          vehicleId={vehicleId}
                          unit={unit}
                          tyres={tyres}
                          setId={set.id}
                          installation={s.installation}
                          trigger={
                            <IconAction label={t('tyres.dialogs.edit')}>
                              <Pencil size={16} aria-hidden />
                            </IconAction>
                          }
                        />
                        {lineOf(s.installation.id) && (
                          <TrashEntryDialog vehicleId={vehicleId} unit={unit} tyres={tyres} entry={lineOf(s.installation.id)!} onError={setError} />
                        )}
                      </>
                    )}
                  </Box>
                ))}
              </Box>
            </Box>
            {error !== undefined && <ErrorMessage error={error} />}
            {canLog && (
              <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
                <TyreSetDialog vehicleId={vehicleId} unit={unit} edit={set} trigger={<Button variant="soft" startIcon={<Pencil size={16} aria-hidden />}>{t('tyres.dialogs.editSet')}</Button>} />
                <InstallationDialog vehicleId={vehicleId} unit={unit} tyres={tyres} setId={set.id} trigger={<Button variant="soft" startIcon={<Plus size={16} aria-hidden />}>{t('tyres.dialogs.installationAdd')}</Button>} />
                <ConfirmDialog
                  trigger={
                    <Button variant="soft" color="error" startIcon={<Trash2 size={16} aria-hidden />}>
                      {t('tyres.dialogs.trashSet')}
                    </Button>
                  }
                  title={t('tyres.dialogs.trashSetTitle')}
                  description={t('tyres.dialogs.trashSetText', { set: set.name })}
                  confirmLabel={t('tyres.dialogs.toTrash')}
                  onConfirm={() =>
                    void (async () => {
                      try {
                        const saved = await run(DeleteTyreSetDocument, { id: set.id })
                        setOpen(false)
                        notify.undoable(saved, t('tyres.toast.trashed'), () => run(RestoreTyreSetDocument, { id: set.id }))
                      } catch (err) {
                        setError(err)
                      }
                    })()
                  }
                />
              </Stack>
            )}
            <DialogButtons>
              <DialogCancel />
            </DialogButtons>
          </Stack>
        )}
      </DialogFrame>
    </>
  )
}

/** An installation's distance in words: measured (≈ when worked out), counted from the fuel log's start, or not measured. */
function useInstallationDistance(unit: DistanceUnit) {
  const { t } = useTranslation()
  const words = useTyreWords(unit)
  return (s: TyreInstallationInfo, logStart: string) =>
    s.distance.measure === 'NOT_MEASURED'
      ? t('tyres.notMeasured')
      : s.distance.measure === 'FROM_FIRST_READING'
        ? t('tyres.countedFrom', { distance: words.distance(s.distance.distance, true), date: logStart })
        : words.distance(s.distance.distance, s.distance.approximate)
}

type Pairs = 'none' | 'one' | 'two'
const pairsValue = (p: boolean | null | undefined): Pairs => (p === true ? 'one' : p === false ? 'two' : 'none')
const pairsOf = (v: Pairs) => (v === 'one' ? true : v === 'two' ? false : null)

/** Adds an installation to a set's history, or changes one: its days, its pairs, and the readings of its days (kept on the tyre log's lines). */
function InstallationDialog({
  trigger,
  vehicleId,
  unit,
  tyres,
  setId,
  installation,
}: {
  trigger: ReactNode
  vehicleId: string
  unit: DistanceUnit
  tyres: Tyres
  setId: string
  installation?: TyreInstallationInfo['installation']
}) {
  const { t } = useTranslation()
  const notify = useTyreToasts()
  const words = useTyreWords(unit)
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState()
  const id = useClientId(open)
  const [from, setFrom] = useState(installation?.from ?? '')
  const [until, setUntil] = useState(installation?.until ?? '')
  const [pairs, setPairs] = useState<Pairs>(pairsValue(installation?.pairOneFront))
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    const text = (name: string) => String(form.get(name) ?? '').trim()
    const values = {
      from,
      until: until || null,
      pairOneFront: pairsOf(pairs),
      odometerFrom: wholeOrNull(text('odometerFrom')),
      odometerUntil: until ? wholeOrNull(text('odometerUntil')) : null,
    }
    setBusy(true)
    setError(undefined)
    try {
      const saved = installation
        ? await run(UpdateTyreInstallationDocument, { input: { id: installation.id, ...values } })
        : await run(AddTyreInstallationDocument, { input: { id, setId, ...values } })
      setOpen(false)
      notify.saved(saved, t('toast.saved'))
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <DialogTrigger trigger={trigger} open={open} onOpen={() => setOpen(true)} />
      <DialogFrame
        open={open}
        onClose={() => setOpen(false)}
        onClosed={() => {
          setFrom(installation?.from ?? '')
          setUntil(installation?.until ?? '')
          setPairs(pairsValue(installation?.pairOneFront))
          setError(undefined)
        }}
        busy={busy}
        maxWidth={480}
        title={installation ? t('tyres.dialogs.installationEdit') : t('tyres.dialogs.installationAdd')}
        description={t('tyres.dialogs.installationDescription')}
      >
        {open && (
          <Form onSubmit={submit} refusal={error}>
            <OfflineNote />
            <Stack sx={{ gap: 1.5 }}>
              <Field name="from" label={t('tyres.fields.from')} required>
                <FieldDate disableFuture value={from} onChange={setFrom} />
              </Field>
              <Field name="until" label={t('tyres.fields.until')} hint={t('tyres.fields.untilHint')}>
                <FieldDate disableFuture value={until} onChange={setUntil} />
              </Field>
              <LabeledSelect
                label={t('tyres.fields.pairs')}
                value={pairs}
                options={[
                  { value: 'none', label: t('tyres.pairs.none') },
                  { value: 'one', label: t('tyres.pairs.one') },
                  { value: 'two', label: t('tyres.pairs.two') },
                ]}
                onChange={setPairs}
              />
              <Field name="odometerFrom" label={t('tyres.fields.odometerFrom', { unit: words.unitShort })} hint={from ? words.odometerText(tyres.readings, from) : undefined} invalid={wholeInvalid(t('forms.numberInvalid'))}>
                <FieldInput inputMode="numeric" autoComplete="off" />
              </Field>
              {until && (
                <Field name="odometerUntil" label={t('tyres.fields.odometerUntil', { unit: words.unitShort })} hint={words.odometerText(tyres.readings, until)} invalid={wholeInvalid(t('forms.numberInvalid'))}>
                  <FieldInput inputMode="numeric" autoComplete="off" />
                </Field>
              )}
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
 * The rough figure: what cannot be measured, the time before the first installation the fuel log measures. When installations now measure time it
 * stood for, the suggestion (keeping the total as it was) is filled in, and saving it moves the day it stands for; never changed by itself.
 */
export function RoughDialog({
  trigger,
  vehicleId,
  unit,
  info,
  measuredFrom,
}: {
  trigger: ReactNode
  vehicleId: string
  unit: DistanceUnit
  info: TyreSetInfo
  measuredFrom: string | null
}) {
  const { t } = useTranslation()
  const format = useFormat()
  const notify = useTyreToasts()
  const words = useTyreWords(unit)
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState()
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  const suggestion = info.figures.suggestion
  const since = suggestion?.until ?? measuredFrom

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    setBusy(true)
    setError(undefined)
    try {
      const saved = await run(SetTyreRoughDistanceDocument, {
        input: { id: info.set.id, distance: wholeOrNull(String(form.get('rough') ?? '')) ?? 0, until: suggestion?.until ?? (info.set.roughUntil ? null : (measuredFrom ?? todayIso())) }, // the device's day: kept, it is sent later
      })
      setOpen(false)
      notify.saved(saved, t('toast.saved'))
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <DialogTrigger trigger={trigger} open={open} onOpen={() => setOpen(true)} />
      <DialogFrame open={open} onClose={() => setOpen(false)} onClosed={() => setError(undefined)} busy={busy} maxWidth={460} title={t('tyres.dialogs.roughTitle')} description={info.set.name}>
        {open && (
          <Form onSubmit={submit} refusal={error}>
            <OfflineNote />
            <Stack sx={{ gap: 1.5 }}>
              <Typography variant="body2">
                {since ? t('tyres.dialogs.roughText', { date: format.date(since), measured: words.distance(info.figures.measured) }) : t('tyres.dialogs.roughTextAll')}
              </Typography>
              <Field
                name="rough"
                label={t('tyres.fields.roughBefore', { unit: words.unitShort })}
                hint={suggestion ? t('tyres.dialogs.roughSuggestion', { old: words.distance(info.set.roughDistance), value: words.distance(suggestion.distance) }) : undefined}
                invalid={wholeInvalid(t('forms.numberInvalid'))}
              >
                <FieldInput inputMode="numeric" autoComplete="off" defaultValue={String(suggestion?.distance ?? info.set.roughDistance)} />
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
