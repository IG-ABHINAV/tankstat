import Box from '@mui/material/Box'
import FormLabel from '@mui/material/FormLabel'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import {
  Ban,
  Banknote,
  Calendar,
  CalendarClock,
  CalendarDays,
  CalendarRange,
  ChartArea,
  ChartColumn,
  ChartLine,
  ChartPie,
  ChartSpline,
  Coins,
  Droplet,
  Fuel,
  Gauge,
  Hash,
  Layers,
  Milestone,
  Percent,
  Route,
  Sigma,
  Tag,
  TrendingUp,
  Wrench,
} from 'lucide-react'
import { useId, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Segmented, SegmentedChecks, type SegmentedOption } from '../components/Segmented.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { Field } from '../forms/Field.tsx'
import { FieldDate } from '../forms/FieldDate.tsx'
import type { ChartGrouping, ChartKind, ChartMetric, ChartRange, ChartTrend } from '../gql/generated.ts'
import { ADDITIVE, isOverTime, normalise, withRunningTotal, withTrend, type ChartRecipe } from './chartFormat.ts'

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
const GROUPINGS: ChartGrouping[] = ['MONTH', 'QUARTER', 'YEAR', 'CATEGORY']
const KINDS: ChartKind[] = ['BAR', 'LINE', 'AREA', 'DONUT']
const RANGES: ChartRange[] = ['LAST1_MONTH', 'LAST3_MONTHS', 'LAST6_MONTHS', 'LAST12_MONTHS', 'THIS_YEAR', 'LAST_YEAR', 'ALL', 'CUSTOM']
const TRENDS: ChartTrend[] = ['NONE', 'MOVING_AVERAGE', 'LINEAR']
const SECOND_KINDS = KINDS.filter((k) => k !== 'DONUT') // a second measure is never a donut
/** The second measure's select has no empty value of its own: this one stands for "none". */
const NO_SECOND = 'NONE'
type Extra = 'stacked' | 'runningTotal' | 'compareYearEarlier'

const size = 18
/** Each metric's icon, in the selects' lists and closed fields. */
const METRIC_ICON: Record<ChartMetric, ReactNode> = {
  TOTAL_SPEND: <Coins size={size} aria-hidden />,
  FUEL_COST: <Fuel size={size} aria-hidden />,
  EXPENSE_COST: <Wrench size={size} aria-hidden />,
  FUEL_VOLUME: <Droplet size={size} aria-hidden />,
  DISTANCE: <Route size={size} aria-hidden />,
  AVERAGE_CONSUMPTION: <Gauge size={size} aria-hidden />,
  AVERAGE_PRICE_PER_UNIT: <Tag size={size} aria-hidden />,
  AVERAGE_PRICE_PER_UNIT_BEFORE_DISCOUNT: <Percent size={size} aria-hidden />,
  FILL_UPS: <Hash size={size} aria-hidden />,
  COST_PER_DISTANCE: <Banknote size={size} aria-hidden />,
  FUEL_COST_PER_DISTANCE: <Milestone size={size} aria-hidden />,
}
const GROUPING_ICON: Record<ChartGrouping, ReactNode> = {
  MONTH: <CalendarDays size={size} aria-hidden />,
  QUARTER: <CalendarRange size={size} aria-hidden />,
  YEAR: <Calendar size={size} aria-hidden />,
  CATEGORY: <Tag size={size} aria-hidden />,
}
const KIND_ICON: Record<ChartKind, ReactNode> = {
  BAR: <ChartColumn size={size} aria-hidden />,
  LINE: <ChartLine size={size} aria-hidden />,
  AREA: <ChartArea size={size} aria-hidden />,
  DONUT: <ChartPie size={size} aria-hidden />,
}
const TREND_ICON: Record<ChartTrend, ReactNode> = {
  NONE: <Ban size={size} aria-hidden />,
  MOVING_AVERAGE: <ChartSpline size={size} aria-hidden />,
  LINEAR: <TrendingUp size={size} aria-hidden />,
}
const EXTRA_ICON: Record<Extra, ReactNode> = {
  runningTotal: <Sigma size={size} aria-hidden />,
  compareYearEarlier: <CalendarClock size={size} aria-hidden />,
  stacked: <Layers size={size} aria-hidden />,
}

