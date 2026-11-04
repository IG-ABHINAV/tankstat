import Box from '@mui/material/Box'
import Skeleton from '@mui/material/Skeleton'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { visuallyHidden } from '../components/visuallyHidden.ts'
import type { DistanceUnit, VehicleTrendQuery, VolumeUnit } from '../gql/generated.ts'
import { useErrorText } from '../i18n/errors.ts'
import { useFormat } from '../i18n/format.ts'
import { Change, Kpi } from './Kpi.tsx'
import { comparedWith, namesFillUps, type TrendFigureId, type TrendPeriod } from './trendPeriod.ts'

type Units = { distance: DistanceUnit; volume: VolumeUnit }
export type TrendData = VehicleTrendQuery['vehicleTrend']

/**
 * The figure drawn from an answer: also the popover's and the Add widget dialog's preview of a period being chosen. Until there is one
 * (`data` null) the same card waits with its label and chip, or says why it has none (`error`): one card either way, so its chip stays the
 * same element, and a popover opened from it keeps its anchor when the answer comes.
 */
export function TrendFigure({ figure, period: p, data, error, rows, units, label, chip }: { figure: TrendFigureId; period: TrendPeriod; data: TrendData | null | undefined; error?: unknown; rows: number; units: Units; label: string; chip?: ReactNode }) {
  const { t } = useTranslation()
  const format = useFormat()
  const errorText = useErrorText()
  const none = t('dashboard.kpi.none')
  if (!data)
    return (
      <Kpi
        rows={rows}
        label={label}
        chip={chip}
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
  const than = (() => {
    const w = comparedWith(p, data.earlier)
    return String(t(w.key as never, w.values))
  })()
  switch (figure) {
    case 'spendingTrend': {
      const main = data.currencies[0]
      const others = data.currencies.slice(1).filter((c) => c.recent)
      return (
        <Kpi
          rows={rows}
          label={label}
          chip={chip}
          value={main ? format.money(main.recent ?? 0, main.currency) : none}
          hint={others.length > 0 ? others.map((c) => format.money(c.recent ?? 0, c.currency)).join(' · ') : undefined}
          change={main && <Change recent={main.recent} earlier={main.earlier} than={than} meaning="worseUp" size={(v) => format.money(v, main.currency)} none={t('dashboard.trend.nothingBefore')} />}
        />
      )
    }
    case 'costPerDistance': {
      const main = data.currencies[0]
      return (
        <Kpi
          rows={rows}
          label={label}
          chip={chip}
          value={main?.recent != null ? format.costPerDistance(main.recent, main.currency, units.distance) : none}
          change={main && <Change recent={main.recent} earlier={main.earlier} than={than} meaning="worseUp" size={(v) => format.costPerDistance(v, main.currency, units.distance)} none={t('dashboard.trend.nothingBefore')} noneRecent={t('dashboard.trend.nothingDriven')} />}
        />
      )
    }
    case 'consumptionTrend': {
      const c = data.consumption
      const fillUps = namesFillUps(p)
      // Fuel per distance in both windows (the stored figure), whatever unit the words show it in: more is more fuel, in mpg too.
      return (
        <Kpi
          rows={rows}
          label={label}
          chip={chip}
          value={c?.recent != null ? format.consumption(c.recent, units) : none}
          hint={c ? (fillUps ? t('dashboard.kpi.fillUpsIn', { count: c.recentFillUps }) : t('dashboard.kpi.lastFillUps', { count: c.recentFillUps })) : undefined}
          change={
            <Change
              recent={c?.recent}
              earlier={c?.earlier}
              fuel
              than={than}
              meaning="worseUp"
              wrap={fillUps && c ? (sentence) => t('dashboard.trend.fillUpCounts', { sentence, recent: c.recentFillUps, earlier: c.earlierFillUps }) : undefined}
              none={t('dashboard.trend.tooFew')}
            />
          }
        />
      )
    }
    case 'distanceTrend': {
      const d = data.distance
      return (
        <Kpi
          rows={rows}
          label={label}
          chip={chip}
          value={d?.recent != null ? format.distance(d.recent, units.distance) : none}
          change={d && <Change recent={d.recent} earlier={d.earlier} than={than} meaning="neutral" size={(v) => format.distance(v, units.distance)} none={t('dashboard.trend.nothingBefore')} />}
        />
      )
    }
  }
}
