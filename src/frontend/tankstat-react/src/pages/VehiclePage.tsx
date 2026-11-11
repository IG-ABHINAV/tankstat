import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Link from '@mui/material/Link'
import Typography from '@mui/material/Typography'
import { ArrowLeft } from 'lucide-react'
import { lazy, Suspense } from 'react'
import { useTranslation } from 'react-i18next'
import { Link as RouterLink, useParams, useSearchParams } from 'react-router'
import { Loading } from '../components/Loading.tsx'
import { TabbedPanels } from '../components/TabbedPanels.tsx'
import { VehicleHero } from '../components/VehicleHero.tsx'
import { VehicleDetailsDocument } from '../gql/generated.ts'
import { useMediaQuery } from '../hooks/useMediaQuery.ts'
import { usePageTitle } from '../hooks/usePageTitle.ts'
import { ErrorMessage } from '../messages.tsx'
import { MEDIA } from '../theme/media.ts'
import { canLogFor } from '../vehicles.ts'
import { DetailsPanel } from './vehicle/DetailsPanel.tsx'
import { LogFab } from './vehicle/LogFab.tsx'
import { RecurringPanel } from './vehicle/RecurringPanel.tsx'
import { SharingPanel } from './vehicle/SharingPanel.tsx'

// The dashboard brings the chart library, the two log tabs the data grid: each is loaded with its tab instead of with the page shell.
const DashboardPanel = lazy(() => import('../dashboard/DashboardPanel.tsx').then((m) => ({ default: m.DashboardPanel })))
const RefuelingsPanel = lazy(() => import('./vehicle/RefuelingsPanel.tsx').then((m) => ({ default: m.RefuelingsPanel })))
const ExpensesPanel = lazy(() => import('./vehicle/ExpensesPanel.tsx').then((m) => ({ default: m.ExpensesPanel })))
const TyresPanel = lazy(() => import('./vehicle/TyresPanel.tsx').then((m) => ({ default: m.TyresPanel })))

const TABS = ['dashboard', 'refuelings', 'expenses', 'recurring', 'tyres', 'details', 'sharing'] as const
type Tab = (typeof TABS)[number]

/**
 * One vehicle: its logs, its details and picture, and who the logs are shared with (tabs; the choice is in the address). On a phone, whoever
 * may add logs also gets a floating add button.
 */
export function VehiclePage({ isAdmin }: { isAdmin: boolean }) {
  const { t } = useTranslation()
  const { id = '' } = useParams()
  const [params, setParams] = useSearchParams()
  const { data, error, refetch } = useQuery(VehicleDetailsDocument, { variables: { id }, fetchPolicy: 'cache-and-network' })
  const vehicle = data?.vehicle
  usePageTitle(vehicle?.name)
  const narrow = useMediaQuery(MEDIA.narrow, false)

  const canLog = vehicle ? canLogFor(vehicle) : false
  const requested = params.get('tab') as Tab | null
  const tab: Tab = requested && TABS.includes(requested) && (requested !== 'sharing' || vehicle?.canEdit) ? requested : 'dashboard'

  const fab = canLog && narrow
  return (
    // With the floating add button, the page ends a little lower, so its last row can scroll out from under the button.
    <Box component="section" aria-labelledby="page-title" sx={{ pb: fab ? 9 : 0 }}>
      <Link component={RouterLink} to={isAdmin ? '/admin/vehicles' : '/'} variant="body2" sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5, minHeight: 44 }}>
        <ArrowLeft size={16} aria-hidden />
        {isAdmin ? t('vehicles.back') : t('vehicles.backHome')}
      </Link>
      {error && <ErrorMessage error={error} />}
      {!data && !error && <Loading />}
      {data && !vehicle && <ErrorMessage>{t('vehicles.notFound')}</ErrorMessage>}
      {vehicle && (
        <>
          <Box sx={{ my: 1.5 }}>
            <VehicleHero id={vehicle.id} name={vehicle.name} plate={vehicle.licensePlate} pictureUrl={vehicle.pictureUrl} owner={vehicle.owner} />
            {!vehicle.canEdit && (
              <Typography variant="caption" component="p" sx={{ color: 'text.secondary', mt: 1 }}>
                {t('vehicles.logAccessNote')}
              </Typography>
            )}
          </Box>
          <TabbedPanels
            label={vehicle.name}
            value={tab}
            onChange={(value) => setParams(value === 'dashboard' ? {} : { tab: value }, { replace: true })}
            tabs={[
              {
                value: 'dashboard',
                label: t('vehicles.tabs.dashboard'),
                content: () => (
                  <Suspense fallback={<Loading />}>
                    <DashboardPanel vehicle={vehicle} />
                  </Suspense>
                ),
              },
              {
                value: 'refuelings',
                label: t('vehicles.tabs.refuelings'),
                content: () => (
                  <Suspense fallback={<Loading />}>
                    <RefuelingsPanel vehicle={vehicle} canLog={canLog} />
                  </Suspense>
                ),
              },
              {
                value: 'expenses',
                label: t('vehicles.tabs.expenses'),
                content: () => (
                  <Suspense fallback={<Loading />}>
                    <ExpensesPanel vehicle={vehicle} canLog={canLog} />
                  </Suspense>
                ),
              },
              { value: 'recurring', label: t('vehicles.tabs.recurring'), content: () => <RecurringPanel vehicle={vehicle} canLog={canLog} /> },
              {
                value: 'tyres',
                label: t('vehicles.tabs.tyres'),
                content: () => (
                  <Suspense fallback={<Loading />}>
                    <TyresPanel vehicle={vehicle} canLog={canLog} />
                  </Suspense>
                ),
              },
              { value: 'details', label: t('vehicles.tabs.details'), content: () => <DetailsPanel vehicle={vehicle} onChanged={() => refetch()} /> },
              ...(vehicle.canEdit ? [{ value: 'sharing' as const, label: t('vehicles.tabs.sharing'), content: () => <SharingPanel vehicleId={vehicle.id} /> }] : []),
            ]}
          />
          {canLog && <LogFab vehicle={vehicle} shown={narrow} />}
        </>
      )}
    </Box>
  )
}
