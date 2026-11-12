import Button from '@mui/material/Button'
import Checkbox from '@mui/material/Checkbox'
import FormControlLabel from '@mui/material/FormControlLabel'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { OfflineNote } from '../components/OfflineNote.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../dialogs/DialogTrigger.tsx'
import { useDialogState } from '../dialogs/useDialogState.ts'
import { todayIso } from '../forms/dates.ts'
import { Field } from '../forms/Field.tsx'
import { FieldDate } from '../forms/FieldDate.tsx'
import { Form } from '../forms/Form.tsx'
import { AddTyrePlaceDocument, UpdateTyrePlaceDocument, type TyreCorner } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { uuidV4 } from '../offline/uuid.ts'
import { ALL_CORNERS, liveTyresOf, useCornerWords, type TyreInfo } from './singleTyres.ts'
import { type Tyres, useTyreMutation, useTyreToasts } from './tyres.ts'

type Place = TyreInfo['places'][number]['place']

/**
 * *Add a time on the car* / *Edit time on the car* of a single tyre (#289): its first day, the day it came off (empty while on) and its
 * corner, inside one of its set's installations (a tyre is on the car only while its set is). *Add the same for* / *Change the same for*
 * the set's other tyres, unticked: each keeps its own corner (an added one the corner it had before; a changed one is its time on the car
 * with the same days as this one's).
 */
export function PlaceDialog({
  trigger, vehicleId, tyres, tyreId, place,
}: {
  trigger: ReactNode
  vehicleId: string
  tyres: Tyres
  tyreId: string
  /** The time on the car to change; none adds one. */
  place?: Place
}) {
  const { t } = useTranslation()
  const corners = useCornerWords()
  const notify = useTyreToasts()
  const run = useTyreMutation(vehicleId)
  const [open, setOpen] = useDialogState()
  const info = tyres.sets.find((s) => (s.tyres ?? []).some((x) => x.tyre.id === tyreId))
  const tyre = info?.tyres.find((x) => x.tyre.id === tyreId)
  const [from, setFrom] = useState(place?.from ?? todayIso())
  const [until, setUntil] = useState(place?.until ?? '')
  const [corner, setCorner] = useState<TyreCorner | ''>((place ? place.corner : tyre?.corner) ?? '')
  const [also, setAlso] = useState<Set<string>>(new Set())
  const [ids, setIds] = useState<Record<string, string>>({})
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  if (!info || !tyre) return null
  const others = liveTyresOf(info).filter((x) => x.tyre.id !== tyreId)
  /** A changed time on the car's partner on another tyre: the same days in the same installation. */
  const partner = (other: TyreInfo) =>
    place ? other.places.map((p) => p.place).find((p) => p.installationId === place.installationId && p.from === place.from && (p.until ?? null) === (place.until ?? null)) : undefined
  const offered = place ? others.filter((o) => partner(o)) : others

  const reset = () => {
    setFrom(place?.from ?? todayIso())
    setUntil(place?.until ?? '')
    setCorner((place ? place.corner : tyre.corner) ?? '') // an edit starts from the place's own corner, never the tyre's now
    setAlso(new Set())
    setIds(Object.fromEntries([tyreId, ...others.map((o) => o.tyre.id)].map((id) => [id, uuidV4()]))) // one per opening: a Save sent again adds nothing twice
    setError(undefined)
  }
  /** The corner another tyre had the last time before a day (an added time on the car keeps each tyre's own). */
  const cornerBefore = (other: TyreInfo, day: string) => other.places.map((p) => p.place).filter((p) => p.from < day).sort((a, b) => (a.from < b.from ? 1 : -1))[0]?.corner ?? null

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    setBusy(true)
    setError(undefined)
    try {
      const days = { from, until: until || null }
      let saved
      if (place) {
        saved = await run(UpdateTyrePlaceDocument, { input: { id: place.id, ...days, corner: corner || null } }, { tyreId, installationId: place.installationId })
        for (const other of offered.filter((o) => also.has(o.tyre.id))) {
          const theirs = partner(other)!
          saved = await run(UpdateTyrePlaceDocument, { input: { id: theirs.id, ...days, corner: theirs.corner ?? null } }, { tyreId: other.tyre.id, installationId: theirs.installationId })
        }
      } else {
        saved = await run(AddTyrePlaceDocument, { input: { tyreId, ...days, corner: corner || null, id: ids[tyreId] } })
        for (const other of offered.filter((o) => also.has(o.tyre.id)))
          saved = await run(AddTyrePlaceDocument, { input: { tyreId: other.tyre.id, ...days, corner: cornerBefore(other, from), id: ids[other.tyre.id] } })
      }
      setOpen(false)
      notify.saved(saved, t('toast.saved'))
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  const title = place ? t('tyres.single.editPlace') : t('tyres.single.addPlace')
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
      <DialogFrame open={open} onClose={() => setOpen(false)} busy={busy} maxWidth={480} title={title} description={t('tyres.single.tyreTitle', { label: tyre.tyre.label })}>
        {open && (
          <Form onSubmit={submit} refusal={error}>
            <OfflineNote />
            <Stack sx={{ gap: 1.5 }}>
              <Field name="from" label={t('tyres.single.from')} required>
                <FieldDate disableFuture value={from} onChange={setFrom} />
              </Field>
              <Field name="until" label={t('tyres.single.until')} hint={t('tyres.single.untilHint')}>
                <FieldDate disableFuture value={until} onChange={setUntil} />
              </Field>
              <LabeledSelect
                label={t('tyres.single.corner')}
                value={corner}
                options={[...ALL_CORNERS.map((c) => ({ value: c as TyreCorner | '', label: corners.of(c) })), { value: '', label: corners.of(null) }]}
                onChange={setCorner}
              />
              {offered.length > 0 && (
                <Stack component="fieldset" sx={{ border: 0, p: 0, m: 0, gap: 0.5 }}>
                  <Typography component="legend" variant="body2" sx={{ fontWeight: 600 }}>
                    {place ? t('tyres.single.changeSameFor') : t('tyres.single.addSameFor')}
                  </Typography>
                  <Stack direction="row" sx={{ flexWrap: 'wrap', gap: 1 }}>
                    {offered.map((o) => (
                      <FormControlLabel
                        key={o.tyre.id}
                        label={o.tyre.label}
                        control={
                          <Checkbox
                            checked={also.has(o.tyre.id)}
                            onChange={(event) => {
                              const next = new Set(also)
                              if (event.target.checked) next.add(o.tyre.id)
                              else next.delete(o.tyre.id)
                              setAlso(next)
                            }}
                          />
                        }
                      />
                    ))}
                  </Stack>
                  <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                    {t('tyres.single.sameForHint')}
                  </Typography>
                </Stack>
              )}
              <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                {t('tyres.single.onlyWhileSet')}
              </Typography>
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
