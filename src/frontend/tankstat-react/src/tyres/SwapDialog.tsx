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
import { ChangeTyrePositionsDocument, SwapTyresDocument, type DistanceUnit } from '../gql/generated.ts'
import { ErrorMessage, WarningMessage } from '../messages.tsx'
import { useToast } from '../toast/toastContext.ts'
import { useFormat } from '../i18n/format.ts'
import { keyedOf, useTyreMutation, useTyreWords, wholeInvalid, wholeOrNull, type Tyres, type TyreSetInfo } from './tyres.ts'

type Mode = 'same' | 'cross' | 'unsure'

/** The pairs a set is at now (`rotate`) or was at the last time on the car: its newest stretch's. */
const pairsOf = (info: TyreSetInfo | undefined, rotate: boolean) => {
  const newest = info?.stretches[0]?.stretch // newest first
  return rotate ? (info?.stretches.find((s) => s.stretch.until == null)?.stretch.pairOneFront ?? null) : (newest?.pairOneFront ?? null)
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
  const { toast } = useToast()
  const words = useTyreWords(unit)
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState()
  const stretchId = useClientId(open)
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
        pairOneFront: pairsNow,
        replace: replace !== null,
        odometer: wholeOrNull(text('odometer')),
        note: text('note') || null,
        stretchId,
      }
      if (rotate) await run(ChangeTyrePositionsDocument, { input })
      else await run(SwapTyresDocument, { input })
      setOpen(false)
      toast(rotate ? t('toast.saved') : t('tyres.toast.swapped', { set: info?.set.name ?? '', date: format.date(input.day) }))
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
              <Field name="note" label={t('tyres.fields.note')}>
                <FieldInput maxLength={500} autoComplete="off" />
              </Field>
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
