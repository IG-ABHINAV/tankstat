import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import ButtonBase from '@mui/material/ButtonBase'
import Skeleton from '@mui/material/Skeleton'
import { useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useTranslation } from 'react-i18next'
import { visuallyHidden } from '../components/visuallyHidden.ts'
import { ChartDataDocument, VehicleFigureDocument, type ChartMetric, type DistanceUnit, type FigureMeasure, type VehicleFigureQuery, type VolumeUnit } from '../gql/generated.ts'
import { useErrorText } from '../i18n/errors.ts'
import { useFormat } from '../i18n/format.ts'
import { COARSE } from '../theme/components.ts'
import { useChangeWords, type ChangeOf } from './changeWords.ts'
import { NEW_RECIPE, toConfig } from './chartFormat.ts'
import { MEANING, periodFigureOf, type FigureRecipe } from './figureRecipe.ts'
import { Kpi, ValueWithUnit } from './Kpi.tsx'
import { LazySparkline } from './LazySparkline.tsx'
import { OutsideTile, useTileStrip } from './tileChrome.ts'
import { comparedWith, namesFillUps, toInput } from './trendPeriod.ts'
import { TrendPeriodPopover, type PeriodControl } from './TrendPeriodPopover.tsx'
import { usePeriodText } from './usePeriodText.ts'

type Units = { distance: DistanceUnit; volume: VolumeUnit }
export type FigureData = VehicleFigureQuery['vehicleFigure']

/** How a measure's value is written (#274): money, money per distance or per volume (all per currency), or one value in its unit. */
type Kind = 'money' | 'perDistance' | 'price' | 'consumption' | 'distance' | 'volume' | 'count' | 'odometer' | 'lastFillUp'
const KIND: Record<FigureMeasure, Kind> = {
  TOTAL_SPEND: 'money',
  FUEL_COST: 'money',
  EXPENSE_COST: 'money',
  COST_PER_DISTANCE: 'perDistance',
  FUEL_COST_PER_DISTANCE: 'perDistance',
  AVERAGE_PRICE_PER_UNIT: 'price',
  AVERAGE_PRICE_PER_UNIT_BEFORE_DISCOUNT: 'price',
  AVERAGE_CONSUMPTION: 'consumption',
  DISTANCE: 'distance',
  FUEL_VOLUME: 'volume',
  FILL_UPS: 'count',
  ODOMETER: 'odometer',
  LAST_FILL_UP: 'lastFillUp',
}
const BY_CURRENCY: readonly Kind[] = ['money', 'perDistance', 'price']

/**
 * A key figure drawn from its answer (#274): any recipe, by its measure. The value in the measure's unit (money the main currency's, the
 * owner's choice B: the other currencies are the note), the note (the other currencies; the fill-ups a mean over them is of; the last
 * fill-up's counts), the comparison when the period has one (the arrow and the sentence, coloured by `MEANING`), and the sparkline the tile
 * hands it. Also the period popover's and the Add widget dialog's preview of a period being chosen. Until there is an answer (`data` null)
 * the same card waits with its label, or says why it has none (`error`): one card either way, so it is not drawn again when the answer comes.
 */
