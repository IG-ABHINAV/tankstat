import Button from '@mui/material/Button'
import Checkbox from '@mui/material/Checkbox'
import FormControlLabel from '@mui/material/FormControlLabel'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Segmented } from '../components/Segmented.tsx'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../dialogs/DialogTrigger.tsx'
import { useClientId } from '../dialogs/useClientId.ts'
import { useDialogState } from '../dialogs/useDialogState.ts'
import { todayIso } from '../forms/dates.ts'
import { Field } from '../forms/Field.tsx'
import { FieldDate } from '../forms/FieldDate.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import { Form } from '../forms/Form.tsx'
import { AddTyreSetDocument, type DistanceUnit, type TyreSeason } from '../gql/generated.ts'
import { ErrorMessage, WarningMessage } from '../messages.tsx'
import { useToast } from '../toast/toastContext.ts'
import { parseMade } from './odometer.ts'
import { keyedOf, useTyreMutation, useTyreWords, wholeInvalid, wholeOrNull } from './tyres.ts'

const SEASONS: TyreSeason[] = ['SUMMER', 'WINTER', 'ALL_SEASON']

/**
 * Adds a tyre set (#287): on the car from today (a swap today) or in storage; a used one with "in use since" and a rough figure for what it
 * ran before (marked ≈), a new one with the day it was bought. A fitted set on a day that has a swap asks once whether to replace it.
 */
export function TyreSetDialog({ trigger, vehicleId, unit, fittedFirst }: { trigger: ReactNode; vehicleId: string; unit: DistanceUnit; fittedFirst: boolean }) {
  const { t } = useTranslation()
  const { toast } = useToast()
  const [open, setOpen] = useDialogState()
  const id = useClientId(open)
  const stretchId = useClientId(open)
  const run = useTyreMutation(vehicleId)
  const words = useTyreWords(unit)
  const [season, setSeason] = useState<TyreSeason>('SUMMER')
  const [where, setWhere] = useState<'car' | 'storage'>(fittedFirst ? 'car' : 'storage')
  const [isNew, setIsNew] = useState(false)
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  const [replace, setReplace] = useState<{ date: string; set: string } | null>(null)

  const reset = () => {
    setSeason('SUMMER')
    setWhere(fittedFirst ? 'car' : 'storage')
    setIsNew(false)
    setError(undefined)
    setReplace(null)
  }

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    const text = (name: string) => String(form.get(name) ?? '').trim()
    const made = parseMade(text('made'))
    setBusy(true)
    setError(undefined)
    try {
      await run(AddTyreSetDocument, {
        input: {
          id,
          stretchId: where === 'car' ? stretchId : null,
          vehicleId,
          name: text('name'),
          season,
          size: text('size') || null,
          since: isNew ? text('bought') : text('since'),
          madeWeek: made?.week ?? null,
          madeYear: made?.year ?? null,
          fitted: where === 'car',
          day: todayIso(),
          roughDistance: isNew ? 0 : (wholeOrNull(text('rough')) ?? 0),
          replace: replace !== null,
        },
      })
      setOpen(false)
      toast(t('toast.saved'))
    } catch (err) {
      const keyed = keyedOf(err)
      if (keyed?.key === 'tyres.swapExists' && replace === null) setReplace({ date: String(keyed.args.date), set: String(keyed.args.set) })
      else setError(err)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <DialogTrigger trigger={trigger} open={open} onOpen={() => setOpen(true)} />
      <DialogFrame open={open} onClose={() => setOpen(false)} onClosed={reset} busy={busy} maxWidth={520} title={t('tyres.dialogs.addTitle')} description={t('tyres.dialogs.addDescription')}>
        {open && (
          <Form onSubmit={submit} refusal={error}>
            <Stack sx={{ gap: 1.5 }}>
              <Field name="name" label={t('tyres.fields.name')} hint={t('tyres.fields.nameHint')} required>
                <FieldInput maxLength={80} autoComplete="off" />
              </Field>
              <Stack sx={{ gap: 0.5 }}>
                <Typography variant="body2" component="span" sx={{ fontWeight: 600 }} id="tyre-season">
                  {t('tyres.fields.season')}
                </Typography>
                <Segmented label={t('tyres.fields.season')} value={season} options={SEASONS.map((s) => ({ value: s, label: t(`tyres.seasons.${s}`) }))} onChange={setSeason} />
              </Stack>
              <Field name="size" label={t('tyres.fields.size')}>
                <FieldInput maxLength={40} autoComplete="off" placeholder="205/55 R16 91V" />
              </Field>
              <Stack sx={{ gap: 0.5 }}>
                <Typography variant="body2" component="span" sx={{ fontWeight: 600 }}>
                  {t('tyres.fields.where')}
                </Typography>
                <Segmented
                  label={t('tyres.fields.where')}
                  value={where}
                  options={[
                    { value: 'car', label: t('tyres.fields.onTheCar') },
                    { value: 'storage', label: t('tyres.fields.inStorage') },
                  ]}
                  onChange={setWhere}
                />
              </Stack>
              <FormControlLabel control={<Checkbox checked={isNew} onChange={(e) => setIsNew(e.target.checked)} />} label={t('tyres.fields.isNew')} />
              {isNew ? (
                <Field name="bought" label={t('tyres.fields.bought')} required>
                  <FieldDate disableFuture defaultValue={todayIso()} />
                </Field>
              ) : (
                <>
                  <Field name="since" label={t('tyres.fields.since')} hint={t('tyres.fields.sinceHint')} required>
                    <FieldDate disableFuture />
                  </Field>
                  <Field name="rough" label={t('tyres.fields.rough', { unit: words.unitShort })} hint={t('tyres.fields.roughHint')} invalid={wholeInvalid(t('forms.numberInvalid'))}>
                    <FieldInput inputMode="numeric" autoComplete="off" />
                  </Field>
                </>
              )}
              <Field
                name="made"
                label={t('tyres.fields.made')}
                hint={t('tyres.fields.madeHint')}
                invalid={{ message: t('tyres.fields.madeInvalid'), test: (v) => v.trim() !== '' && parseMade(v) === null }}
              >
                <FieldInput inputMode="numeric" maxLength={4} autoComplete="off" placeholder="0523" />
              </Field>
              <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                {t('tyres.fields.setNote')}
              </Typography>
              {replace && <WarningMessage>{t('tyres.dialogs.replaceNote', { date: replace.date, set: replace.set })}</WarningMessage>}
              {error !== undefined && <ErrorMessage error={error} />}
              <DialogButtons>
                <DialogCancel />
                <Button type="submit" disabled={busy}>
                  {replace ? t('tyres.dialogs.replaceSwap') : t('tyres.dialogs.saveSet')}
                </Button>
              </DialogButtons>
            </Stack>
          </Form>
        )}
      </DialogFrame>
    </>
  )
}