/** A row of toggles with its name and the chosen one's words beside it, so a glance reads the choice without the icons. */
function Toggles({ label, chosen, hint, children }: { label: string; chosen?: string; hint?: string; children: ReactNode }) {
  const id = useId()
  return (
    <Stack sx={{ gap: 0.5, alignItems: 'flex-start' }}>
      <Stack direction="row" sx={{ gap: 1, alignItems: 'baseline' }}>
        <FormLabel id={id}>{label}</FormLabel>
        {chosen && (
          <Typography component="span" variant="body2" sx={{ color: 'primary.light' }}>
            {chosen}
          </Typography>
        )}
      </Stack>
      {children}
      {hint && (
        <Typography variant="caption" sx={{ color: 'text.secondary' }}>
          {hint}
        </Typography>
      )}
    </Stack>
  )
}

/**
 * The controls of a chart's recipe (#228): what to measure and a second measure as selects whose options carry an icon; how to group it,
 * the chart type, the trend line and how the second measure is drawn as rows of icon toggles with a tooltip each (the chosen one's words
 * beside the row's name); the period as short chips; the extras (a running total, a year earlier, the split into fuel and expenses) as
 * toggles that can be on together. What the chosen measure or grouping rules out is dimmed, not hidden (`normalise` mirrors the server's
 * rules), so the rows never change shape; the title and the sharing are the form's, not the recipe's.
 */
