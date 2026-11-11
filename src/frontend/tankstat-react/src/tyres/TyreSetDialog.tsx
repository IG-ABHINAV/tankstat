import { useApolloClient } from '@apollo/client/react'
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
import { AddTyreSetDocument, UpdateTyreSetDocument, VehicleTyresDocument, type DistanceUnit, type TyreSeason } from '../gql/generated.ts'
import { ErrorMessage, WarningMessage } from '../messages.tsx'
import { useFormat } from '../i18n/format.ts'
import { madeText, parseMade } from './odometer.ts'
import { OfflineNote } from '../components/OfflineNote.tsx'
import { feeOf } from './entries.ts'
import { FeeFields } from './FeeFields.tsx'
import { noTyres } from './replay.ts'
import { keyedOf, type TyreSet, useTyreMutation, useTyreToasts, useTyreWords, wholeInvalid, wholeOrNull } from './tyres.ts'

const SEASONS: TyreSeason[] = ['SUMMER', 'WINTER', 'ALL_SEASON']

/**
 * Adds a tyre set (#287): on the car from today (a swap today) or in storage; a used one with "in use since" and a rough figure for what it
 * ran before (marked ≈), a new one with the day it was bought. A fitted set on a day that has a swap asks once whether to replace it.
 * With `edit`, the set's own details are corrected (name, season, size, in use since, made): where it is and what it ran are its history's.
 */