export function FigureView({ recipe, data, error, rows, units, label, chip, periodWords, sparkline }: { recipe: Pick<FigureRecipe, 'measure' | 'period'>; data: FigureData | null | undefined; error?: unknown; rows: number; units: Units; label: string; chip?: ReactNode; periodWords?: string; sparkline?: ReactNode }) {
  const { t } = useTranslation()
  const format = useFormat()
  const errorText = useErrorText()
  const wordsOf = useChangeWords()
  const none = t('dashboard.kpi.none')
  const p = recipe.period
  // The answer and its period are what the card shows in words: a new one fits it afresh (#259).
  const fitKey = JSON.stringify([p, data ?? null, !!error])
  if (!data)
    return (
      <Kpi
        rows={rows}
        label={label}
        chip={chip}
        fitKey={fitKey}
        value={
          error ? none : (
            <>
              <Box component="span" role="status" sx={visuallyHidden}>
                {t('app.loading')}
              </Box>
              <Skeleton aria-hidden variant="text" width="60%" sx={{ display: 'inline-block' }} />
            </>
          )
        }
        hint={error ? errorText(error) : undefined}
      />
    )

  const kind = KIND[recipe.measure]
  // What the recent window is compared with; a period without a comparison (#272), or a point in time, has no change to show.
  const compared = p && data.earlier ? comparedWith(p, data.earlier) : null
  const than = compared ? String(t(compared.key as never, compared.values)) : ''
  const change = (of: Omit<ChangeOf, 'than' | 'meaning'>) => (compared ? wordsOf({ ...of, than, meaning: MEANING[recipe.measure] }) : undefined)
  const card = (value: ReactNode, hint?: string, changed?: ReturnType<typeof change>) => (
    <Kpi rows={rows} label={label} chip={chip} period={periodWords} fitKey={fitKey} value={value} hint={hint} change={changed} sparkline={sparkline} />
  )

  if (BY_CURRENCY.includes(kind)) {
    const write = (v: number, currency: string) =>
      kind === 'money' ? format.money(v, currency) : kind === 'perDistance' ? format.costPerDistance(v, currency, units.distance) : format.pricePerUnit(v, currency, units.volume)
    const main = data.currencies[0]
    const others = data.currencies.slice(1).filter((c) => c.recent)
    const value =
      !main ? none
      : kind === 'money' ? format.money(main.recent ?? 0, main.currency)
      : main.recent == null ? none
      : kind === 'perDistance' ? <ValueWithUnit {...format.costPerDistanceParts(main.recent, main.currency, units.distance)} />
      : write(main.recent, main.currency)
    return card(
      value,
      others.length > 0 ? others.map((c) => write(c.recent!, c.currency)).join(' · ') : undefined,
      main && change({ recent: main.recent, earlier: main.earlier, size: (v) => write(v, main.currency), none: t('dashboard.trend.nothingBefore'), noneRecent: kind === 'perDistance' ? t('dashboard.trend.nothingDriven') : undefined }),
    )
  }

  const v = data.value
  switch (kind) {
    case 'consumption': {
      // Fuel per distance in both windows (the stored figure), whatever unit the words show it in: more is more fuel, in mpg too.
      const byDates = p ? namesFillUps(p) : false
      const recent = data.recent
      return card(
        v?.recent != null ? <ValueWithUnit {...format.consumptionParts(v.recent, units)} /> : none,
        v?.recent != null && recent ? t(byDates ? 'dashboard.kpi.fillUpsIn' : 'dashboard.kpi.lastFillUps', { count: recent.fillUps }) : undefined,
        change({
          recent: v?.recent,
          earlier: v?.earlier,
          fuel: true,
          wrap: byDates && recent && data.earlier ? (sentence) => t('dashboard.trend.fillUpCounts', { sentence, recent: recent.fillUps, earlier: data.earlier!.fillUps }) : undefined,
          none: t('dashboard.trend.tooFew'),
        }),
      )
    }
    case 'distance':
      return card(v?.recent != null ? format.distance(v.recent, units.distance) : none, undefined, v ? change({ recent: v.recent, earlier: v.earlier, size: (d) => format.distance(d, units.distance), none: t('dashboard.trend.nothingBefore') }) : undefined)
    case 'volume':
      return card(v?.recent != null ? format.volume(v.recent, units.volume) : none, undefined, v ? change({ recent: v.recent, earlier: v.earlier, size: (d) => format.volume(d, units.volume), none: t('dashboard.trend.nothingBefore') }) : undefined)
    case 'count':
      return card(v?.recent != null ? format.number(v.recent) : none, undefined, v ? change({ recent: v.recent, earlier: v.earlier, size: (d) => t('dashboard.kpi.fillUpCount', { count: d }), none: t('dashboard.trend.nothingBefore') }) : undefined)
    case 'odometer':
      return card(v?.recent != null ? format.distance(v.recent, units.distance) : none)
    case 'lastFillUp': {
      const last = data.lastFillUp
      return card(last?.date ? format.date(last.date) : none, last ? t('dashboard.kpi.counts', { fuel: last.fillUps, expenses: last.expenses }) : undefined)
    }
    default:
      return card(none)
  }
}

/**
 * A key figure of the dashboard (#274): any recipe, its own `vehicleFigure` answer (refetched as its period changes), its sparkline when the
 * recipe has one (the measure by month over the last six months, `vehicleChartData`, the main currency's), and, while the dashboard is
 * arranged (#260), its period's chip on the tile's edit strip after its name (`TileStripContext`'s lead slot, #262), a button that opens
 * the period's popover; outside Customize the card has no chip, and its arrow says the period instead. A point in time has no period, so
 * no chip.
 */
