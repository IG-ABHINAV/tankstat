import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Segmented } from '../components/Segmented.tsx'
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
import { ChangeTyrePositionsDocument, SwapTyresDocument, type DistanceUnit, type TyreCorner } from '../gql/generated.ts'
import { ErrorMessage, WarningMessage } from '../messages.tsx'
import { useFormat } from '../i18n/format.ts'
import { OfflineNote } from '../components/OfflineNote.tsx'
import { feeOf } from './entries.ts'
import { FeeFields } from './FeeFields.tsx'
import { keyedOf, type Tyres, type TyreSetInfo, useTyreMutation, useTyreToasts, useTyreWords, wholeInvalid, wholeOrNull } from './tyres.ts'
import { CarPicture, TyreButton } from './CarPicture.tsx'
import { CROSS, EXACT, isExactCorner, liveTyresOf, useCornerWords, type ExactCorner } from './singleTyres.ts'
import ButtonBase from '@mui/material/ButtonBase'
import Chip from '@mui/material/Chip'

type CornerMode = 'same' | 'cross' | 'unsure' | 'own'
type CornerMap = Record<string, string | null>

type Mode = 'same' | 'cross' | 'unsure'

/** The pairs a set is at now (`rotate`) or was at the last time on the car: its newest installation's. */
const pairsOf = (info: TyreSetInfo | undefined, rotate: boolean) => {
  const newest = info?.installations[0]?.installation // newest first
  return rotate ? (info?.installations.find((s) => s.installation.until == null)?.installation.pairOneFront ?? null) : (newest?.pairOneFront ?? null)
}

/**
 * Puts a set on the car on a day, today or in the past (`swap`), or changes the pairs of the set on the car from a day (`rotate`). A whole
 * set has no labels, so the form follows its two pairs: *Same as last time*, *Front ↔ rear* or *Not sure*. The day's reading is optional:
 * its hint says the car's reading that day, the one worked out from the fuel log, or why there is none. A second swap on a day asks once
 * whether to replace the first (#287, the owner's choice).
 */
