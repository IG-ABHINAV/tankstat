import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
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
import { ReplaceTyresDocument, type DistanceUnit, type TyreRetiredWhy } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { feeOf, useEntryWords } from './entries.ts'
import { FeeFields } from './FeeFields.tsx'
import { ForTyres } from './ForTyres.tsx'
import { parseMade } from './odometer.ts'
import { EXACT, liveTyresOf, suggestLabels } from './singleTyres.ts'
import { TyreRuleError } from './tyreHistory.ts'
import { type Tyres, useTyreMutation, useTyreToasts, useTyreWords, wholeInvalid, wholeOrNull } from './tyres.ts'

const WHY: readonly TyreRetiredWhy[] = ['DAMAGED', 'WORN', 'TOO_OLD']

/** What is typed for one new tyre; empty fields take their starting values (the next label, the set's name). */
interface Typed {
  label?: string
  name?: string
  made?: string
}

/**
 * *Replace tyres* (#289 step 3), from a set's History, or *Replace this tyre* from a tyre's dialog with it picked: the tyres to replace on the
 * car picture (a whole set's labels edited per corner, the set broken up first on save), a new tyre for each (its label, the next one free;
 * its name, the set's at first; *Made*), why, the day, the reading, and a *Price*: one expense, a Bought line per new tyre sharing it.
 */
