import { useMutation } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useId, useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { LabeledSwitch } from '../components/LabeledSwitch.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../dialogs/DialogTrigger.tsx'
import { useDialogState } from '../dialogs/useDialogState.ts'
import { Field } from '../forms/Field.tsx'
import { FieldDate } from '../forms/FieldDate.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import { Form } from '../forms/Form.tsx'
import { SaveChartDocument, type ChartGrouping, type ChartKind, type ChartMetric, type ChartRange, type ChartTrend, type DistanceUnit, type VolumeUnit } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { useToast } from '../toast/toastContext.ts'
import { ChartCard } from './ChartCard.tsx'
import { ADDITIVE, isOverTime, isReady, NEW_RECIPE, normalise, toConfig, withRunningTotal, withTrend, type ChartRecipe } from './chartFormat.ts'

const METRICS: ChartMetric[] = [
  'TOTAL_SPEND',
  'FUEL_COST',
  'EXPENSE_COST',
  'FUEL_VOLUME',
  'DISTANCE',
  'AVERAGE_CONSUMPTION',
  'AVERAGE_PRICE_PER_UNIT',
  'AVERAGE_PRICE_PER_UNIT_BEFORE_DISCOUNT',
  'FILL_UPS',
  'COST_PER_DISTANCE',
  'FUEL_COST_PER_DISTANCE',
]
const KINDS: ChartKind[] = ['BAR', 'LINE', 'AREA', 'DONUT']
const RANGES: ChartRange[] = ['LAST1_MONTH', 'LAST3_MONTHS', 'LAST6_MONTHS', 'LAST12_MONTHS', 'THIS_YEAR', 'LAST_YEAR', 'ALL', 'CUSTOM']
const TRENDS: ChartTrend[] = ['NONE', 'MOVING_AVERAGE', 'LINEAR']
const SECOND_KINDS = KINDS.filter((k) => k !== 'DONUT') // a second measure is never a donut
/** The second measure's select has no empty value of its own: this one stands for "none". */
const NO_SECOND = 'NONE'

export interface EditableChart {
  id: string
  title: string
  isShared: boolean
  recipe: ChartRecipe
}

/**
 * Compose a chart from the data the app can provide: what to measure, how to group it, the chart type and the period (a preset or a
 * custom from-to range), and for a chart over time a trend line, a running total or the same periods a year earlier. Only valid
 * combinations are offered, and the preview shows the user's own data as the choices change. A second measure (another metric, as
 * bars, a line or an area) can join a chart over time, on a scale of its own.
 */
export function ChartBuilderDialog({
  trigger,
  vehicle,
  chart,
  start,
  canShare,
  onSaved,
}: {
  trigger: ReactNode
  vehicle: { id: string; units: { distance: DistanceUnit; volume: VolumeUnit } }
  chart?: EditableChart
  /** A new chart that starts from a recipe and title (a ready-made chart copied as one's own). */
  start?: { title: string; recipe: ChartRecipe }
  /** Sharing with everyone who sees the vehicle needs Edit access to its logs. */
  canShare: boolean
  onSaved: () => void | Promise<unknown>
}) {
  const { t } = useTranslation()
  const { toast } = useToast()
  const [open, setOpen] = useDialogState()

  return (
    <>
      <DialogTrigger trigger={trigger} open={open} onOpen={() => setOpen(true)} />
      <DialogFrame open={open} onClose={() => setOpen(false)} maxWidth={640} title={chart ? t('charts.dialogEdit') : start ? t('dashboard.copyTitle') : t('charts.dialogAdd')} description={t('charts.dialogDescription')}>
        {open && (
          <BuilderForm
            vehicle={vehicle}
            chart={chart}
            start={start}
            canShare={canShare}
            onSaved={async () => {
              await onSaved()
              setOpen(false)
              toast(t('toast.saved'))
            }}
          />
        )}
      </DialogFrame>
    </>
  )
}

