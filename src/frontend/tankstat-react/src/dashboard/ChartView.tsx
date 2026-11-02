import Box from '@mui/material/Box'
import Link from '@mui/material/Link'
import Stack from '@mui/material/Stack'
import TableBody from '@mui/material/TableBody'
import TableCell from '@mui/material/TableCell'
import TableHead from '@mui/material/TableHead'
import TableRow from '@mui/material/TableRow'
import Typography from '@mui/material/Typography'
import { BarPlot } from '@mui/x-charts/BarChart'
import { ChartsAxis } from '@mui/x-charts/ChartsAxis'
import { ChartsAxisHighlight } from '@mui/x-charts/ChartsAxisHighlight'
import { ChartsClipPath } from '@mui/x-charts/ChartsClipPath'
import { ChartsDataProvider } from '@mui/x-charts/ChartsDataProvider'
import { ChartsGrid } from '@mui/x-charts/ChartsGrid'
import { ChartsLegend } from '@mui/x-charts/ChartsLegend'
import { ChartsSurface } from '@mui/x-charts/ChartsSurface'
import { ChartsTooltip } from '@mui/x-charts/ChartsTooltip'
import { ChartsWrapper } from '@mui/x-charts/ChartsWrapper'
import { LineChart, LinePlot } from '@mui/x-charts/LineChart'
import { PieChart } from '@mui/x-charts/PieChart'
import { useReducedMotion } from 'motion/react'
import { useEffect, useId, useRef, useState, type RefObject } from 'react'
import { useTranslation } from 'react-i18next'
import { SurfaceTable } from '../components/SurfaceTable.tsx'
import type { DistanceUnit, VolumeUnit } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { validLocale } from '../i18n/locale.ts'
import { standaloneLink } from '../theme/components.ts'
import { barPlaces, chartAxes, isValue, labelStep, PALETTE, perCurrency, periodLabel, seriesId, shortPeriodLabels, stackOf, type ChartData, type ChartRecipe, type Series } from './chartFormat.ts'
import { BarLayoutContext, type BarLayout } from './barLayout.ts'
import { PlacedBar } from './PlacedBar.tsx'

type Units = { distance: DistanceUnit; volume: VolumeUnit }

/**
 * The hover tooltip goes to the page, not into the chart's own layer: the tooltip is positioned with `position: fixed`, and inside a card
 * (whose blurred background makes it the reference of fixed children) it landed far outside the screen.
 */
const tooltipInPage = () => ({ tooltip: { container: document.body } })

const noData = (data: ChartData) => data.series.every((s) => s.points.every((p) => !p.value))

/** The gap between two bars of a period, as a part of a bar's width (MUI's default, named so `PlacedBar` lays them out the same way). */
const BAR_GAP_RATIO = 0.1

/**
 * Kept free beside the chart's edges, for half of the first and last period's labels: MUI cuts a label wider than twice its distance from
 * the edge, measuring a year under its month as one line ("nov. 2025"). A bar's period starts half a band in; a line's first point sits on
 * the edge itself.
 */
const LABEL_ROOM = { bars: 18, points: 32 }

