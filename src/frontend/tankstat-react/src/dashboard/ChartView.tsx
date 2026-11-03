import Box from '@mui/material/Box'
import Stack from '@mui/material/Stack'
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
import { AreaPlot, LineChart, LinePlot, MarkPlot } from '@mui/x-charts/LineChart'
import { PieChart } from '@mui/x-charts/PieChart'
import { useReducedMotion } from 'motion/react'
import { useEffect, useId, useRef, useState, type RefObject } from 'react'
import { useTranslation } from 'react-i18next'
import type { DistanceUnit, VolumeUnit } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { validLocale } from '../i18n/locale.ts'
import { barPlaces, chartAxes, isValue, labelStep, PALETTE, seriesId, shortPeriodLabels, stackOf, type ChartData, type ChartRecipe, type Series } from './chartFormat.ts'
import { BarLayoutContext, type BarLayout } from './barLayout.ts'
import { noData, useSeriesText } from './seriesText.ts'
import { PlacedBar } from './PlacedBar.tsx'

type Units = { distance: DistanceUnit; volume: VolumeUnit }

/**
 * The hover tooltip goes to the page, not into the chart's own layer: the tooltip is positioned with `position: fixed`, and inside a card
 * (whose blurred background makes it the reference of fixed children) it landed far outside the screen.
 */
const tooltipInPage = () => ({ tooltip: { container: document.body } })

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
 * available as a table (`ChartTable`, which the card opens in a dialog), which is what a screen reader reads. A trend or a year earlier
 * is always a line over the values, in its series' colour and on its scale, dashed or dotted; a bar chart is composed from MUI X's parts
 * so its bars and those lines share the axes.
 */
