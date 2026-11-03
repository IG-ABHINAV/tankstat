import { useTranslation } from 'react-i18next'
import type { DistanceUnit, VolumeUnit } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { validLocale } from '../i18n/locale.ts'
import { isValue, perCurrency, periodLabel, seriesId, type ChartData, type ChartRecipe, type Series } from './chartFormat.ts'

type Units = { distance: DistanceUnit; volume: VolumeUnit }

/** Nothing to draw or list: every value is missing or 0. */
export const noData = (data: ChartData) => data.series.every((s) => s.points.every((p) => !p.value))

/**
 * The words of a chart's data, shared by the picture (`ChartView`) and the table (`ChartTable`): a period's label, a series' name (the
 * first measure's by its kind, "Fuel · HUF"; a second measure's by its metric; a line drawn from the values by its role too), every value
 * written in its own measure's unit, and the rows (one per period, a column per series).
 */
export function useSeriesText(data: ChartData, recipe: Pick<ChartRecipe, 'metric'>, units: Units) {
  const { t, i18n } = useTranslation()
  const format = useFormat()
  const keyLabel = (key: string) => (key === '' ? t('dashboard.uncategorised') : periodLabel(key, validLocale(i18n.language, i18n.resolvedLanguage)))
  // The first measure's series are named by their kind ("Fuel · HUF"); a second measure's by its metric ("Distance driven"). The recipe
  // says which is first: the first measure may have no series at all in the range.
  const firstMetric = recipe.metric
  const baseName = (s: Series) =>
    `${s.metric !== firstMetric ? t(`charts.metrics.${s.metric}`) : t(`dashboard.series.${s.kind as 'total' | 'fuel' | 'expenses'}`, { defaultValue: t('dashboard.series.value') })}${s.currency ? ` · ${s.currency}` : ''}`
  const name = (s: Series) => (isValue(s) ? baseName(s) : `${baseName(s)} · ${t(`dashboard.series.role.${s.role as 'MOVING_AVERAGE' | 'LINEAR' | 'YEAR_EARLIER'}`)}`)
  // Every series is written in its own measure's unit: a chart of distance and consumption has both.
  const show = (value: number | null | undefined, s: Series) => {
    // Nothing spent in a currency in a period is no amount: a dash, as a missing value, not "HUF 0" beside the real ones; a trend line at 0 is a 0.
    if (value == null || (perCurrency(s.unit) && value === 0 && s.role !== 'LINEAR' && s.role !== 'MOVING_AVERAGE')) return t('common.none')
    switch (s.unit) {
      case 'CURRENCY': return format.money(value, s.currency ?? '')
      case 'COST_PER_DISTANCE': return format.costPerDistance(value, s.currency ?? '', units.distance)
      case 'PRICE_PER_VOLUME': return format.pricePerUnit(value, s.currency ?? '', units.volume)
      case 'VOLUME': return format.volume(value, units.volume)
      case 'DISTANCE': return format.distance(value, units.distance)
      case 'CONSUMPTION': return format.consumption(value, units)
      default: return format.number(value)
    }
  }
  const keys = [...new Set(data.series.flatMap((s) => s.points.map((p) => p.key)))]
  const rows: Record<string, string | number | null>[] = keys.map((key) => ({ key, label: keyLabel(key), ...Object.fromEntries(data.series.map((s) => [seriesId(s), s.points.find((p) => p.key === key)?.value ?? null])) }))
  return { keyLabel, baseName, name, show, keys, rows }
}