/** How wide an element is now (0 until it is laid out, and in jsdom): the axis names as many periods as fit in it. */
function useWidth(): [RefObject<HTMLElement | null>, number] {
  const ref = useRef<HTMLElement>(null)
  const [width, setWidth] = useState(0)
  useEffect(() => {
    const element = ref.current
    if (!element || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(([entry]) => setWidth(Math.round(entry.contentRect.width)))
    observer.observe(element)
    return () => observer.disconnect()
  }, [])
  return [ref, width]
}

/** How a line drawn from the values looks: a trend dashed, a year earlier dotted and fainter (never told apart by colour alone). */
const OVERLAY_LOOK = { trend: { strokeDasharray: '7 5' }, earlier: { strokeDasharray: '2 4', strokeOpacity: 0.75 } } as const

/**
 * A chart of server-calculated data. The picture is for sighted users (hidden from assistive technology); the same numbers are always
 * available as a table next to it, which is what a screen reader reads. A trend or a year earlier is always a line over the values, in
 * its series' colour and on its scale, dashed or dotted; a bar chart is composed from MUI X's parts so its bars and those lines share
 * the axes.
 */
export function ChartView({
  data,
  recipe,
  units,
  title,
  height = 240,
}: {
  data: ChartData
  recipe: Pick<ChartRecipe, 'kind' | 'metric' | 'grouping' | 'stacked'> & Partial<Pick<ChartRecipe, 'trend' | 'compareYearEarlier'>>
  units: Units
  title: string
  height?: number
}) {
  const { t, i18n } = useTranslation()
  const format = useFormat()
  const reduce = useReducedMotion() ?? false
  const [table, setTable] = useState(false)
  const [figure, figureWidth] = useWidth()
  const clipId = useId()

  if (noData(data)) {
    return (
      <Typography variant="body2" role="status" sx={{ color: 'text.secondary' }}>
        {t('dashboard.noData')}
      </Typography>
    )
  }

  const keyLabel = (key: string) => (key === '' ? t('dashboard.uncategorised') : periodLabel(key, validLocale(i18n.language, i18n.resolvedLanguage)))
  const baseName = (s: Series) => `${t(`dashboard.series.${s.kind as 'total' | 'fuel' | 'expenses'}`, { defaultValue: t('dashboard.series.value') })}${s.currency ? ` · ${s.currency}` : ''}`
  const name = (s: Series) => (isValue(s) ? baseName(s) : `${baseName(s)} · ${t(`dashboard.series.role.${s.role as 'MOVING_AVERAGE' | 'LINEAR' | 'YEAR_EARLIER'}`)}`)
  const show = (value: number | null | undefined, s: Series) => {
    // Nothing spent in a currency in a period is no amount: a dash, as a missing value, not "HUF 0" beside the real ones.
    if (value == null || (perCurrency(data.unit) && value === 0)) return t('common.none')
    switch (data.unit) {
      case 'CURRENCY': return format.money(value, s.currency ?? '')
      case 'COST_PER_DISTANCE': return format.costPerDistance(value, s.currency ?? '', units.distance)
      case 'VOLUME': return format.volume(value, units.volume)
      case 'DISTANCE': return format.distance(value, units.distance)
      case 'CONSUMPTION': return format.consumption(value, units)
      default: return format.number(value)
    }
  }
  const axis = (value: number) => (data.unit === 'CURRENCY' || data.unit === 'COUNT' || data.unit === 'DISTANCE' ? format.compact(value) : format.number(value, { maximumFractionDigits: 1 }))

  const keys = [...new Set(data.series.flatMap((s) => s.points.map((p) => p.key)))]
  const rows: Record<string, string | number | null>[] = keys.map((key) => ({ key, label: keyLabel(key), ...Object.fromEntries(data.series.map((s) => [seriesId(s), s.points.find((p) => p.key === key)?.value ?? null])) }))
  const colour = (i: number) => PALETTE[i % PALETTE.length]
  const values = data.series.filter(isValue)
  // A year earlier with nothing logged in it is not drawn (a note says so, below); the table still lists it.
  const overlays = data.series.filter((s) => !isValue(s) && (s.role !== 'YEAR_EARLIER' || s.points.some((p) => p.value)))
  const several = values.length + overlays.length > 1
  // Every currency of a chart of costs on a scale of its own (#159): one axis per currency, shown (in its currency) only when there is one.
  const scales = chartAxes(data)
  // A line drawn from a series has that series' colour; one drawn from a stack's total (no series of its own) the next colours.
  const colours = new Map<Series, string>(values.map((s, i) => [s, colour(i)]))
  const totals = new Map<string, string>()
  for (const s of overlays) {
    const base = values.find((v) => v.kind === s.kind && v.currency === s.currency)
    const key = `${s.kind}|${s.currency ?? ''}`
    if (!base && !totals.has(key)) totals.set(key, colour(values.length + totals.size))
    colours.set(s, base ? colours.get(base)! : totals.get(key)!)
  }
  const overlayLook = Object.fromEntries(overlays.map((s) => [`& path[data-series="${seriesId(s)}"]`, s.role === 'YEAR_EARLIER' ? OVERLAY_LOOK.earlier : OVERLAY_LOOK.trend]))
  const overlayLines = overlays.map((s) => ({
    id: seriesId(s),
    dataKey: seriesId(s),
    label: name(s),
    color: colours.get(s),
    curve: s.role === 'LINEAR' ? ('linear' as const) : ('monotoneX' as const),
    showMark: false,
    connectNulls: true,
    yAxisId: scales.axisOf(s),
    valueFormatter: (value: number | null) => show(value, s),
  }))
  const axisWidth = (currency: string | null) => (currency ? 64 : 48)
  const yAxis = scales.axes.map((a) => ({
    id: a.id,
    position: scales.shown ? ('left' as const) : ('none' as const),
    // Room for an amount with its currency ("HUF 500", "40 E Ft"): MUI's 'auto' measured too little and cut it.
    width: axisWidth(a.currency),
    valueFormatter: (value: number) => (a.currency ? format.compactMoney(value, a.currency) : axis(value)),
    // The highest value of each currency reaches the top, whatever the size of its numbers; from 0, as a bar is, so a line of prices that
    // barely moves does not look like a swing from nothing to everything with no axis to read it by.
    ...(scales.shown ? {} : { domainLimit: 'strict' as const, min: 0 }),
  }))
  const room = recipe.kind === 'BAR' ? LABEL_ROOM.bars : LABEL_ROOM.points
  // A period is named short along the bottom, every `step`-th one so the named ones fit (more on a phone), its year on every one that
  // starts a year among them; in full in the tooltip. A category is named as it is ("Uncategorised" for none).
  const step = labelStep(keys.length, figureWidth - room * 2 - (scales.shown ? axisWidth(scales.axes[0].currency) : 0))
  const short = shortPeriodLabels(keys, validLocale(i18n.language, i18n.resolvedLanguage), recipe.grouping, step)
  const byPeriod = recipe.grouping === 'MONTH' || recipe.grouping === 'QUARTER'
  const period = (key: string, context: { location: string }) => (context.location === 'tick' && byPeriod ? (short.get(key) ?? '') : keyLabel(key))
  // The x axis of every cartesian chart: as high as its labels need (a year goes under its month).
  const periods = { dataKey: 'key', valueFormatter: period, height: 'auto' as const, tickLabelInterval: byPeriod ? (key: string) => short.has(key) : ('auto' as const) }
  // The parts every cartesian chart shares: the periods along the bottom, compact values up the side, light horizontal lines (only with
  // an axis to read them by).
  const cartesian = {
    dataset: rows,
    height,
    // Room for half a period's label beside the edge: MUI cuts a label wider than twice its distance from the edge (the first and the
    // last month), and without a value axis the first one sits at the very edge.
    margin: { top: 8, right: room, bottom: 0, left: scales.shown ? 0 : room },
    yAxis,
    grid: { horizontal: scales.shown },
    hideLegend: !several,
    skipAnimation: reduce,
    disableKeyboardNavigation: true,
    // By axis (the whole period), never by item: a bar is drawn where `PlacedBar` puts it, but MUI finds the one under the pointer at its own place.
    slotProps: { tooltip: { ...tooltipInPage().tooltip, trigger: 'axis' as const } },
  }
  const lines = (area: boolean) => [
    ...values.map((s) => ({
      id: seriesId(s),
      dataKey: seriesId(s),
      label: name(s),
      color: colours.get(s),
      curve: 'monotoneX' as const,
      showMark: rows.length < 25,
      connectNulls: true,
      area,
      stack: area && recipe.stacked ? stackOf(s) : undefined,
      yAxisId: scales.axisOf(s),
      valueFormatter: (value: number | null) => show(value, s),
    })),
    ...overlayLines,
  ]
  // The bars of a period: only those of the currencies (or series) it has, centred in it (#159). Worked out for a bar chart only.
  const barLayout = (): BarLayout => {
    const places = barPlaces({ series: values }, keys, recipe.stacked)
    const byId = new Map(values.map((s) => [seriesId(s), s]))
    return {
      periodOf: (index) => keys[index],
      at: (id, index) => (byId.has(id) ? places.at(byId.get(id)!, index) : null),
      gapRatio: BAR_GAP_RATIO,
      radius: 4,
    }
  }

  const picture =
    recipe.kind === 'DONUT' ? (
      <Stack direction="row" sx={{ gap: 2, flexWrap: 'wrap', justifyContent: 'center' }}>
        {data.series.map((s, i) => (
          <Box key={seriesId(s)} sx={{ width: 'min(100%, 18rem)' }}>
            {several && (
              <Typography variant="body2" sx={{ color: 'text.secondary', textAlign: 'center' }}>
                {name(s)}
              </Typography>
            )}
            <PieChart
              height={height}
              skipAnimation={reduce}
              disableKeyboardNavigation
              slotProps={tooltipInPage()}
              series={[
                {
                  data: s.points.map((p, j) => ({ id: p.key, value: p.value ?? 0, label: keyLabel(p.key), color: colour(j + i) })),
                  innerRadius: '55%',
                  outerRadius: '85%',
                  paddingAngle: 2,
                  cornerRadius: 2,
                  valueFormatter: (item) => show(item.value, s),
                },
              ]}
            />
          </Box>
        ))}
      </Stack>
    ) : recipe.kind === 'LINE' ? (
      <LineChart {...cartesian} sx={overlayLook} xAxis={[{ scaleType: 'point', ...periods }]} series={lines(false)} />
    ) : recipe.kind === 'AREA' ? (
      <LineChart {...cartesian} sx={overlayLook} xAxis={[{ scaleType: 'point', ...periods }]} series={lines(true)} />
    ) : (
      // Composed as MUI's BarChart is, with the lines drawn from the values over the bars. The bars draw themselves (`PlacedBar`),
      // rounded corners included: MUI's `borderRadius` would clip them where MUI placed them.
      <BarLayoutContext.Provider value={barLayout()}>
        <ChartsDataProvider
          dataset={cartesian.dataset}
          height={height}
          margin={cartesian.margin}
          skipAnimation={reduce}
          disableKeyboardNavigation
          xAxis={[{ id: 'periods', scaleType: 'band', ...periods, barGapRatio: BAR_GAP_RATIO }]}
          yAxis={yAxis}
          series={[
            ...values.map((s) => ({
              type: 'bar' as const,
              id: seriesId(s),
              dataKey: seriesId(s),
              label: name(s),
              color: colours.get(s),
              stack: recipe.stacked ? stackOf(s) : undefined,
              yAxisId: scales.axisOf(s),
              valueFormatter: (value: number | null) => show(value, s),
            })),
            ...overlayLines.map((line) => ({ type: 'line' as const, ...line })),
          ]}
        >
          <ChartsWrapper hideLegend={!several} sx={overlayLook}>
            {several && <ChartsLegend />}
            <ChartsSurface>
              <ChartsGrid horizontal={scales.shown} />
              <g clipPath={`url(#${clipId})`}>
                <BarPlot slots={{ bar: PlacedBar }} />
                <LinePlot />
                <ChartsAxisHighlight x="band" />
              </g>
              <ChartsAxis />
              <ChartsClipPath id={clipId} />
            </ChartsSurface>
            {/* By axis (the whole period), never by item: a bar is drawn where `PlacedBar` puts it, but MUI finds the one under the pointer at its own place. */}
            <ChartsTooltip trigger="axis" {...tooltipInPage().tooltip} />
          </ChartsWrapper>
        </ChartsDataProvider>
      </BarLayoutContext.Provider>
    )

  const categories = recipe.grouping === 'CATEGORY'
  // What the lines drawn from the values say, in words: a linear trend from where it starts to where it ends (in the chart's own units,
  // so miles per gallon read right too), and why a line asked for is not there.
  const trendLines = overlays.filter((s) => s.role === 'LINEAR')
  const hasTrend = overlays.some((s) => (s.role === 'LINEAR' || s.role === 'MOVING_AVERAGE') && s.points.some((p) => p.value != null))
  const hasEarlier = overlays.some((s) => s.role === 'YEAR_EARLIER' && s.points.some((p) => p.value))
  const notes = [
    ...trendLines.map((s) => t('dashboard.trendSentence', { series: baseName(s), from: show(s.points[0]?.value, s), to: show(s.points[s.points.length - 1]?.value, s) })),
    ...(recipe.trend && recipe.trend !== 'NONE' && !hasTrend ? [t('dashboard.noTrend')] : []),
    ...(recipe.compareYearEarlier && !hasEarlier ? [t('dashboard.nothingYearEarlier')] : []),
  ]
  return (
    <Box component="figure" ref={figure} sx={{ m: 0 }}>
      <div role="img" aria-label={t('dashboard.chartLabel', { title })}>
        <div aria-hidden>{picture}</div>
      </div>
      {/* A note, not the figure's caption: the chart's title names it. */}
      {recipe.kind !== 'DONUT' && !scales.shown && (
        <Typography component="p" variant="body2" sx={{ color: 'text.secondary', mt: 0.5, mb: 0 }}>
          {t('dashboard.ownScales')}
        </Typography>
      )}
      {notes.map((note) => (
        <Typography key={note} component="p" variant="body2" sx={{ color: 'text.secondary', mt: 0.5, mb: 0 }}>
          {note}
        </Typography>
      ))}
      <Link component="button" type="button" variant="body2" underline="always" aria-expanded={table} onClick={() => setTable(!table)} sx={{ minHeight: 24, mt: 1, ...standaloneLink }}>
        {table ? t('dashboard.hideTable') : t('dashboard.showTable')}
      </Link>
      {table && (
        <Box sx={{ mt: 1 }}>
          <SurfaceTable caption={title}>
            <TableHead>
              <TableRow>
                <TableCell>{categories ? t('dashboard.table.category') : t('dashboard.table.period')}</TableCell>
                {data.series.map((s) => (
                  <TableCell key={seriesId(s)} align="right">
                    {name(s)}
                  </TableCell>
                ))}
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map((row) => (
                <TableRow key={String(row.key)}>
                  <TableCell component="th" scope="row">
                    {String(row.label)}
                  </TableCell>
                  {data.series.map((s) => (
                    <TableCell key={seriesId(s)} align="right">
                      {show(typeof row[seriesId(s)] === 'number' ? (row[seriesId(s)] as number) : null, s)}
                    </TableCell>
                  ))}
                </TableRow>
              ))}
            </TableBody>
          </SurfaceTable>
        </Box>
      )}
    </Box>
  )
}