export function KeyFigure({ recipe, label, vehicleId, rows, units, control }: { recipe: FigureRecipe; label: string; vehicleId: string; rows: number; units: Units; control?: PeriodControl }) {
  const { t } = useTranslation()
  const p = recipe.period
  const { data, error } = useQuery(VehicleFigureDocument, { variables: { vehicleId, measure: recipe.measure, period: p ? toInput(p) : null }, fetchPolicy: 'cache-and-network' })
  const periodText = usePeriodText()
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const figure = periodFigureOf(recipe)
  const words = p && figure ? periodText(figure, p) : undefined
  // The strip's lead slot, after the tile's name (#262); null outside Customize (and `control` undefined), so the chip is only ever on a
  // strip being shown.
  const slot = useTileStrip()?.lead
  const chip = control && slot && words && createPortal(<PeriodChip words={words} name={t('dashboard.period.chipName', { figure: label, period: words })} open={anchor !== null} onClick={(e) => setAnchor(e.currentTarget)} />, slot)
  // The popover's preview is no tile under a strip (`OutsideTile`): it keeps its label and a card's own padding, not the strip's inset.
  const popover = control && anchor && p && figure && (
    <OutsideTile>
      <TrendPeriodPopover anchor={anchor} figure={figure} recipe={recipe} label={label} vehicleId={vehicleId} units={units} period={p} control={control} onClose={() => setAnchor(null)} />
    </OutsideTile>
  )
  const main = data?.vehicleFigure.currencies[0]?.currency
  return (
    <>
      <FigureView
        recipe={recipe}
        data={data?.vehicleFigure}
        error={error}
        rows={rows}
        units={units}
        label={label}
        periodWords={words}
        sparkline={recipe.sparkline && recipe.measure !== 'ODOMETER' && recipe.measure !== 'LAST_FILL_UP' ? <FigureSparkline vehicleId={vehicleId} measure={recipe.measure as ChartMetric} currency={main} /> : undefined}
      />
      {chip}
      {popover}
    </>
  )
}

/**
 * A key figure's sparkline (#274): its measure by month over the last six months, the current one included (`LAST6_MONTHS`), as the
 * dashboard's charts ask for it; for money the main currency's series, and none until there is one, as *Spent this month*'s always was.
 */
function FigureSparkline({ vehicleId, measure, currency }: { vehicleId: string; measure: ChartMetric; currency: string | null | undefined }) {
  const { t } = useTranslation()
  const byCurrency = BY_CURRENCY.includes(KIND[measure])
  // Named by its measure (#275: the builder offers a sparkline on any); total spend as the card's always was.
  const label = measure === 'TOTAL_SPEND' ? undefined : t('dashboard.kpi.sparkline', { measure: t(`charts.metrics.${measure}`) })
  const { data } = useQuery(ChartDataDocument, {
    variables: { vehicleId, config: toConfig({ ...NEW_RECIPE, metric: measure, grouping: 'MONTH', kind: 'LINE', range: 'LAST6_MONTHS' }) },
    fetchPolicy: 'cache-and-network',
    skip: byCurrency && !currency,
  })
  const series = data?.vehicleChartData.series.filter((s) => s.role === 'VALUE')
  const line = byCurrency ? series?.find((s) => s.currency === currency) : series?.[0]
  if (!line || line.points.length === 0) return null
  return <LazySparkline points={line.points.map((pt) => ({ month: pt.key, amount: pt.value ?? 0 }))} currency={byCurrency ? currency : null} label={label} />
}

/**
 * The chip as a button on the tile's edit strip (#260): the period's words in a pill outlined in the primary colour, so it is seen and pressed,
 * named "Spending period: 12 full months. Change it". On a touch screen the button is 44 px tall while the pill keeps its height (its
 * margins give the room back).
 */
function PeriodChip({ words, name, open, onClick }: { words: string; name: string; open: boolean; onClick: (event: React.MouseEvent<HTMLButtonElement>) => void }) {
  return (
    <ButtonBase
      aria-label={name}
      aria-haspopup="dialog"
      aria-expanded={open}
      onClick={onClick}
      sx={{ flex: '0 1 auto', maxWidth: '100%', minWidth: 0, borderRadius: 999, [COARSE]: { minHeight: 44, minWidth: 44, my: '-10px' }, '&:focus-visible > span': { outline: 2, outlineColor: 'primary.main', outlineOffset: 2 } }}
    >
      <Box
        component="span"
        sx={(theme) => ({
          display: 'block',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
          px: 1,
          py: 0.25,
          borderRadius: 999,
          border: 2,
          borderColor: 'primary.main',
          bgcolor: open ? theme.vars.palette.primary.soft : theme.vars.palette.background.paper,
          color: theme.vars.palette.primary.softText,
          typography: 'caption',
          fontWeight: 600,
        })}
      >
        {words}
      </Box>
    </ButtonBase>
  )
}