export function SwapDialog({
  trigger,
  vehicleId,
  unit,
  tyres,
  rotate = false,
  setId,
}: {
  trigger: ReactNode
  vehicleId: string
  unit: DistanceUnit
  tyres: Tyres
  rotate?: boolean
  /** The set going on (a swap) or on the car (a change of positions) to start with. */
  setId?: string
}) {
  const { t } = useTranslation()
  const format = useFormat()
  const notify = useTyreToasts()
  const words = useTyreWords(unit)
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState()
  const installationId = useClientId(open)
  const expenseId = useClientId(open)
  const choices = tyres.sets.filter((s) => !s.set.isRetired && (rotate ? s.onTheCar : !s.onTheCar))
  const [picked, setChosen] = useState(setId ?? choices[0]?.set.id ?? '')
  const chosen = choices.some((s) => s.set.id === picked) ? picked : (choices[0]?.set.id ?? '')
  const info = tyres.sets.find((s) => s.set.id === chosen)
  const base = pairsOf(info, rotate)
  const known = base !== null
  const [mode, setMode] = useState<Mode>(rotate && known ? 'cross' : 'same')
  const [day, setDay] = useState(todayIso())
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  const [replace, setReplace] = useState<{ date: string; set: string } | null>(null)
  // A set tracked tyre by tyre (#289): where each tyre goes, as the owner's decision 43 has it.
  const broken = !!info?.set.brokenUpOn
  const [cornerMode, setCornerMode] = useState<CornerMode>(rotate ? 'cross' : 'same')
  const [own, setOwn] = useState<CornerMap>({})
  const baseCorners: CornerMap = Object.fromEntries(liveTyresOf(info).map((x) => [x.tyre.id, x.corner ?? null]))
  const cornersNow: CornerMap =
    cornerMode === 'own' ? own
      : cornerMode === 'unsure' ? Object.fromEntries(Object.keys(baseCorners).map((id) => [id, null]))
      : cornerMode === 'cross' ? Object.fromEntries(Object.entries(baseCorners).map(([id, c]) => [id, isExactCorner(c) ? CROSS[c] : c]))
      : baseCorners

  // Without pairs recorded before, the two choices name the pairs themselves: "same" is pair 1 at the front.
  const pairsNow = mode === 'unsure' ? null : mode === 'cross' ? (known ? !base : false) : known ? base : true
  const options: { value: Mode; label: string }[] = known
    ? [...(rotate ? [] : [{ value: 'same' as const, label: t('tyres.fields.sameAsLast') }]), { value: 'cross', label: t('tyres.fields.frontRear') }]
    : [
        { value: 'same', label: t('tyres.fields.pairOneFront') },
        { value: 'cross', label: t('tyres.fields.pairTwoFront') },
      ]
  if (!rotate) options.push({ value: 'unsure', label: t('tyres.fields.notSure') })

  const reset = () => {
    setChosen(setId ?? choices[0]?.set.id ?? '')
    setDay(todayIso())
    setError(undefined)
    setReplace(null)
    setMode(rotate && pairsOf(tyres.sets.find((s) => s.set.id === (setId ?? choices[0]?.set.id)), rotate) !== null ? 'cross' : 'same')
    setCornerMode(rotate ? 'cross' : 'same')
    setOwn({})
  }

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    const text = (name: string) => String(form.get(name) ?? '').trim()
    setBusy(true)
    setError(undefined)
    try {
      const input = {
        vehicleId,
        setId: chosen,
        day: text('day'),
        pairOneFront: broken ? null : pairsNow,
        ...(broken ? { corners: Object.entries(cornersNow).map(([tyreId, corner]) => ({ tyreId, corner: (corner ?? null) as TyreCorner | null })) } : {}),
        replace: replace !== null,
        odometer: wholeOrNull(text('odometer')),
        note: text('note') || null,
        installationId,
        // What it cost (#288): on its line, an expense titled by what was done and the set.
        ...(text('feeAmount') !== ''
          ? { fee: feeOf(form, `${t(`tyres.entries.kinds.${rotate ? 'POSITIONS_CHANGED' : 'SWAP'}`)}: ${info?.set.name ?? ''}`, expenseId) }
          : {}),
      }
      const saved = rotate ? await run(ChangeTyrePositionsDocument, { input }) : await run(SwapTyresDocument, { input })
      setOpen(false)
      notify.saved(saved, rotate ? t('toast.saved') : t('tyres.toast.swapped', { set: info?.set.name ?? '', date: format.date(input.day) }))
    } catch (err) {
      const keyed = keyedOf(err)
      if (keyed?.key === 'tyres.swapExists' && replace === null) setReplace({ date: format.date(String(keyed.args.date)), set: String(keyed.args.set) })
      else setError(err)
    } finally {
      setBusy(false)
    }
  }

  const front = pairsNow === false ? 2 : 1
  return (
    <>
      <DialogTrigger
        trigger={trigger}
        open={open}
        onOpen={() => {
          reset() // the tyres may have changed since the dialog was mounted (a set fitted, pairs recorded, another day)
          setOpen(true)
        }}
      />
      <DialogFrame
        open={open}
        onClose={() => setOpen(false)}
        onClosed={reset}
        busy={busy}
        maxWidth={520}
        title={rotate ? t('tyres.dialogs.rotateTitle') : t('tyres.dialogs.swapTitle')}
        description={rotate ? t('tyres.dialogs.rotateDescription') : t('tyres.dialogs.swapDescription')}
      >
        {open && (
          <Form onSubmit={submit} refusal={error}>
            <OfflineNote />
            <Stack sx={{ gap: 1.5 }}>
              {!rotate && (
                <LabeledSelect
                  label={t('tyres.fields.goingOn')}
                  hint={t('tyres.fields.goingOnHint')}
                  value={chosen}
                  options={choices.map((s) => ({ value: s.set.id, label: `${t(`tyres.seasons.${s.set.season}`)}: ${s.set.name}` }))}
                  onChange={(value) => {
                    setChosen(value)
                    setMode('same')
                    setCornerMode('same')
                    setOwn({})
                    setReplace(null) // the replace note named the swap of the day as it was sent
                  }}
                />
              )}
              <Field name="day" label={t('tyres.fields.day')} hint={rotate ? undefined : t('tyres.fields.dayHint')} required>
                <FieldDate
                  disableFuture
                  value={day}
                  onChange={(next) => {
                    setDay(next)
                    setReplace(null) // asked for another day: that day's swap is asked about afresh
                  }}
                />
              </Field>
              <Field name="odometer" label={t('tyres.fields.odometer', { unit: words.unitShort })} hint={words.odometerText(tyres.readings, day)} invalid={wholeInvalid(t('forms.numberInvalid'))}>
                <FieldInput inputMode="numeric" autoComplete="off" />
              </Field>
              {broken && info ? (
                <WhereEachTyre
                  info={info}
                  rotate={rotate}
                  base={baseCorners}
                  corners={cornersNow}
                  mode={cornerMode}
                  onMode={(next) => {
                    if (next === 'own') setOwn(cornersNow)
                    setCornerMode(next)
                  }}
                  onExchange={(a, b) => {
                    setOwn({ ...cornersNow, [a]: cornersNow[b] ?? null, [b]: cornersNow[a] ?? null })
                    setCornerMode('own')
                  }}
                  onPlace={(id, corner) => {
                    setOwn({ ...cornersNow, [id]: corner })
                    setCornerMode('own')
                  }}
                />
              ) : (
              <Stack component="fieldset" sx={{ gap: 1, border: 0, p: 0, m: 0 }}>
                <Typography component="legend" variant="body2" sx={{ fontWeight: 600, mb: 0.5 }}>
                  {t('tyres.fields.positions')}
                </Typography>
                <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                  {t('tyres.fields.pairsHint')}{' '}
                  {known ? (rotate ? t('tyres.fields.now', { pairs: words.pairs(base) }) : t('tyres.fields.lastTime', { pairs: words.pairs(base) })) : t('tyres.fields.notRecorded')}
                </Typography>
                <Segmented label={t('tyres.fields.positions')} value={mode} options={options} onChange={setMode} />
                <PairPicture front={front} dim={mode === 'unsure'} />
              </Stack>
              )}
              <FeeFields vehicleId={vehicleId} tyres={tyres} label={rotate ? t('tyres.fee.paid') : t('tyres.fee.paidForSwap')} />
              <Field name="note" label={t('tyres.fields.note')}>
                <FieldInput maxLength={500} autoComplete="off" />
              </Field>
              {!rotate && (
                <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                  {t('tyres.fee.swapText')}
                </Typography>
              )}
              {replace && <WarningMessage>{t('tyres.dialogs.replaceNote', replace)}</WarningMessage>}
              {error !== undefined && <ErrorMessage error={error} />}
              <DialogButtons>
                <DialogCancel />
                <Button type="submit" disabled={busy || !chosen}>
                  {replace ? t('tyres.dialogs.replaceSwap') : rotate ? t('tyres.dialogs.savePositions') : t('tyres.dialogs.swapTitle')}
                </Button>
              </DialogButtons>
            </Stack>
          </Form>
        )}
      </DialogFrame>
    </>
  )
}

