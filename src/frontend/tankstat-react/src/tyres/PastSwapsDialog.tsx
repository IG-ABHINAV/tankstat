import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Plus, X } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { IconAction } from '../components/IconAction.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../dialogs/DialogTrigger.tsx'
import { useDialogState } from '../dialogs/useDialogState.ts'
import { Field } from '../forms/Field.tsx'
import { FieldDate } from '../forms/FieldDate.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import { Form } from '../forms/Form.tsx'
import { AddPastTyreSwapsDocument, type DistanceUnit } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { ErrorMessage, WarningMessage } from '../messages.tsx'
import { uuidV4 } from '../offline/uuid.ts'
import { OfflineNote } from '../components/OfflineNote.tsx'
import { keyedOf, type Tyres, useTyreMutation, useTyreToasts, useTyreWords, wholeInvalid, wholeOrNull } from './tyres.ts'

/** The server's limit (`TyreService.MaxPastSwaps`): one change of a device's queue must fit the sync's ledger. */
export const MAX_PAST_SWAPS = 25

interface Row {
  /** The id of the installation this row creates: the same for every Save of one opening. */
  key: string
  day: string
  setId: string
}

/**
 * Swaps from papers (#287): one row per swap, the day and the set that went on, a reading only where the papers have one (left empty, the
 * distance is worked out from the fuel log and marked ≈). All are saved at once, in day order; a day that has a swap asks once whether to
 * replace it.
 */
export function PastSwapsDialog({ trigger, vehicleId, unit, tyres }: { trigger: ReactNode; vehicleId: string; unit: DistanceUnit; tyres: Tyres }) {
  const { t } = useTranslation()
  const format = useFormat()
  const notify = useTyreToasts()
  const words = useTyreWords(unit)
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState()
  const sets = tyres.sets.filter((s) => !s.set.isRetired)
  const fresh = (): Row => ({ key: uuidV4(), day: '', setId: sets[0]?.set.id ?? '' })
  const [rows, setRows] = useState<Row[]>(() => [fresh()])
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  const [replace, setReplace] = useState<string | null>(null)
  // A change of the rows asks about the days that have a swap afresh: the replace note named the rows as they were sent.
  const edit = (next: (all: Row[]) => Row[]) => {
    setRows(next)
    setReplace(null)
  }
  const change = (key: string, values: Partial<Row>) => edit((all) => all.map((r) => (r.key === key ? { ...r, ...values } : r)))
  const reset = () => {
    setRows([fresh()])
    setError(undefined)
    setReplace(null)
  }

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    setBusy(true)
    setError(undefined)
    try {
      const saved = await run(AddPastTyreSwapsDocument, {
        input: {
          vehicleId,
          replace: replace !== null,
          swaps: rows.map((r) => ({ day: r.day, setId: r.setId, odometer: wholeOrNull(String(form.get(`odometer-${r.key}`) ?? '')), installationId: r.key })),
        },
      })
      setOpen(false)
      notify.saved(saved, t('tyres.toast.pastSaved', { count: rows.length }))
    } catch (err) {
      const keyed = keyedOf(err)
      if (keyed?.key === 'tyres.swapExists' && replace === null) {
        // Every row on a day another set went on is replaced, not only the one the server named.
        const starts = new Set(tyres.sets.flatMap((s) => s.installations.map((x) => x.installation.from)))
        const clashing = [...new Set(rows.map((r) => r.day).filter((d) => starts.has(d)))].sort()
        setReplace((clashing.length > 0 ? clashing : [String(keyed.args.date)]).map(format.date).join(', '))
      }
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
          reset() // the sets may have changed since the dialog was mounted
          setOpen(true)
        }}
      />
      <DialogFrame
        open={open}
        onClose={() => setOpen(false)}
        onClosed={reset}
        busy={busy}
        maxWidth={620}
        title={t('tyres.dialogs.pastTitle')}
        description={t('tyres.dialogs.pastDescription')}
      >
        {open && (
          <Form onSubmit={submit} refusal={error}>
            <OfflineNote />
            <Stack sx={{ gap: 1.5 }}>
              {rows.map((row, i) => (
                <Box
                  key={row.key}
                  component="fieldset"
                  sx={{ border: 0, m: 0, p: 0, display: 'grid', gap: 1, alignItems: 'end', gridTemplateColumns: { xs: '1fr 1fr auto', sm: '1.1fr 1.4fr 1fr auto' } }}
                >
                  <Typography component="legend" variant="body2" sx={{ fontWeight: 600, gridColumn: '1 / -1', mb: 0.5 }}>
                    {t('tyres.dialogs.rowLabel', { row: i + 1 })}
                  </Typography>
                  <Box sx={{ gridColumn: { xs: '1 / span 2', sm: 'auto' } }}>
                    <Field name={`day-${row.key}`} label={t('tyres.fields.day')} required>
                      <FieldDate disableFuture value={row.day} onChange={(day) => change(row.key, { day })} />
                    </Field>
                  </Box>
                  <Box sx={{ gridColumn: { xs: '1 / span 2', sm: 'auto' }, gridRow: { xs: 3, sm: 'auto' } }}>
                    <LabeledSelect
                      label={t('tyres.fields.goesOn')}
                      value={row.setId}
                      options={sets.map((s) => ({ value: s.set.id, label: `${t(`tyres.seasons.${s.set.season}`)}: ${s.set.name}` }))}
                      onChange={(setId) => change(row.key, { setId })}
                    />
                  </Box>
                  <Box sx={{ gridColumn: { xs: '1 / span 2', sm: 'auto' }, gridRow: { xs: 4, sm: 'auto' } }}>
                    <Field
                      name={`odometer-${row.key}`}
                      label={t('tyres.fields.odometerPlain', { unit: words.unitShort })}
                      hint={row.day ? words.odometerText(tyres.readings, row.day) : undefined}
                      invalid={wholeInvalid(t('forms.numberInvalid'))}
                    >
                      <FieldInput inputMode="numeric" autoComplete="off" />
                    </Field>
                  </Box>
                  <Box sx={{ gridColumn: { xs: 3, sm: 'auto' }, gridRow: { xs: 2, sm: 'auto' }, pb: { sm: 0.5 } }}>
                    {rows.length > 1 && <IconAction label={t('tyres.dialogs.removeRow', { row: i + 1 })} onClick={() => edit((all) => all.filter((r) => r.key !== row.key))}><X size={16} aria-hidden /></IconAction>}
                  </Box>
                </Box>
              ))}
              <Box>
                <Button variant="soft" startIcon={<Plus size={16} aria-hidden />} disabled={rows.length >= MAX_PAST_SWAPS} onClick={() => edit((all) => [...all, { ...fresh(), setId: all[all.length - 1]?.setId ?? '' }])}>
                  {t('tyres.dialogs.addRow')}
                </Button>
              </Box>
              <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                {t('tyres.dialogs.pastHint', { date: tyres.readings[0] ? format.date(tyres.readings[0].date) : '' })}
              </Typography>
              {replace && <WarningMessage>{t('tyres.dialogs.replaceDays', { days: replace })}</WarningMessage>}
              {error !== undefined && <ErrorMessage error={error} />}
              <DialogButtons>
                <DialogCancel />
                <Button type="submit" disabled={busy || sets.length === 0}>
                  {replace ? t('tyres.dialogs.replaceSwap') : t('tyres.dialogs.saveSwaps')}
                </Button>
              </DialogButtons>
            </Stack>
          </Form>
        )}
      </DialogFrame>
    </>
  )
}