function BuilderForm({
  vehicle,
  chart,
  start,
  canShare,
  onSaved,
}: {
  vehicle: { id: string; units: { distance: DistanceUnit; volume: VolumeUnit } }
  chart?: EditableChart
  start?: { title: string; recipe: ChartRecipe }
  canShare: boolean
  onSaved: () => Promise<unknown>
}) {
  const { t } = useTranslation()
  const [title, setTitle] = useState(chart?.title ?? start?.title ?? '')
  const [recipe, setRecipe] = useState<ChartRecipe>(chart?.recipe ?? start?.recipe ?? NEW_RECIPE)
  const [shared, setShared] = useState(chart?.isShared ?? false)
  const [save, { loading, error }] = useMutation(SaveChartDocument)
  const previewId = useId()

  const change = (patch: Partial<ChartRecipe>) => setRecipe((r) => normalise({ ...r, ...patch }))
  const groupings: ChartGrouping[] = recipe.metric === 'EXPENSE_COST' ? ['MONTH', 'QUARTER', 'YEAR', 'CATEGORY'] : ['MONTH', 'QUARTER', 'YEAR']
  const kinds = KINDS.filter((k) => k !== 'DONUT' || ADDITIVE.includes(recipe.metric))
  const canStack = recipe.metric === 'TOTAL_SPEND' && recipe.grouping !== 'CATEGORY'
  const overTime = isOverTime(recipe)
  const canRunningTotal = overTime && ADDITIVE.includes(recipe.metric)
  const ready = isReady(recipe)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!ready) return
    try {
      await save({ variables: { input: { id: chart?.id ?? null, vehicleId: vehicle.id, title, shared: canShare && shared, config: toConfig(recipe) } } })
      await onSaved()
    } catch {
      // shown through `error`
    }
  }

  return (
    <Form onSubmit={submit} refusal={error}>
      <Stack sx={{ gap: 1.5 }}>
        <Field name="title" label={t('charts.fields.title')} required>
          <FieldInput maxLength={80} autoComplete="off" value={title} onChange={(e) => setTitle(e.target.value)} />
        </Field>
        <Stack direction="row" sx={{ gap: 1.5, flexWrap: 'wrap', '& > *': { flex: '1 1 12rem' } }}>
          <LabeledSelect label={t('charts.fields.metric')} value={recipe.metric} onChange={(metric) => change({ metric })} options={METRICS.map((m) => ({ value: m, label: t(`charts.metrics.${m}`) }))} />
          <LabeledSelect label={t('charts.fields.grouping')} value={recipe.grouping} onChange={(grouping) => change({ grouping })} options={groupings.map((g) => ({ value: g, label: t(`charts.groupings.${g}`) }))} />
          <LabeledSelect label={t('charts.fields.kind')} value={recipe.kind} onChange={(kind) => change({ kind })} options={kinds.map((k) => ({ value: k, label: t(`charts.kinds.${k}`) }))} />
          <LabeledSelect label={t('charts.fields.range')} value={recipe.range} onChange={(range) => change({ range })} options={RANGES.map((r) => ({ value: r, label: t(`charts.ranges.${r}`) }))} />
        </Stack>

        {recipe.range === 'CUSTOM' && (
          <Stack direction="row" sx={{ gap: 1.5, flexWrap: 'wrap', '& > *': { flex: '1 1 12rem' } }}>
            <Box>
              <Field name="from" label={t('charts.fields.from')} required invalid={{ message: t('errors.chart.datesReversed'), test: () => recipe.from !== null && recipe.to !== null && recipe.from > recipe.to }}>
                <FieldDate value={recipe.from ?? ''} onChange={(from) => change({ from: from || null })} />
              </Field>
            </Box>
            <Box>
              <Field name="to" label={t('charts.fields.to')} required>
                <FieldDate value={recipe.to ?? ''} onChange={(to) => change({ to: to || null })} />
              </Field>
            </Box>
          </Stack>
        )}

        {canStack && <LabeledSwitch label={t('charts.fields.stacked')} hint={t('charts.hints.stacked')} checked={recipe.stacked} onChange={(stacked) => change({ stacked })} />}
        {overTime && (
          <>
            <LabeledSelect
              label={t('charts.fields.trend')}
              hint={t('charts.hints.trend')}
              value={recipe.trend}
              onChange={(trend) => setRecipe((r) => withTrend(r, trend))}
              options={TRENDS.map((trend) => ({ value: trend, label: t(`charts.trends.${trend}`) }))}
            />
            {canRunningTotal && (
              <LabeledSwitch label={t('charts.fields.runningTotal')} hint={t('charts.hints.runningTotal')} checked={recipe.runningTotal} onChange={(on) => setRecipe((r) => withRunningTotal(r, on))} />
            )}
            <LabeledSwitch
              label={t('charts.fields.compareYearEarlier')}
              hint={t('charts.hints.compareYearEarlier')}
              checked={recipe.compareYearEarlier}
              onChange={(compareYearEarlier) => change({ compareYearEarlier })}
            />
            <Stack direction="row" sx={{ gap: 1.5, flexWrap: 'wrap', '& > *': { flex: '1 1 12rem' } }}>
              <LabeledSelect
                label={t('charts.fields.secondMetric')}
                hint={t('charts.hints.secondMetric')}
                value={recipe.secondMetric ?? NO_SECOND}
                onChange={(value) => change({ secondMetric: value === NO_SECOND ? null : (value as ChartMetric) })}
                options={[{ value: NO_SECOND, label: t('charts.secondNone') }, ...METRICS.filter((m) => m !== recipe.metric).map((m) => ({ value: m, label: t(`charts.metrics.${m}`) }))]}
              />
              {recipe.secondMetric && (
                <LabeledSelect
                  label={t('charts.fields.secondKind')}
                  value={recipe.secondKind ?? 'LINE'} // never null with a second metric (normalise), typed for the select
                  onChange={(secondKind) => change({ secondKind })}
                  options={SECOND_KINDS.map((k) => ({ value: k, label: t(`charts.kinds.${k}`) }))}
                />
              )}
            </Stack>
          </>
        )}
        {canShare && <LabeledSwitch label={t('charts.fields.shared')} hint={t('charts.hints.shared')} checked={shared} onChange={setShared} />}

        <section aria-labelledby={previewId}>
          <Typography id={previewId} sx={{ fontWeight: 700, mb: 1 }}>
            {t('charts.preview')}
          </Typography>
          {ready ? (
            <ChartCard vehicleId={vehicle.id} title={title.trim() || t('charts.preview')} recipe={recipe} units={vehicle.units} headingLevel={2} />
          ) : (
            <Typography variant="body2" sx={{ color: 'text.secondary' }}>
              {t('errors.chart.datesRequired')}
            </Typography>
          )}
        </section>

        {error && <ErrorMessage error={error} />}
        <DialogButtons>
          <DialogCancel />
          <Button type="submit" disabled={loading || !ready}>
            {t('charts.save')}
          </Button>
        </DialogButtons>
      </Stack>
    </Form>
  )
}
