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
import { FieldInput } from '../forms/FieldInput.tsx'
import { Form } from '../forms/Form.tsx'
import { BreakUpTyreSetDocument, type DistanceUnit } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { EXACT, suggestLabels, useCornerWords, type ExactCorner } from './singleTyres.ts'
import { type Tyres, useTyreMutation, useTyreToasts, useTyreWords } from './tyres.ts'

/**
 * *Track tyres one by one* (#289), from a whole set's History: a label and a corner per tyre (where it is now on the car, else where it
 * was the last time; the four corners or *Not sure*), what happens said beforehand, and the button. Never undone.
 */
export function BreakUpDialog({ trigger, vehicleId, unit, tyres, setId }: { trigger: ReactNode; vehicleId: string; unit: DistanceUnit; tyres: Tyres; setId: string }) {
  const { t } = useTranslation()
  const words = useTyreWords(unit)
  const corners = useCornerWords()
  const notify = useTyreToasts()
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState()
  const ids = [useClientId(open), useClientId(open), useClientId(open), useClientId(open)]
  const info = tyres.sets.find((s) => s.set.id === setId)
  const [labels, setLabels] = useState<string[]>([])
  const [where, setWhere] = useState<(ExactCorner | '')[]>([...EXACT])
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  if (!info) return null
  const { set, figures } = info

  const reset = () => {
    setLabels(suggestLabels(tyres, set.season))
    setWhere([...EXACT])
    setError(undefined)
  }

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    setBusy(true)
    setError(undefined)
    try {
      const given = labels.map((label, n) => ({ id: ids[n], label: label.trim(), corner: where[n] || null }))
      const saved = await run(BreakUpTyreSetDocument, { input: { setId, day: todayIso(), tyres: given } })
      setOpen(false)
      notify.saved(saved, t('tyres.single.breakUp.toast', { labels: given.map((g) => g.label).join(', ') }))
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  const whereLabel = info.onTheCar ? t('tyres.single.whereNow') : t('tyres.single.whereLast')
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
      <DialogFrame open={open} onClose={() => setOpen(false)} busy={busy} maxWidth={560} title={t('tyres.single.trackOneByOne')} description={set.name}>
        {open && (
          <Form onSubmit={submit} refusal={error}>
            <OfflineNote />
            <Stack sx={{ gap: 2 }}>
              <Typography variant="body2">{t('tyres.single.breakUp.intro')}</Typography>
              <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' }, gap: 1.5 }}>
                {labels.map((label, n) => (
                  <Stack key={n} component="fieldset" sx={{ gap: 1, border: 1, borderColor: 'divider', borderRadius: 2, p: 1.5, m: 0, minWidth: 0 }}>
                    <Typography component="legend" variant="body2" sx={{ fontWeight: 600, px: 0.5 }}>
                      {t('tyres.single.breakUp.tyreN', { n: n + 1 })}
                    </Typography>
                    <Field name={`label${n}`} label={t('tyres.single.label')} required>
                      <FieldInput value={label} maxLength={6} autoComplete="off" onChange={(e) => setLabels(labels.map((l, i) => (i === n ? e.target.value : l)))} />
                    </Field>
                    <LabeledSelect
                      label={whereLabel}
                      value={where[n]}
                      options={[...EXACT.map((c) => ({ value: c, label: corners.title(c) })), { value: '' as const, label: t('tyres.fields.notSure') }]}
                      onChange={(value) => setWhere(where.map((w, i) => (i === n ? value : w)))}
                    />
                  </Stack>
                ))}
              </Box>
              <Box component="ul" sx={{ m: 0, pl: 2.5, display: 'flex', flexDirection: 'column', gap: 0.5 }}>
                <Typography component="li" variant="body2">
                  {t('tyres.single.breakUp.history', { distance: words.distance(figures.distance, figures.approximate) })}
                </Typography>
                <Typography component="li" variant="body2">{t('tyres.single.breakUp.perTyre')}</Typography>
                <Typography component="li" variant="body2">{t('tyres.single.breakUp.swapAll')}</Typography>
                <Typography component="li" variant="body2">{t('tyres.single.breakUp.setLines')}</Typography>
                <Typography component="li" variant="body2" sx={{ fontWeight: 600 }}>
                  {t('tyres.single.breakUp.noUndo')}
                </Typography>
              </Box>
              {error !== undefined && <ErrorMessage error={error} />}
              <DialogButtons>
                <DialogCancel />
                <Button type="submit" disabled={busy}>
                  {t('tyres.single.trackButton')}
                </Button>
              </DialogButtons>
            </Stack>
          </Form>
        )}
      </DialogFrame>
    </>
  )
}
