import { useMutation, useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Card from '@mui/material/Card'
import Skeleton from '@mui/material/Skeleton'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Pencil, Plus, Trash2 } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../components/ConfirmDialog.tsx'
import { IconAction } from '../components/IconAction.tsx'
import { UserChip } from '../components/UserAvatar.tsx'
import { visuallyHidden } from '../components/visuallyHidden.ts'
import { DeleteChartDocument, VehicleDashboardDocument, type AccessLevel, type DistanceUnit, type VolumeUnit } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { ErrorMessage } from '../messages.tsx'
import { spentText } from '../spending.ts'
import { ChartBuilderDialog } from './ChartBuilderDialog.tsx'
import { ChartCard } from './ChartCard.tsx'
import { LazySparkline } from './LazySparkline.tsx'
import { NEW_RECIPE, type ChartRecipe } from './chartFormat.ts'

type Units = { distance: DistanceUnit; volume: VolumeUnit }

const recipe = (over: Partial<ChartRecipe>): ChartRecipe => ({ ...NEW_RECIPE, ...over })

/** Ready-made charts every vehicle gets (they are not stored; the user's own charts come below them). */
const PRESETS: { id: string; title: 'monthlyCosts' | 'consumption' | 'fuelPrice' | 'categories' | 'distance'; recipe: ChartRecipe }[] = [
  { id: 'monthlyCosts', title: 'monthlyCosts', recipe: recipe({ metric: 'TOTAL_SPEND', kind: 'BAR', stacked: true }) },
  { id: 'consumption', title: 'consumption', recipe: recipe({ metric: 'AVERAGE_CONSUMPTION', kind: 'LINE' }) },
  { id: 'fuelPrice', title: 'fuelPrice', recipe: recipe({ metric: 'AVERAGE_PRICE_PER_UNIT', kind: 'LINE' }) },
  { id: 'categories', title: 'categories', recipe: recipe({ metric: 'EXPENSE_COST', grouping: 'CATEGORY', kind: 'DONUT' }) },
  { id: 'distance', title: 'distance', recipe: recipe({ metric: 'DISTANCE', kind: 'BAR' }) },
]

/** The key figures: one column on a phone, two from a small tablet, four on a desktop. */
const kpiGrid = { display: 'grid', gridTemplateColumns: { xs: 'minmax(0, 1fr)', sm: 'repeat(2, minmax(0, 1fr))', lg: 'repeat(4, minmax(0, 1fr))' }, gap: 1.5 } as const
/** The charts: two side by side on a desktop. */
const chartGrid = { display: 'grid', gridTemplateColumns: { xs: 'minmax(0, 1fr)', lg: 'repeat(2, minmax(0, 1fr))' }, gap: 2 } as const

function Kpi({ label, value, hint, children }: { label: string; value: string; hint?: string; children?: ReactNode }) {
  return (
    <Card sx={{ p: 2 }}>
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {label}
      </Typography>
      <Typography variant="h3" component="p" sx={{ my: 0.5 }}>
        {value}
      </Typography>
      {hint && (
        <Typography variant="caption" component="p" sx={{ color: 'text.secondary' }}>
          {hint}
        </Typography>
      )}
      {children}
    </Card>
  )
}

/** While the figures load: their outlines, and "Loading…" for a screen reader. */
function DashboardSkeleton() {
  const { t } = useTranslation()
  return (
    <>
      <span role="status" style={visuallyHidden}>
        {t('app.loading')}
      </span>
      <Box aria-hidden className="tk-delayed" sx={kpiGrid}>
        {[0, 1, 2, 3].map((i) => (
          <Skeleton key={i} variant="rounded" height={112} sx={{ borderRadius: '12px' }} />
        ))}
      </Box>
    </>
  )
}

