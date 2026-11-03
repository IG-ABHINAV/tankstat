import { useMutation } from '@apollo/client/react'
import Button from '@mui/material/Button'
import Collapse from '@mui/material/Collapse'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { ChevronDown } from 'lucide-react'
import { useId, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { LabeledSwitch } from '../components/LabeledSwitch.tsx'
import { DialogButtons, DialogCancel } from '../dialogs/DialogFrame.tsx'
import { Field } from '../forms/Field.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import { Form } from '../forms/Form.tsx'
import { SaveChartDocument, type DistanceUnit, type VolumeUnit } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { ChartCard } from './ChartCard.tsx'
import { ChartFields } from './ChartFields.tsx'
import { isReady, NEW_RECIPE, normalise, toConfig, type ChartRecipe } from './chartFormat.ts'

export interface EditableChart {
  id: string
  title: string
  isShared: boolean
  recipe: ChartRecipe
}

const sameRecipe = (a: ChartRecipe, b: ChartRecipe) => JSON.stringify(toConfig(normalise(a))) === JSON.stringify(toConfig(normalise(b)))

/**
 * The form of a chart of one's own: its title, its recipe (`ChartFields`), whether it is shared, and the live preview on the vehicle's
 * data; saved with `SaveChart`. It edits a saved chart (`chart`), starts a blank one (`start` with `NEW_RECIPE`), or starts from a
 * ready-made chart (`ready`): then its fields are folded under *Customize this chart* and, as long as nothing was changed, the submit
 * adds the ready-made chart as it is (`onAddReady`) instead of saving a copy; changed, it is saved as the user's own and `onSaved` gets
 * its id. On everyone's dashboard a new chart must be shared (`shareLocked`): a private one would be dropped from it.
 */
export function ChartForm({
  vehicle,
  canShare,
  chart,
  start,
  ready = false,
  shareLocked = false,
  previewFirst = false,
  submitLabel,
  onSaved,
  onAddReady,
}: {
  vehicle: { id: string; units: { distance: DistanceUnit; volume: VolumeUnit } }
  /** Sharing with everyone who sees the vehicle needs Edit access to its logs. */
  canShare: boolean
  chart?: EditableChart
  start?: { title: string; recipe: ChartRecipe }
  ready?: boolean
  shareLocked?: boolean
  previewFirst?: boolean
  submitLabel?: string
  /** The chart was saved: its id and title (new, or as edited). */
  onSaved: (id: string, title: string) => void | Promise<unknown>
  onAddReady?: () => void
}) {
  const { t } = useTranslation()
  const initialTitle = chart?.title ?? start?.title ?? ''
  const initialRecipe = chart?.recipe ?? start?.recipe ?? NEW_RECIPE
  const initialShared = chart?.isShared ?? (shareLocked && canShare)
  const [title, setTitle] = useState(initialTitle)
  const [recipe, setRecipe] = useState<ChartRecipe>(initialRecipe)
  const [shared, setShared] = useState(initialShared)
  const [unfolded, setUnfolded] = useState(!ready)
  const [save, { loading, error }] = useMutation(SaveChartDocument)
  const previewId = useId()
  const fieldsId = useId()

  const changed = !ready || title.trim() !== initialTitle || shared !== initialShared || !sameRecipe(recipe, initialRecipe)
  const valid = isReady(recipe)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!valid) return
    if (!changed) {
      onAddReady?.()
      return
    }
    try {
      const result = await save({ variables: { input: { id: chart?.id ?? null, vehicleId: vehicle.id, title, shared: canShare && shared, config: toConfig(recipe) } } })
      await onSaved(result.data?.saveVehicleChart.id ?? chart?.id ?? '', title.trim())
    } catch {
      // shown through `error`
    }
  }

  const preview = (
    <section aria-labelledby={previewId}>
      <Typography id={previewId} sx={{ fontWeight: 700, mb: 1 }}>
        {t('charts.preview')}
      </Typography>
      {valid ? (
        <ChartCard vehicleId={vehicle.id} title={title.trim() || t('charts.preview')} recipe={recipe} units={vehicle.units} headingLevel={2} />
      ) : (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t('errors.chart.datesRequired')}
        </Typography>
      )}
    </section>
  )
  const fields = (
    <Stack sx={{ gap: 1.5 }}>
      <Field name="title" label={t('charts.fields.title')} required>
        <FieldInput maxLength={80} autoComplete="off" value={title} onChange={(e) => setTitle(e.target.value)} />
      </Field>
      <ChartFields recipe={recipe} onChange={setRecipe} />
      {canShare && (
        <LabeledSwitch label={t('charts.fields.shared')} hint={shareLocked ? t('charts.hints.sharedForEveryone') : t('charts.hints.shared')} checked={shared} disabled={shareLocked} onChange={setShared} />
      )}
    </Stack>
  )

  return (
    <Form onSubmit={submit} refusal={error}>
      <Stack sx={{ gap: 1.5 }}>
        {previewFirst && preview}
        {ready ? (
          <>
            <Button type="button" variant="ghost" aria-expanded={unfolded} aria-controls={fieldsId} onClick={() => setUnfolded((u) => !u)} sx={{ alignSelf: 'flex-start' }}>
              <ChevronDown size={16} aria-hidden style={{ transform: unfolded ? 'rotate(180deg)' : undefined }} />
              {t('dashboard.widget.customize')}
            </Button>
            <Collapse in={unfolded} id={fieldsId} unmountOnExit>
              {fields}
            </Collapse>
          </>
        ) : (
          fields
        )}
        {!previewFirst && preview}

        {error && <ErrorMessage error={error} />}
        <DialogButtons>
          <DialogCancel />
          <Button type="submit" disabled={loading || !valid}>
            {ready && !changed ? t('dashboard.widget.add') : (submitLabel ?? t('charts.save'))}
          </Button>
        </DialogButtons>
      </Stack>
    </Form>
  )
}