export function TyreSetDialog({
  trigger,
  vehicleId,
  unit,
  fittedFirst = false,
  edit,
}: {
  trigger: ReactNode
  vehicleId: string
  unit: DistanceUnit
  fittedFirst?: boolean
  edit?: TyreSet
}) {
  const { t } = useTranslation()
  const notify = useTyreToasts()
  const format = useFormat()
  const [open, setOpen] = useDialogState()
  const id = useClientId(open)
  const installationId = useClientId(open)
  const expenseId = useClientId(open)
  const boughtEntryId = useClientId(open)
  const tyres = useApolloClient().readQuery({ query: VehicleTyresDocument, variables: { vehicleId } })?.vehicleTyres ?? noTyres(vehicleId)
  // A price (#288): a Bought line on the day it was bought; a used set asks for that day, starting from "in use since".
  const [priced, setPriced] = useState(false)
  const [since, setSince] = useState('')
  const [boughtOn, setBoughtOn] = useState<string | null>(null)
  const run = useTyreMutation(vehicleId)
  const words = useTyreWords(unit)
  const [season, setSeason] = useState<TyreSeason>(edit?.season ?? 'SUMMER')
  const [where, setWhere] = useState<'car' | 'storage'>(fittedFirst ? 'car' : 'storage')
  const [isNew, setIsNew] = useState(false)
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  const [replace, setReplace] = useState<{ date: string; set: string } | null>(null)

  const reset = () => {
    setSeason(edit?.season ?? 'SUMMER')
    setWhere(fittedFirst ? 'car' : 'storage')
    setIsNew(false)
    setPriced(false)
    setSince('')
    setBoughtOn(null)
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
      if (edit) {
        const saved = await run(UpdateTyreSetDocument, {
          input: { id: edit.id, name: text('name'), season, size: text('size') || null, since: text('since'), madeWeek: made?.week ?? null, madeYear: made?.year ?? null },
        })
        setOpen(false)
        notify.saved(saved, t('toast.saved'))
        return
      }
      const saved = await run(AddTyreSetDocument, {
        input: {
          id,
          installationId: where === 'car' ? installationId : null,
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
          ...(text('feeAmount') !== ''
            ? {
                price: feeOf(form, `${t('tyres.entries.kinds.BOUGHT')}: ${text('name')}`, expenseId),
                boughtOn: isNew ? text('bought') : text('boughtOn') || text('since'),
                boughtEntryId,
              }
            : {}),
        },
      })
      setOpen(false)
      notify.saved(saved, t('toast.saved'))
    } catch (err) {
      const keyed = keyedOf(err)
      if (keyed?.key === 'tyres.swapExists' && replace === null) setReplace({ date: format.date(String(keyed.args.date)), set: String(keyed.args.set) })
      else setError(err)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <DialogTrigger
        trigger={trigger}
        open={open}
        onOpen={() => {
          reset() // where the set goes follows whether a set is on the car now, not when the dialog was mounted
          setOpen(true)
        }}
      />
      <DialogFrame
        open={open}
        onClose={() => setOpen(false)}
        onClosed={reset}
        busy={busy}
        maxWidth={520}
        title={edit ? t('tyres.dialogs.editTitle') : t('tyres.dialogs.addTitle')}
        description={edit ? t('tyres.dialogs.editDescription') : t('tyres.dialogs.addDescription')}
      >
        {open && (
          <Form onSubmit={submit} refusal={error}>
            <OfflineNote />
            <Stack sx={{ gap: 1.5 }}>
              <Field name="name" label={t('tyres.fields.name')} hint={t('tyres.fields.nameHint')} required>
                <FieldInput maxLength={80} autoComplete="off" defaultValue={edit?.name} />
              </Field>
              <Stack sx={{ gap: 0.5 }}>
                <Typography variant="body2" component="span" sx={{ fontWeight: 600 }} id="tyre-season">
                  {t('tyres.fields.season')}
                </Typography>
                <Segmented label={t('tyres.fields.season')} value={season} options={SEASONS.map((s) => ({ value: s, label: t(`tyres.seasons.${s}`) }))} onChange={setSeason} />
              </Stack>
              <Field name="size" label={t('tyres.fields.size')}>
                <FieldInput maxLength={40} autoComplete="off" placeholder="205/55 R16 91V" defaultValue={edit?.size ?? undefined} />
              </Field>
              {edit ? (
                <Field name="since" label={t('tyres.fields.since')} hint={t('tyres.fields.sinceHint')} required>
                  <FieldDate disableFuture defaultValue={edit.since} />
                </Field>
              ) : (
                <>
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
                        <FieldDate disableFuture value={since} onChange={setSince} />
                      </Field>
                      <Field name="rough" label={t('tyres.fields.rough', { unit: words.unitShort })} hint={t('tyres.fields.roughHint')} invalid={wholeInvalid(t('forms.numberInvalid'))}>
                        <FieldInput inputMode="numeric" autoComplete="off" />
                      </Field>
                    </>
                  )}
                </>
              )}
              <Field
                name="made"
                label={t('tyres.fields.made')}
                hint={t('tyres.fields.madeHint')}
                invalid={{ message: t('tyres.fields.madeInvalid'), test: (v) => v.trim() !== '' && parseMade(v) === null }}
              >
                <FieldInput inputMode="numeric" maxLength={4} autoComplete="off" placeholder="0523" defaultValue={madeText(edit?.madeWeek, edit?.madeYear)} />
              </Field>
              {!edit && (
                <>
                  <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                    {t('tyres.fee.priceText')}
                  </Typography>
                  <FeeFields vehicleId={vehicleId} tyres={tyres} label={t('tyres.fee.price')} onAmount={(v) => setPriced(v.trim() !== '')} />
                  {priced && !isNew && (
                    <Field name="boughtOn" label={t('tyres.fee.boughtOn')} hint={t('tyres.fee.boughtOnHint')} required>
                      <FieldDate disableFuture value={boughtOn ?? since} onChange={setBoughtOn} />
                    </Field>
                  )}
                  <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                    {t('tyres.fields.setNote')}
                  </Typography>
                </>
              )}
              {replace && <WarningMessage>{t('tyres.dialogs.replaceNote', { date: replace.date, set: replace.set })}</WarningMessage>}
              {error !== undefined && <ErrorMessage error={error} />}
              <DialogButtons>
                <DialogCancel />
                <Button type="submit" disabled={busy}>
                  {replace ? t('tyres.dialogs.replaceSwap') : edit ? t('tyres.dialogs.saveChanges') : t('tyres.dialogs.saveSet')}
                </Button>
              </DialogButtons>
            </Stack>
          </Form>
        )}
      </DialogFrame>
    </>
  )
}
