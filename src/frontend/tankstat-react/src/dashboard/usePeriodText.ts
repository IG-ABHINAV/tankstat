import { useTranslation } from 'react-i18next'
import { useFormat } from '../i18n/format.ts'
import { periodWords, type TrendFigureId, type TrendPeriod, type Words } from './trendPeriod.ts'

/** The chip's words for a period: "12 full months", "Last 8 weeks · vs a year earlier". */
export function usePeriodText() {
  const { t } = useTranslation()
  const format = useFormat()
  const say = (w: Words) => String(t(w.key as never, w.values))
  return (figure: TrendFigureId, p: TrendPeriod) => {
    const { period, against } = periodWords(figure, p, format.date)
    return against ? String(t('dashboard.period.withAgainst', { period: say(period), against: say(against) })) : say(period)
  }
}
