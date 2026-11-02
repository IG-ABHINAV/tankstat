import { useMutation, useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Link from '@mui/material/Link'
import Skeleton from '@mui/material/Skeleton'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Pencil, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../components/ConfirmDialog.tsx'
import { IconAction } from '../components/IconAction.tsx'
import { UserChip } from '../components/UserAvatar.tsx'
import { visuallyHidden } from '../components/visuallyHidden.ts'
import { DeleteChartDocument, ResetDashboardLayoutDocument, SessionDocument, VehicleDashboardDocument, type AccessLevel, type DistanceUnit, type VehicleDashboardQuery, type VolumeUnit } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { ChartBuilderDialog } from './ChartBuilderDialog.tsx'
import { ChartCard } from './ChartCard.tsx'
import { type ChartRecipe } from './chartFormat.ts'
import { CustomizeDashboardDialog } from './CustomizeDashboardDialog.tsx'
import { Figure } from './Figures.tsx'
import { FIGURE_GRID, resolveLayout } from './widgets.ts'

type Units = { distance: DistanceUnit; volume: VolumeUnit }
type SavedChart = VehicleDashboardQuery['vehicleCharts'][number]

/** The charts: two side by side on a desktop. */
const chartGrid = { display: 'grid', gridTemplateColumns: { xs: 'minmax(0, 1fr)', lg: 'repeat(2, minmax(0, 1fr))' }, gap: 2 } as const

/** While the figures load: their outlines, and "Loading…" for a screen reader. */
function DashboardSkeleton() {
  const { t } = useTranslation()
  return (
    <>
      <span role="status" style={visuallyHidden}>
        {t('app.loading')}
      </span>
      <Box aria-hidden className="tk-delayed" sx={FIGURE_GRID}>
        {[0, 1, 2, 3].map((i) => (
          <Skeleton key={i} variant="rounded" height={112} sx={{ borderRadius: '12px' }} />
        ))}
      </Box>
    </>
  )
}

const recipeOf = (c: SavedChart): ChartRecipe => ({
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
})

/**
 * The first screen of a vehicle: its key figures and its charts, ready-made and saved, as its dashboard is arranged (#211): the user's own
 * arrangement, else the vehicle's, else the Standard preset (`widgets.ts`). A saved chart the arrangement does not mention comes at its end.
 */
export function DashboardPanel({ vehicle }: { vehicle: { id: string; units: Units; logAccess: AccessLevel } }) {
  const { t } = useTranslation()
  const { data, error, refetch } = useQuery(VehicleDashboardDocument, { variables: { id: vehicle.id }, fetchPolicy: 'cache-and-network' })
  // With sign-in off everyone is the same anonymous user: an arrangement of one's own would be everyone's.
  const signInOff = useQuery(SessionDocument, { fetchPolicy: 'cache-only' }).data?.session.mode === 'NONE'
  const [remove] = useMutation(DeleteChartDocument)
  const [reset] = useMutation(ResetDashboardLayoutDocument)
  const [actionError, setActionError] = useState<unknown>()
  const canShare = vehicle.logAccess === 'EDIT' || vehicle.logAccess === 'DELETE'

  if (error) return <ErrorMessage error={error} />
  if (!data) return <DashboardSkeleton />

  const s = data.vehicle?.summary
  const dashboard = data.vehicle?.dashboard
  const personal = dashboard?.personal ?? null
  const layout = resolveLayout(personal ?? dashboard?.shared ?? null, data.vehicleCharts)

  async function act(work: () => Promise<unknown>) {
    setActionError(undefined)
    try {
      await work()
      await refetch()
    } catch (e) {
      setActionError(e)
    }
  }

  const savedCard = (c: SavedChart) => {
    const r = recipeOf(c)
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
                onConfirm={() => void act(() => remove({ variables: { id: c.id } }))}
              />
            </Stack>
          ) : undefined
        }
      />
    )
  }

  return (
    <Stack sx={{ gap: 3 }}>
      {layout.figures.length > 0 && (
        <section aria-label={t('dashboard.kpi.title')}>
          <Box sx={FIGURE_GRID}>
            {layout.figures.map((id) => (
              <Figure key={id} id={id} summary={s} units={vehicle.units} />
            ))}
          </Box>
        </section>
      )}

      <section aria-labelledby="dashboard-charts">
        <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', gap: 1.5, flexWrap: 'wrap', mb: 1.5 }}>
          <Typography component="h2" variant="h5" id="dashboard-charts">
            {t('dashboard.yourCharts')}
          </Typography>
          <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
            {/* Null for a vehicle added on this device: the Standard preset until the server has it. */}
            {dashboard && (dashboard.canChangeShared || !signInOff) && (
              <CustomizeDashboardDialog
                vehicleId={vehicle.id}
                shared={dashboard.shared ?? null}
                personal={personal}
                canChangeShared={dashboard.canChangeShared}
                allowPersonal={!signInOff}
                savedCharts={data.vehicleCharts}
                onSaved={() => refetch()}
              />
            )}
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
        </Stack>
        {personal && !signInOff && (
          <Typography variant="body2" sx={{ color: 'text.secondary', mb: 1.5 }}>
            {t('dashboard.customize.yours')}{' '}
            <Link component="button" type="button" variant="body2" underline="always" onClick={() => void act(() => reset({ variables: { vehicleId: vehicle.id, personal: true } }))}>
              {t('dashboard.customize.resetPersonal')}
            </Link>
          </Typography>
        )}
        {actionError !== undefined && <ErrorMessage error={actionError} />}
        {layout.charts.length > 0 && (
          <Box sx={chartGrid}>
            {layout.charts.map((item) =>
              item.kind === 'ready' ? (
                <ChartCard key={item.key} vehicleId={vehicle.id} title={t(`dashboard.presets.${item.chart.id}`)} recipe={item.chart.recipe} units={vehicle.units} />
              ) : (
                savedCard(item.chart)
              ),
            )}
          </Box>
        )}
        {layout.charts.length === 0 && data.vehicleCharts.length > 0 && (
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {t('dashboard.customize.noCharts')}
          </Typography>
        )}
        {data.vehicleCharts.length === 0 && (
          <Typography variant="body2" sx={{ color: 'text.secondary', mt: layout.charts.length > 0 ? 1.5 : 0 }}>
            {t('dashboard.noCharts')}
          </Typography>
        )}
      </section>
    </Stack>
  )
}
