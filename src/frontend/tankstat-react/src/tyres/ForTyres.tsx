import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Checkbox from '@mui/material/Checkbox'
import FormControlLabel from '@mui/material/FormControlLabel'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Field } from '../forms/Field.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import { CarPicture, TyreButton } from './CarPicture.tsx'
import { EXACT, isExactCorner, liveTyresOf, useCornerWords, type ExactCorner } from './singleTyres.ts'
import type { TyreSetInfo } from './tyres.ts'

/**
 * *For* in *Add entry* (#289): *The whole set* (ticked at first), or, unticked, the car picture to tap the tyres the entry is for. On a whole
 * set the four corners, each with a label to edit, since saving breaks the set up first (the owner's decision 19); on a broken-up set its
 * tyres at their corners (where they are now, else where they were last), a tyre without one a toggle under the picture. Picked, a corner
 * or a tyre is pressed; the picture is only a picture, every button names itself.
 */
export function ForTyres({
  info, whole, onWhole, picked, onPicked, labels, onLabels, locked = false, legend, tap, breakUpNote,
}: {
  info: TyreSetInfo
  whole: boolean
  onWhole: (whole: boolean) => void
  /** The tyres picked: their ids on a broken-up set, their corners on a whole one. */
  picked: readonly string[]
  onPicked: (picked: string[]) => void
  /** A whole set's labels, one per corner in `EXACT`'s order. */
  labels: readonly string[]
  onLabels: (labels: string[]) => void
  /** Opened for one tyre (its dialog's *Add entry*): *The whole set* is not offered. */
  locked?: boolean
  /** Its own words in another dialog (*Replace tyres*): the legend, what to tap, and the note a whole set's labels get. */
  legend?: string
  tap?: string
  breakUpNote?: string
}) {
  const { t } = useTranslation()
  const corners = useCornerWords()
  const toggle = (key: string) => onPicked(picked.includes(key) ? picked.filter((p) => p !== key) : [...picked, key])
  const brokenUp = !!info.set.brokenUpOn
  const where = t(info.onTheCar ? 'tyres.entries.whereNow' : 'tyres.entries.whereLast')

  let picture
  if (!brokenUp) {
    const cells = Object.fromEntries(
      EXACT.map((corner, n) => [corner, (
        <TyreButton
          key={corner}
          corner={corner}
          label={labels[n]?.trim() || corners.title(corner)}
          lines={[corners.title(corner)]}
          name={t('tyres.entries.tyreAtCorner', { corner: corners.of(corner) })}
          pressed={picked.includes(corner)}
          onClick={() => toggle(corner)}
        />
      )]),
    ) as Record<ExactCorner, ReactNode>
    picture = (
      <>
        <CarPicture cells={cells} />
        <Box sx={{ display: 'grid', gap: 1.5, gridTemplateColumns: 'repeat(2, minmax(0,1fr))' }}>
          {EXACT.map((corner, n) => (
            <Field key={corner} name={`forLabel${n}`} label={t('tyres.entries.labelAt', { corner: corners.of(corner) })} required>
              <FieldInput value={labels[n] ?? ''} maxLength={6} autoComplete="off" onChange={(e) => onLabels(labels.map((l, i) => (i === n ? e.target.value : l)))} />
            </Field>
          ))}
        </Box>
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {breakUpNote ?? t('tyres.entries.breakUpOnSave')}
        </Typography>
      </>
    )
  } else {
    // At their corners where those are exact and each its own; the others under the picture.
    const live = liveTyresOf(info)
    const seen = new Set<string>()
    const placed = live.filter((x) => {
      if (!isExactCorner(x.corner) || seen.has(x.corner)) return false
      seen.add(x.corner)
      return true
    })
    const loose = live.filter((x) => !placed.includes(x))
    const cells = Object.fromEntries(
      placed.map((x) => {
        const corner = x.corner as ExactCorner
        return [corner, (
          <TyreButton
            key={x.tyre.id}
            corner={corner}
            label={x.tyre.label}
            lines={[corners.title(corner)]}
            name={t('tyres.entries.tyreAt', { label: x.tyre.label, corner: corners.of(corner) })}
            pressed={picked.includes(x.tyre.id)}
            onClick={() => toggle(x.tyre.id)}
          />
        )]
      }),
    ) as Partial<Record<ExactCorner, ReactNode>>
    picture = (
      <>
        {placed.length > 0 && <CarPicture cells={cells} />}
        {loose.length > 0 && (
          <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
            {loose.map((x) => (
              <Button
                key={x.tyre.id}
                variant={picked.includes(x.tyre.id) ? 'contained' : 'soft'}
                aria-pressed={picked.includes(x.tyre.id)}
                aria-label={t('tyres.entries.tyreAt', { label: x.tyre.label, corner: corners.of(x.corner) })}
                onClick={() => toggle(x.tyre.id)}
              >
                {x.tyre.label}
              </Button>
            ))}
          </Stack>
        )}
      </>
    )
  }

  return (
    <Stack component="fieldset" sx={{ gap: 1.5, border: 0, p: 0, m: 0, minWidth: 0 }}>
      <Typography component="legend" variant="body2" sx={{ fontWeight: 600, p: 0, mb: 0.5 }}>
        {legend ?? t('tyres.entries.for')}
      </Typography>
      {!locked && (
        <FormControlLabel control={<Checkbox checked={whole} onChange={(e) => onWhole(e.target.checked)} />} label={t('tyres.entries.wholeSet')} />
      )}
      {whole ? (
        brokenUp && (
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {t('tyres.entries.setLine')}
          </Typography>
        )
      ) : (
        <>
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {where} {tap ?? `${t('tyres.entries.tapTyres')} ${t('tyres.entries.pickSeveral')}`}
          </Typography>
          {picture}
        </>
      )}
    </Stack>
  )
}