export function ChartFields({ recipe, onChange }: { recipe: ChartRecipe; onChange: (recipe: ChartRecipe) => void }) {
  const { t } = useTranslation()
  const change = (patch: Partial<ChartRecipe>) => onChange(normalise({ ...recipe, ...patch }))
  const additive = ADDITIVE.includes(recipe.metric)
  const overTime = isOverTime(recipe)
  const canStack = recipe.metric === 'TOTAL_SPEND' && recipe.grouping !== 'CATEGORY'
  const notOverTime = overTime ? undefined : t('charts.hints.overTime')
  const extrasOn = (['stacked', 'runningTotal', 'compareYearEarlier'] as const).filter((extra) => recipe[extra])
  const setExtra = (extra: Extra, on: boolean) => {
    if (extra === 'runningTotal') onChange(withRunningTotal(recipe, on))
    else change({ [extra]: on })
  }
  const metricOptions = (list: ChartMetric[]) => list.map((m) => ({ value: m, label: t(`charts.metrics.${m}`), icon: METRIC_ICON[m] }))

  return (
    <>
      <Stack direction="row" sx={{ gap: 1.5, flexWrap: 'wrap', '& > *': { flex: '1 1 14rem' } }}>
        <LabeledSelect label={t('charts.fields.metric')} value={recipe.metric} onChange={(metric) => change({ metric })} options={metricOptions(METRICS)} />
        <LabeledSelect
          label={t('charts.fields.secondMetric')}
          hint={notOverTime ?? t('charts.hints.secondMetric')}
          disabled={!overTime}
          value={recipe.secondMetric ?? NO_SECOND}
          onChange={(value) => change({ secondMetric: value === NO_SECOND ? null : (value as ChartMetric) })}
          options={[{ value: NO_SECOND, label: t('charts.secondNone') }, ...metricOptions(METRICS.filter((m) => m !== recipe.metric))]}
        />
      </Stack>

      <Stack direction="row" sx={{ gap: 3, rowGap: 1.5, flexWrap: 'wrap' }}>
        <Toggles label={t('charts.fields.grouping')} chosen={t(`charts.groupings.${recipe.grouping}`)}>
          <Segmented
            label={t('charts.fields.grouping')}
            value={recipe.grouping}
            onChange={(grouping) => change({ grouping })}
            options={GROUPINGS.map((g): SegmentedOption<ChartGrouping> => ({ value: g, label: t(`charts.groupings.${g}`), icon: GROUPING_ICON[g], ...(g === 'CATEGORY' && recipe.metric !== 'EXPENSE_COST' ? { disabled: true, reason: t('charts.hints.category') } : {}) }))}
          />
        </Toggles>
        <Toggles label={t('charts.fields.kind')} chosen={t(`charts.kinds.${recipe.kind}`)}>
          <Segmented
            label={t('charts.fields.kind')}
            value={recipe.kind}
            onChange={(kind) => change({ kind })}
            options={KINDS.map((k): SegmentedOption<ChartKind> => ({ value: k, label: t(`charts.kinds.${k}`), icon: KIND_ICON[k], ...(k === 'DONUT' && !additive ? { disabled: true, reason: t('charts.hints.donut') } : {}) }))}
          />
        </Toggles>
        <Toggles label={t('charts.fields.trend')} chosen={t(`charts.trends.${recipe.trend}`)}>
          <Segmented
            label={t('charts.fields.trend')}
            value={recipe.trend}
            onChange={(trend) => onChange(withTrend(recipe, trend))}
            options={TRENDS.map((trend): SegmentedOption<ChartTrend> => ({ value: trend, label: t(`charts.trends.${trend}`), icon: TREND_ICON[trend], ...(notOverTime ? { disabled: true, reason: notOverTime } : {}) }))}
          />
        </Toggles>
        {recipe.secondMetric && (
          <Toggles label={t('charts.fields.secondKind')} chosen={t(`charts.kinds.${recipe.secondKind ?? 'LINE'}`)}>
            <Segmented
              label={t('charts.fields.secondKind')}
              value={recipe.secondKind ?? 'LINE'} // never null with a second metric (normalise), typed for the toggles
              onChange={(secondKind) => change({ secondKind })}
              options={SECOND_KINDS.map((k): SegmentedOption<ChartKind> => ({ value: k, label: t(`charts.kinds.${k}`), icon: KIND_ICON[k] }))}
            />
          </Toggles>
        )}
      </Stack>

      <Toggles label={t('charts.fields.range')} chosen={t(`charts.ranges.${recipe.range}`)}>
        <Segmented label={t('charts.fields.range')} value={recipe.range} onChange={(range) => change({ range })} options={RANGES.map((r) => ({ value: r, label: t(`charts.ranges.${r}`) }))} />
      </Toggles>

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

      <Toggles label={t('charts.fields.also')} chosen={extrasOn.map((extra) => t(`charts.fields.${extra}`)).join(' · ') || undefined} hint={t('charts.hints.also')}>
        <SegmentedChecks<Extra>
          label={t('charts.fields.also')}
          values={extrasOn}
          onChange={setExtra}
          options={[
            { value: 'runningTotal', label: t('charts.fields.runningTotal'), icon: EXTRA_ICON.runningTotal, ...(notOverTime ? { disabled: true, reason: notOverTime } : !additive ? { disabled: true, reason: t('charts.hints.runningTotalNeedsTotals') } : { reason: t('charts.hints.runningTotal') }) },
            { value: 'compareYearEarlier', label: t('charts.fields.compareYearEarlier'), icon: EXTRA_ICON.compareYearEarlier, ...(notOverTime ? { disabled: true, reason: notOverTime } : { reason: t('charts.hints.compareYearEarlier') }) },
            { value: 'stacked', label: t('charts.fields.stacked'), icon: EXTRA_ICON.stacked, ...(canStack ? { reason: t('charts.hints.stacked') } : { disabled: true, reason: t('charts.hints.stackNeedsTotalSpend') }) },
          ]}
        />
      </Toggles>
    </>
  )
}