/** The two pairs as a picture of the car seen from above (only a picture: the choice above says the same in words). */
function PairPicture({ front, dim }: { front: 1 | 2; dim: boolean }) {
  const { t } = useTranslation()
  const pair = (n: number) => (
    <Box sx={{ width: 1, textAlign: 'center', py: 1, borderRadius: 2, border: 1, borderColor: 'divider', bgcolor: 'background.paper', fontWeight: 700 }}>
      {t('tyres.fields.pairLabel', { pair: n })}
    </Box>
  )
  return (
    <Stack
      aria-hidden
      sx={{ alignItems: 'center', gap: 0.75, p: 1.25, mx: 'auto', width: 1, maxWidth: 220, border: 2, borderColor: 'divider', borderRadius: '24px 24px 14px 14px', opacity: dim ? 0.45 : 1 }}
    >
      <Typography variant="caption" sx={{ textTransform: 'uppercase', letterSpacing: '0.1em', fontWeight: 700, color: 'text.secondary' }}>
        {t('tyres.fields.front')}
      </Typography>
      {pair(front)}
      {pair(front === 1 ? 2 : 1)}
      <Typography variant="caption" sx={{ textTransform: 'uppercase', letterSpacing: '0.1em', fontWeight: 700, color: 'text.secondary' }}>
        {t('tyres.fields.rear')}
      </Typography>
    </Stack>
  )
}

/**
 * *Where each tyre goes* (#289) for a set tracked tyre by tyre: the car picture with each tyre at its corner. The swap starts from *Same as
 * last time*, a change of positions from *Front ↔ rear, same side*; *Not sure* dims the picture (saved without corners); tapping two tyres
 * exchanges them (*Your own*), a status line saying what to tap next.
 */