/** The first screen of a vehicle: key figures, ready-made charts, and the charts the user (or others) composed. */
export function DashboardPanel({ vehicle }: { vehicle: { id: string; units: Units; logAccess: AccessLevel } }) {
  const { t } = useTranslation()
  const format = useFormat()
  const { data, error, refetch } = useQuery(VehicleDashboardDocument, { variables: { id: vehicle.id }, fetchPolicy: 'cache-and-network' })
  const [remove] = useMutation(DeleteChartDocument)
  const [actionError, setActionError] = useState<unknown>()
  const none = t('dashboard.kpi.none')
  const canShare = vehicle.logAccess === 'EDIT' || vehicle.logAccess === 'DELETE'

  if (error) return <ErrorMessage error={error} />
  if (!data) return <DashboardSkeleton />

  const s = data.vehicle?.summary
  async function deleteChart(id: string) {
    setActionError(undefined)
    try {
      await remove({ variables: { id } })
      await refetch()
    } catch (e) {
      setActionError(e)
    }
  }

  return (
    <Stack sx={{ gap: 3 }}>
      <section aria-label={t('dashboard.kpi.title')}>
        <Box sx={kpiGrid}>
          <Kpi
            label={t('dashboard.kpi.thisMonth')}
            value={(s && spentText(s.spending, 'thisMonth', format)) ?? none}
            hint={s && s.spending.length > 0 ? t('dashboard.kpi.lastMonth', { amount: spentText(s.spending, 'lastMonth', format) }) : undefined}
          >
            {s && s.currency && <LazySparkline points={s.spendTrend} currency={s.currency} />}
          </Kpi>
          <Kpi label={t('dashboard.kpi.consumption')} value={s?.averageConsumption != null ? format.consumption(s.averageConsumption, vehicle.units) : none} hint={t('dashboard.kpi.consumptionHint')} />
          <Kpi label={t('dashboard.kpi.odometer')} value={s?.latestOdometer != null ? format.distance(s.latestOdometer, vehicle.units.distance) : none} />
          <Kpi
            label={t('dashboard.kpi.lastFillUp')}
            value={s?.lastFillUpDate ? format.date(s.lastFillUpDate) : none}
            hint={s ? t('dashboard.kpi.counts', { fuel: s.fillUpCount, expenses: s.expenseCount }) : undefined}
          />
        </Box>
      </section>

      <section aria-labelledby="overview-charts">
        <Typography component="h2" variant="h5" id="overview-charts" sx={{ mb: 1.5 }}>
          {t('dashboard.overview')}
        </Typography>
        <Box sx={chartGrid}>
          {PRESETS.map((p) => (
            <ChartCard key={p.id} vehicleId={vehicle.id} title={t(`dashboard.presets.${p.title}`)} recipe={p.recipe} units={vehicle.units} />
          ))}
        </Box>
      </section>

      <section aria-labelledby="your-charts">
        <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', gap: 1.5, flexWrap: 'wrap', mb: 1.5 }}>
          <Typography component="h2" variant="h5" id="your-charts">
            {t('dashboard.yourCharts')}
          </Typography>
          <ChartBuilderDialog
            vehicle={vehicle}
            canShare={canShare}
            onSaved={() => refetch()}
            trigger={
              <Button size="large">
                <Plus size={16} aria-hidden />
                {t('dashboard.addChart')}
              </Button>
            }
          />
        </Stack>
        {actionError !== undefined && <ErrorMessage error={actionError} />}
        {data.vehicleCharts.length === 0 ? (
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {t('dashboard.noCharts')}
          </Typography>
        ) : (
          <Box sx={chartGrid}>
            {data.vehicleCharts.map((c) => {
              const r: ChartRecipe = {
                metric: c.metric,
                grouping: c.grouping,
                kind: c.kind,
                range: c.range,
                stacked: c.stacked,
                from: c.rangeFrom ?? null,
                to: c.rangeTo ?? null,
                trend: c.trend,
                runningTotal: c.runningTotal,
                compareYearEarlier: c.compareYearEarlier,
                secondMetric: c.secondMetric ?? null,
                secondKind: c.secondKind ?? null,
              }
              return (
                <ChartCard
                  key={c.id}
                  vehicleId={vehicle.id}
                  title={c.title}
                  recipe={r}
                  units={vehicle.units}
                  shared={c.isShared}
                  meta={c.createdBy && c.isShared ? <UserChip user={c.createdBy} /> : undefined}
                  actions={
                    c.canEdit ? (
                      <Stack direction="row" sx={{ gap: 1 }}>
                        <ChartBuilderDialog
                          vehicle={vehicle}
                          canShare={canShare}
                          chart={{ id: c.id, title: c.title, isShared: c.isShared, recipe: r }}
                          onSaved={() => refetch()}
                          trigger={
                            <IconAction size="large" tone="primary" label={t('dashboard.editChart', { title: c.title })}>
                              <Pencil size={16} aria-hidden />
                            </IconAction>
                          }
                        />
                        <ConfirmDialog
                          trigger={
                            <IconAction size="large" tone="error" label={t('dashboard.deleteChart', { title: c.title })}>
                              <Trash2 size={16} aria-hidden />
                            </IconAction>
                          }
                          title={t('dashboard.deleteTitle')}
                          description={t('dashboard.deleteDescription', { title: c.title })}
                          confirmLabel={t('dashboard.deleteConfirm')}
                          onConfirm={() => void deleteChart(c.id)}
                        />
                      </Stack>
                    ) : undefined
                  }
                />
              )
            })}
          </Box>
        )}
      </section>
    </Stack>
  )
}