export function ReplaceDialog({
  trigger, vehicleId, unit, tyres, setId, tyreId,
}: {
  trigger: ReactNode
  vehicleId: string
  unit: DistanceUnit
  tyres: Tyres
  setId: string
  /** *Replace this tyre*: picked at first. */
  tyreId?: string
}) {
  const { t } = useTranslation()
  const words = useTyreWords(unit)
  const entryWords = useEntryWords()
  const notify = useTyreToasts()
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState()
  const expenseId = useClientId(open)
  const newIds = [useClientId(open), useClientId(open), useClientId(open), useClientId(open)]
  const lineIds = [useClientId(open), useClientId(open), useClientId(open), useClientId(open)]
  const breakIds = [useClientId(open), useClientId(open), useClientId(open), useClientId(open)]
  const info = tyres.sets.find((s) => s.set.id === setId)
  const [picked, setPicked] = useState<string[]>([])
  const [labels, setLabels] = useState<string[]>([])
  const [typed, setTyped] = useState<Record<string, Typed>>({})
  const [why, setWhy] = useState<TyreRetiredWhy>('DAMAGED')
  const [day, setDay] = useState(todayIso())
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  if (!info) return null
  const { set } = info
  const whole = !set.brokenUpOn

  const reset = () => {
    setPicked(tyreId ? [tyreId] : [])
    setLabels(whole ? suggestLabels(tyres, set.season) : [])
    setTyped({})
    setWhy('DAMAGED')
    setDay(todayIso())
    setError(undefined)
  }

  // The tyres picked, in the car's order, each with the label it has (a whole set's as typed for the break-up).
  const live = liveTyresOf(info)
  const chosen = whole
    ? EXACT.flatMap((corner, n) => (picked.includes(corner) ? [{ key: corner, old: (labels[n] ?? '').trim(), tyreId: breakIds[n] }] : []))
    : live.filter((x) => picked.includes(x.tyre.id)).map((x) => ({ key: x.tyre.id, old: x.tyre.label, tyreId: x.tyre.id }))
  // The next labels free: after a whole set's four, which its break-up takes.
  const nextLabels = whole ? suggestLabels(tyres, set.season, 4 + chosen.length).slice(4) : suggestLabels(tyres, set.season, chosen.length)
  const valueOf = (key: string, n: number) => ({
    label: typed[key]?.label ?? nextLabels[n] ?? '',
    name: typed[key]?.name ?? set.name,
    made: typed[key]?.made ?? '',
  })
  const type = (key: string, field: keyof Typed, value: string) => setTyped({ ...typed, [key]: { ...typed[key], [field]: value } })

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    setBusy(true)
    setError(undefined)
    try {
      if (chosen.length === 0) throw new TyreRuleError('tyres.tapToReplace')
      const odometer = wholeOrNull(String(form.get('odometer') ?? '').trim())
      const price = feeOf(form, entryWords.feeTitle('BOUGHT', set.name), expenseId)
      const replacing = chosen.map((c, n) => {
        const v = valueOf(c.key, n)
        const made = parseMade(v.made)
        return {
          tyreId: c.tyreId, id: newIds[n], lineId: lineIds[n], label: v.label.trim(), name: v.name.trim() === set.name ? null : v.name.trim() || null,
          madeWeek: made?.week ?? null, madeYear: made?.year ?? null,
        }
      })
      const breakUp = whole ? { setId, day, tyres: EXACT.map((corner, n) => ({ id: breakIds[n], label: (labels[n] ?? '').trim(), corner })) } : null
      const saved = await run(ReplaceTyresDocument, {
        input: { setId, day, why, odometer, tyres: replacing, ...(price.amount != null ? { price } : {}), ...(breakUp ? { breakUp } : {}) },
      })
      setOpen(false)
      const old = chosen.map((c) => c.old).join(', ')
      notify.saved(saved, t(chosen.length === 1 ? 'tyres.replace.toastOne' : 'tyres.replace.toastMany', { tyres: old }))
    } catch (err) {
      setError(err)
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
          reset()
          setOpen(true)
        }}
      />
      <DialogFrame open={open} onClose={() => setOpen(false)} busy={busy} maxWidth={600} title={t(tyreId ? 'tyres.replace.thisTyre' : 'tyres.replace.title')} description={set.name}>
        {open && (
          <Form onSubmit={submit} refusal={error}>
            <OfflineNote />
            <Stack sx={{ gap: 2 }}>
              <Typography variant="body2">{t('tyres.replace.intro')}</Typography>
              <ForTyres
                info={info}
                whole={false}
                onWhole={() => undefined}
                picked={picked}
                onPicked={setPicked}
                labels={labels}
                onLabels={setLabels}
                locked
                legend={t('tyres.replace.toReplace')}
                tap={t('tyres.replace.tap')}
                breakUpNote={t('tyres.replace.breakUpFirst')}
              />
              {chosen.length > 0 && (
                <Stack sx={{ gap: 1.5 }}>
                  <Typography component="h3" variant="subtitle2">
                    {t('tyres.replace.newTyres')}
                  </Typography>
                  {chosen.map((c, n) => {
                    const v = valueOf(c.key, n)
                    return (
                      <Stack key={c.key} component="fieldset" sx={{ gap: 1, border: 1, borderColor: 'divider', borderRadius: 2, p: 1.5, m: 0, minWidth: 0 }}>
                        <Typography component="legend" variant="body2" sx={{ fontWeight: 600, px: 0.5 }}>
                          {t('tyres.replace.newTyre', { label: v.label.trim() || '…', old: c.old })}
                        </Typography>
                        <Box sx={{ display: 'grid', gap: 1.5, gridTemplateColumns: { xs: 'minmax(0,1fr)', sm: 'minmax(0,1fr) minmax(0,2fr) minmax(0,1fr)' } }}>
                          <Field name={`newLabel${n}`} label={t('tyres.single.label')} required>
                            <FieldInput value={v.label} maxLength={6} autoComplete="off" onChange={(e) => type(c.key, 'label', e.target.value)} />
                          </Field>
                          <Field name={`newName${n}`} label={t('tyres.fields.name')}>
                            <FieldInput value={v.name} maxLength={80} autoComplete="off" onChange={(e) => type(c.key, 'name', e.target.value)} />
                          </Field>
                          <Field
                            name={`newMade${n}`}
                            label={t('tyres.fields.made')}
                            invalid={{ message: t('tyres.fields.madeInvalid'), test: (value) => value.trim() !== '' && parseMade(value) === null }}
                          >
                            <FieldInput value={v.made} inputMode="numeric" maxLength={4} autoComplete="off" placeholder="0523" onChange={(e) => type(c.key, 'made', e.target.value)} />
                          </Field>
                        </Box>
                      </Stack>
                    )
                  })}
                </Stack>
              )}
              <Box sx={{ display: 'grid', gap: 1.5, gridTemplateColumns: { xs: 'minmax(0,1fr)', md: 'repeat(2, minmax(0,1fr))' } }}>
                <LabeledSelect label={t('tyres.replace.why')} value={why} options={WHY.map((w) => ({ value: w, label: t(`tyres.single.why.${w}`) }))} onChange={setWhy} />
                <Field name="day" label={t('tyres.fields.day')} required>
                  <FieldDate disableFuture value={day} onChange={setDay} />
                </Field>
                <Field
                  name="odometer"
                  label={t('tyres.fields.odometer', { unit: words.unitShort })}
                  hint={[t('tyres.entries.odometerHint'), words.neighbourText(tyres.readings, day)].filter(Boolean).join(' ')}
                  invalid={wholeInvalid(t('forms.numberInvalid'))}
                >
                  <FieldInput inputMode="numeric" autoComplete="off" />
                </Field>
              </Box>
              <FeeFields vehicleId={vehicleId} tyres={tyres} label={t('tyres.fee.price')} shared={t('tyres.replace.priceHint')} />
              {error !== undefined && <ErrorMessage error={error} />}
              <DialogButtons>
                <DialogCancel />
                <Button type="submit" disabled={busy}>
                  {t('tyres.replace.button')}
                </Button>
              </DialogButtons>
            </Stack>
          </Form>
        )}
      </DialogFrame>
    </>
  )
}
