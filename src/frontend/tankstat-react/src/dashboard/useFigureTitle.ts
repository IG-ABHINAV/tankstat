import { useTranslation } from 'react-i18next'
import type { ChartMetric } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { periodFigureOf, templateOf, type FigureRecipe } from './figureRecipe.ts'
import { periodWords } from './trendPeriod.ts'

/**
 * A key figure's title (#274): its own when it has one, else its template's, translated (`dashboard.kpi.<id>`: a mapped *Spent this month*
 * reads in the viewer's language), else its measure and period ("Fuel volume · Last 30 days"), as #275's builder names it.
 */
export function useFigureTitle() {
  const { t } = useTranslation()
  const format = useFormat()
  return (r: FigureRecipe, title?: string | null): string => {
    if (title) return title
    const template = templateOf(r)
    if (template) return t(`dashboard.kpi.${template}`)
    const measure = r.measure === 'ODOMETER' ? t('dashboard.kpi.odometer') : r.measure === 'LAST_FILL_UP' ? t('dashboard.kpi.lastFillUp') : t(`charts.metrics.${r.measure as ChartMetric}`)
    const figure = periodFigureOf(r)
    if (!r.period || !figure) return measure
    // The period's words alone: what it is compared with is the chip's and the sentence's to say.
    const { period } = periodWords(figure, r.period, format.date)
    return t('dashboard.figure.title', { measure, period: String(t(period.key as never, period.values)) })
  }
}