function WhereEachTyre({
  info, rotate, base, corners, mode, onMode, onExchange, onPlace,
}: {
  info: TyreSetInfo
  rotate: boolean
  base: CornerMap
  corners: CornerMap
  mode: CornerMode
  onMode: (mode: CornerMode) => void
  onExchange: (a: string, b: string) => void
  /** A tyre without a corner put at an empty one (the owner's decision 50). */
  onPlace: (tyreId: string, corner: ExactCorner) => void
}) {
  const { t } = useTranslation()
  const words = useCornerWords()
  const [picked, setPicked] = useState<string | null>(null)
  const tyres = liveTyresOf(info)
  const labelOf = (id: string) => tyres.find((x) => x.tyre.id === id)?.tyre.label ?? ''
  const options: { value: CornerMode; label: string }[] = [
    ...(rotate ? [] : [{ value: 'same' as const, label: t('tyres.fields.sameAsLast') }]),
    { value: 'cross', label: t('tyres.single.frontRearSameSide') },
    ...(rotate ? [] : [{ value: 'unsure' as const, label: t('tyres.fields.notSure') }]),
    ...(mode === 'own' ? [{ value: 'own' as const, label: t('tyres.single.ownMode') }] : []),
  ]
  const listed = tyres.map((x) => t('tyres.single.cornerList', { label: x.tyre.label, corner: words.of(base[x.tyre.id]) })).join(', ')
  const cells = Object.fromEntries(
    tyres.filter((x) => isExactCorner(corners[x.tyre.id])).map((x) => {
      const corner = corners[x.tyre.id] as ExactCorner
      return [corner, (
        <TyreButton
          key={x.tyre.id}
          corner={corner}
          label={x.tyre.label}
          lines={[words.of(corner)]}
          name={`${words.title(corner)}: ${t('tyres.single.tyreTitle', { label: x.tyre.label })}`}
          pressed={picked === x.tyre.id}
          onClick={() => {
            if (mode === 'unsure') return
            if (picked === null) return setPicked(x.tyre.id)
            if (picked !== x.tyre.id) onExchange(picked, x.tyre.id)
            setPicked(null)
          }}
        />
      )]
    }),
  ) as Partial<Record<ExactCorner, ReactNode>>
  // A tyre without a corner (#289, decision 50): a chip under the picture; picked, an empty corner takes it.
  const loose = mode === 'unsure' ? [] : tyres.filter((x) => !isExactCorner(corners[x.tyre.id]))
  if (picked && mode !== 'unsure')
    for (const corner of EXACT)
      if (!cells[corner])
        cells[corner] = (
          <ButtonBase
            onClick={() => {
              onPlace(picked, corner)
              setPicked(null)
            }}
            aria-label={t('tyres.single.putAt', { label: labelOf(picked), corner: words.of(corner) })}
            sx={{ flex: 1, minHeight: 60, borderRadius: 2, border: 1, borderStyle: 'dashed', borderColor: 'primary.main', color: 'primary.main', p: 1, typography: 'caption', textAlign: 'center' }}
          >
            {t('tyres.single.putHere', { label: labelOf(picked) })}
          </ButtonBase>
        )
  const emptyCorner = EXACT.some((c) => !tyres.some((x) => corners[x.tyre.id] === c))
  return (
    <Stack component="fieldset" sx={{ gap: 1, border: 0, p: 0, m: 0 }}>
      <Typography component="legend" variant="body2" sx={{ fontWeight: 600, mb: 0.5 }}>
        {t('tyres.single.whereEach')}
      </Typography>
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {rotate ? t('tyres.single.nowCorners', { corners: listed }) : t('tyres.single.lastCorners', { corners: listed })}
      </Typography>
      <Segmented
        label={t('tyres.single.whereEach')}
        value={mode}
        options={options}
        onChange={(next) => {
          setPicked(null)
          onMode(next)
        }}
      />
      <CarPicture cells={cells} dim={mode === 'unsure'} />
      {loose.length > 0 && (
        <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap', alignItems: 'center' }}>
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {t('tyres.single.withoutCorner')}
          </Typography>
          {loose.map((x) => (
            <Chip
              key={x.tyre.id}
              label={x.tyre.label}
              variant={picked === x.tyre.id ? 'filled' : 'outlined'}
              color={picked === x.tyre.id ? 'primary' : 'default'}
              aria-label={t('tyres.single.chipName', { label: x.tyre.label })}
              aria-pressed={picked === x.tyre.id}
              onClick={() => setPicked(picked === x.tyre.id ? null : x.tyre.id)}
              sx={{ fontWeight: 700 }}
            />
          ))}
        </Stack>
      )}
      <Typography role="status" variant="body2" sx={{ color: 'text.secondary' }}>
        {mode === 'unsure'
          ? t('tyres.single.unsureNote')
          : picked
            ? emptyCorner ? t('tyres.single.pickedOrCorner', { label: labelOf(picked) }) : t('tyres.single.picked', { label: labelOf(picked) })
            : t('tyres.single.tapTwo')}
      </Typography>
    </Stack>
  )
}