export function ChartView({
  data,
  recipe,
  units,
  title,
  height = 240,
}: {
  data: ChartData
  recipe: Pick<ChartRecipe, 'kind' | 'metric' | 'grouping' | 'stacked'> & Partial<Pick<ChartRecipe, 'trend' | 'compareYearEarlier' | 'secondKind'>>
  units: Units
  title: string
  height?: number
}) {
  const { t, i18n } = useTranslation()
  const format = useFormat()
  const reduce = useReducedMotion() ?? false
  const [figure, figureWidth] = useWidth()
  const clipId = useId()
  const { keyLabel, baseName, name, show, keys, rows } = useSeriesText(data, recipe, units)

  if (noData(data)) {
    return (
      <Typography variant="body2" role="status" sx={{ color: 'text.secondary' }}>
        {t('dashboard.noData')}
      </Typography>
    )
  }

  // The first measure is the recipe's metric: it may have no series at all in the range.
  const firstMetric = recipe.metric
  const axis = (value: number, unit: Series['unit']) => (unit === 'CURRENCY' || unit === 'COUNT' || unit === 'DISTANCE' ? format.compact(value) : format.number(value, { maximumFractionDigits: 1 }))
  const colour = (i: number) => PALETTE[i % PALETTE.length]
  const values = data.series.filter(isValue)
  // A year earlier with nothing logged in it is not drawn (a note says so, below); the table still lists it.
  const overlays = data.series.filter((s) => !isValue(s) && (s.role !== 'YEAR_EARLIER' || s.points.some((p) => p.value)))
  const several = values.length + overlays.length > 1
  // How a series is drawn: the first measure as the chart's kind, a second one as its own; the lines drawn from the values always as lines.
  const kindOf = (s: Series) => (s.metric === firstMetric ? recipe.kind : (recipe.secondKind ?? 'LINE'))
  const hasBars = values.some((s) => kindOf(s) === 'BAR')
  // Every currency of a chart of costs on a scale of its own (#159), and a second measure's units on the right: one axis per unit (and
  // currency), each side's shown only when it has one.
  const scales = chartAxes(data, firstMetric)
  // A line drawn from a series has that series' colour; one drawn from a stack's total (no series of its own) the next colours.
  const colours = new Map<Series, string>(values.map((s, i) => [s, colour(i)]))
  const totals = new Map<string, string>()
  for (const s of overlays) {
    const base = values.find((v) => v.metric === s.metric && v.kind === s.kind && v.currency === s.currency)
    const key = `${s.metric}|${s.kind}|${s.currency ?? ''}`
    if (!base && !totals.has(key)) totals.set(key, colour(values.length + totals.size))
    colours.set(s, base ? colours.get(base)! : totals.get(key)!)
  }
  const overlayLook = Object.fromEntries(overlays.map((s) => [`& path[data-series="${seriesId(s)}"]`, s.role === 'YEAR_EARLIER' ? OVERLAY_LOOK.earlier : OVERLAY_LOOK.trend]))
  const stackedHere = (s: Series) => recipe.stacked && s.metric === firstMetric
  // What every series tells MUI, whatever it is drawn as: its id, name, colour, scale and how its values are written.
  const base = (s: Series) => ({ id: seriesId(s), dataKey: seriesId(s), label: name(s), color: colours.get(s), yAxisId: scales.axisOf(s), valueFormatter: (value: number | null) => show(value, s) })
  const valueLine = (s: Series, area: boolean) => ({ ...base(s), curve: 'monotoneX' as const, showMark: rows.length < 25, connectNulls: true, area, stack: area && stackedHere(s) ? stackOf(s) : undefined })
  const valueBar = (s: Series) => ({ ...base(s), stack: stackedHere(s) ? stackOf(s) : undefined })
  const overlayLines = overlays.map((s) => ({ ...base(s), curve: s.role === 'LINEAR' ? ('linear' as const) : ('monotoneX' as const), showMark: false, connectNulls: true }))
  const axisWidth = (currency: string | null) => (currency ? 64 : 48)
  // The shown axis first: MUI's grid lines follow the first value axis, and a chart whose only readable scale is on the right still gets them.
  const yAxis = [...scales.axes].sort((a, b) => Number(b.shown) - Number(a.shown)).map((a) => ({
    id: a.id,
    position: a.shown ? a.side : ('none' as const),
    // Room for an amount with its currency ("HUF 500", "40 E Ft"): MUI's 'auto' measured too little and cut it.
    width: axisWidth(a.currency),
    valueFormatter: (value: number) => (a.currency ? format.compactMoney(value, a.currency) : axis(value, a.unit)),
    // The highest value of each currency reaches the top, whatever the size of its numbers; from 0, as a bar is, so a line of prices that
    // barely moves does not look like a swing from nothing to everything with no axis to read it by.
    ...(a.shown ? {} : { domainLimit: 'strict' as const, min: 0 }),
  }))
  const shownOn = (side: 'left' | 'right') => scales.axes.find((a) => a.side === side && a.shown)
  const [leftAxis, rightAxis] = [shownOn('left'), shownOn('right')]
  const anyAxis = Boolean(leftAxis || rightAxis)
  const room = hasBars ? LABEL_ROOM.bars : LABEL_ROOM.points
  // A period is named short along the bottom, every `step`-th one so the named ones fit (more on a phone), its year on every one that
  // starts a year among them; in full in the tooltip. A category is named as it is ("Uncategorised" for none).
  const step = labelStep(keys.length, figureWidth - room * 2 - (leftAxis ? axisWidth(leftAxis.currency) : 0) - (rightAxis ? axisWidth(rightAxis.currency) : 0))
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
    margin: { top: 8, right: rightAxis ? 0 : room, bottom: 0, left: leftAxis ? 0 : room },
    yAxis,
    grid: { horizontal: anyAxis },
    hideLegend: !several,
    skipAnimation: reduce,
    disableKeyboardNavigation: true,
    // By axis (the whole period), never by item: a bar is drawn where `PlacedBar` puts it, but MUI finds the one under the pointer at its own place.
    slotProps: { tooltip: { ...tooltipInPage().tooltip, trigger: 'axis' as const } },
  }
  // The bars of a period: only those of the currencies (or series) it has, centred in it (#159). Worked out for a chart with bars only.
  const bars = values.filter((s) => kindOf(s) === 'BAR')
  const barLayout = (): BarLayout => {
    const places = barPlaces({ series: bars }, keys, recipe.stacked, firstMetric)
    const byId = new Map(bars.map((s) => [seriesId(s), s]))
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
    ) : !hasBars ? (
      <LineChart {...cartesian} sx={overlayLook} xAxis={[{ scaleType: 'point', ...periods }]} series={[...values.map((s) => valueLine(s, kindOf(s) === 'AREA')), ...overlayLines]} />
    ) : (
      // Composed as MUI's BarChart is: the bars, and any line or area (a second measure, a trend, a year earlier) over them on the same
      // periods. The bars draw themselves (`PlacedBar`), rounded corners included: MUI's `borderRadius` would clip them where MUI placed them.
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
            ...values.map((s) => (kindOf(s) === 'BAR' ? { type: 'bar' as const, ...valueBar(s) } : { type: 'line' as const, ...valueLine(s, kindOf(s) === 'AREA') })),
            ...overlayLines.map((line) => ({ type: 'line' as const, ...line })),
          ]}
        >
          <ChartsWrapper hideLegend={!several} sx={overlayLook}>
            {several && <ChartsLegend />}
            <ChartsSurface>
              <ChartsGrid horizontal={anyAxis} />
              <g clipPath={`url(#${clipId})`}>
                <AreaPlot />
                <BarPlot slots={{ bar: PlacedBar }} />
                <LinePlot />
                <ChartsAxisHighlight x="band" />
              </g>
              {/* Outside the clip, as MUI's own LineChart draws them: a mark at a series' highest value sits on the plot's edge. */}
              <MarkPlot />
              <ChartsAxis />
              <ChartsClipPath id={clipId} />
            </ChartsSurface>
            {/* By axis (the whole period), never by item: a bar is drawn where `PlacedBar` puts it, but MUI finds the one under the pointer at its own place. */}
            <ChartsTooltip trigger="axis" {...tooltipInPage().tooltip} />
          </ChartsWrapper>
        </ChartsDataProvider>
      </BarLayoutContext.Provider>
    )

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
      {recipe.kind !== 'DONUT' && scales.ownScales && (
        <Typography component="p" variant="body2" sx={{ color: 'text.secondary', mt: 0.5, mb: 0 }}>
          {t('dashboard.ownScales')}
        </Typography>
      )}
      {notes.map((note) => (
        <Typography key={note} component="p" variant="body2" sx={{ color: 'text.secondary', mt: 0.5, mb: 0 }}>
          {note}
        </Typography>
      ))}
    </Box>
  )
}
