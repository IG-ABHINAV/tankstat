import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Drawer from '@mui/material/Drawer'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { X } from 'lucide-react'
import { motion } from 'motion/react'
import { lazy, Suspense, useId, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, Route, Routes, useLocation, useSearchParams } from 'react-router'
import { IconAction } from './components/IconAction.tsx'
import { useMediaQuery } from './hooks/useMediaQuery.ts'
import { SessionDocument, type AuthMode, type SessionQuery } from './gql/generated.ts'
import { ErrorBoundary } from './ErrorBoundary.tsx'
import { LoginView } from './LoginView.tsx'
import { ErrorMessage } from './messages.tsx'
import { NoticeBanner } from './NoticeBanner.tsx'
import { useConnectivity } from './offline/useConnectivity.ts'
import { useTakeoverHeld } from './setup/takeoverHold.ts'
import { PasswordChangeRequiredView, ResetPasswordView } from './PasswordForms.tsx'
import { HealthFooter } from './shell/HealthFooter.tsx'
import { UiSettingsProvider } from './settings/UiSettingsProvider.tsx'
import { useUiSettings } from './settings/uiSettingsContext.ts'
import { NavList } from './shell/NavList.tsx'
import { DrawerSettings, TopBar } from './shell/TopBar.tsx'
import { OfflineReadyNotice } from './shell/OfflineReadyNotice.tsx'
import { UpdateNotice } from './shell/UpdateNotice.tsx'
import { Loading } from './components/Loading.tsx'
import { glass } from './theme/components.ts'
import { MEDIA } from './theme/media.ts'

// Every page is its own chunk, loaded when it is first visited: the first screen only needs the shell and the page it opens on.
const WelcomePage = lazy(() => import('./pages/WelcomePage.tsx').then((m) => ({ default: m.WelcomePage })))
const VehiclesPage = lazy(() => import('./pages/VehiclesPage.tsx').then((m) => ({ default: m.VehiclesPage })))
const VehiclePage = lazy(() => import('./pages/VehiclePage.tsx').then((m) => ({ default: m.VehiclePage })))
const TrashPage = lazy(() => import('./pages/TrashPage.tsx').then((m) => ({ default: m.TrashPage })))
const SyncPage = lazy(() => import('./pages/SyncPage.tsx').then((m) => ({ default: m.SyncPage })))
const ImportPage = lazy(() => import('./pages/ImportPage.tsx').then((m) => ({ default: m.ImportPage })))
const NotificationsPage = lazy(() => import('./pages/NotificationsPage.tsx').then((m) => ({ default: m.NotificationsPage })))
const AccountPage = lazy(() => import('./pages/AccountPage.tsx').then((m) => ({ default: m.AccountPage })))
const AdminPanel = lazy(() => import('./admin/AdminPanel.tsx').then((m) => ({ default: m.AdminPanel })))
const SystemSettingsPage = lazy(() => import('./admin/SystemSettingsPage.tsx').then((m) => ({ default: m.SystemSettingsPage })))
const AdminOverviewPage = lazy(() => import('./admin/AdminOverviewPage.tsx').then((m) => ({ default: m.AdminOverviewPage })))
const BackupsPage = lazy(() => import('./admin/BackupsPage.tsx').then((m) => ({ default: m.BackupsPage })))
const SetupPage = lazy(() => import('./setup/SetupPage.tsx').then((m) => ({ default: m.SetupPage })))

/**
 * While a new instance waits for its setup, whoever reaches it in Standalone (nobody can sign in yet) or with sign-in off gets the setup
 * page wherever they go; with a provider (OIDC, proxy) a visitor signs in first and finds the setup at `/setup`, the notice links to it.
 * Not while the server is out of reach: the session is then the device's last answer, maybe from before the setup was done elsewhere, and
 * the setup page could only say that it needs the server, where the device's own data still works. Except while the setup page waits for
 * a restart it asked for (`takeoverHold.ts`): the server is away on purpose then, and the page goes on once it is back.
 */
function useSetupTakesOver(data: SessionQuery | undefined) {
  const { reachable } = useConnectivity()
  const held = useTakeoverHeld()
  return (
    (reachable || held) &&
    data !== undefined &&
    data.notices.some((n) => n.code === 'SETUP_PENDING') &&
    (data.session.mode === 'NONE' || (data.session.mode === 'STANDALONE' && !data.session.user))
  )
}

/**
 * The notices above the page. The setup's own is left out where the setup page is what is shown (in place of every page, or at `/setup`
 * once past the sign-in), since it would point at itself; and while the setup waits there, so is the warning of sign-in off: the shipped
 * mode is not chosen yet, the wizard asks for one, and its last step says what sign-in off means. The address is read here, so a
 * navigation redraws this, not the whole shell.
 */
function Notices({ data }: { data: SessionQuery }) {
  const { pathname } = useLocation()
  const setupShown = useSetupTakesOver(data) || (pathname === '/setup' && (data.session.mode === 'NONE' || data.session.user !== null))
  const waits = data.notices.some((n) => n.code === 'SETUP_PENDING')
  const hidden = setupShown ? (waits ? ['SETUP_PENDING', 'AUTH_DISABLED'] : ['SETUP_PENDING']) : []
  return <NoticeBanner notices={data.notices.filter((n) => !hidden.includes(n.code))} />
}

/** Another address for a page, its query string kept (a page or a sort someone saved a link to). */
function KeepQuery({ to }: { to: string }) {
  const { search } = useLocation()
  return <Navigate to={`${to}${search}`} replace />
}

/**
 * The administration's first page. The old single page kept its tabs in `?tab=`: those links lead to where the tab went (the users and the
 * access to People), the overview otherwise.
 */
function AdminHome() {
  const [params] = useSearchParams()
  const tab = params.get('tab')
  if (tab === 'access') return <Navigate to="/admin/people?tab=access" replace />
  if (tab === 'users') return <Navigate to="/admin/people" replace />
  return <AdminOverviewPage />
}

export default function App() {
  const { data, error, loading } = useQuery(SessionDocument)
  const mode = data?.session.mode ?? 'NONE'
  const user = data?.session.user ?? null
  // No menu while the setup page stands in for every page (none of them is there yet), nor while the change of a password asked at
  // sign-in stands in for them.
  const takenOver = useSetupTakesOver(data)
  const signedIn = data !== undefined && (mode === 'NONE' || user !== null) && !takenOver
  const showMenu = signedIn && !user?.passwordChangeRequired

  // The UI settings follow the user: asked from the server once per session, kept per user (the key starts afresh on sign-in and sign-out),
  // the change asked at sign-in included (its page speaks the account's language, and what is chosen in the top bar meanwhile is kept).
  return (
    <UiSettingsProvider key={user?.id ?? mode} enabled={signedIn}>
      <Shell data={data} error={error} loading={loading} mode={mode} user={user} showMenu={showMenu} />
    </UiSettingsProvider>
  )
}

function Shell({
  data,
  error,
  loading,
  mode,
  user,
  showMenu,
}: {
  data: SessionQuery | undefined
  error: unknown
  loading: boolean
  mode: AuthMode
  user: SessionQuery['session']['user']
  showMenu: boolean
}) {
  const { t } = useTranslation()
  const drawerTitle = useId()
  // Desktop: a docked sidebar, open by default; hiding it is remembered (in this browser, and with the account once signed in).
  // Phone: an overlay drawer, closed until the menu button is pressed, never remembered.
  const desktop = useMediaQuery(MEDIA.desktop, true)
  const settings = useUiSettings()!
  const dockedOpen = settings.navOpen
  const [drawerOpen, setDrawerOpen] = useState(false)
  const navOpen = desktop ? dockedOpen : drawerOpen
  const toggleNav = () => (desktop ? settings.setNavOpen(!dockedOpen) : setDrawerOpen(!drawerOpen))

  return (
    <Stack sx={{ minHeight: '100vh' }}>
      <SkipLink />
      <TopBar user={user} showMenu={showMenu} navOpen={navOpen} onToggleNav={toggleNav} />
      <Box sx={{ display: 'flex', flexGrow: 1, alignItems: 'stretch', minWidth: 0 }}>
        {showMenu && desktop && dockedOpen && (
          <Box
            id="app-nav"
            sx={(theme) => ({
              ...glass(theme),
              boxShadow: theme.shadows[4],
              borderRight: `1px solid ${theme.vars.palette.neutral.softHover}`,
              p: 1.5,
              width: '16rem',
              flexShrink: 0,
            })}
          >
            <NavList mode={mode} user={user} />
          </Box>
        )}
        {showMenu && !desktop && (
          <Drawer
            open={drawerOpen}
            onClose={() => setDrawerOpen(false)}
            slotProps={{
              paper: {
                id: 'app-nav',
                role: 'dialog',
                'aria-modal': true,
                'aria-labelledby': drawerTitle,
                // Installed on a phone the drawer runs edge to edge: its content keeps clear of the notch and the home indicator.
                sx: (theme) => ({
                  ...glass(theme),
                  width: 'min(20rem, 85vw)',
                  pt: 'calc(20px + env(safe-area-inset-top, 0px))',
                  pb: 'calc(24px + env(safe-area-inset-bottom, 0px))',
                  pl: 'calc(24px + env(safe-area-inset-left, 0px))',
                  pr: 3,
                }),
              },
            }}
          >
            <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 1.5 }}>
              <Typography id={drawerTitle} component="h2" variant="h5">
                {t('app.name')}
              </Typography>
              {/* A ghost button reaches into the padding, so the row is only as tall as the title. */}
              <IconAction label={t('nav.close')} variant="ghost" size="large" sx={{ m: -1 }} onClick={() => setDrawerOpen(false)}>
                <X size={20} aria-hidden />
              </IconAction>
            </Stack>
            <NavList mode={mode} user={user} onNavigate={() => setDrawerOpen(false)} />
            <DrawerSettings />
          </Drawer>
        )}
        {/* The page column (at most 1136 px, centred), clear of a phone's rounded corners in landscape. */}
        <Box
          component="main"
          id="main"
          tabIndex={-1}
          sx={{
            flexGrow: 1,
            minWidth: 0,
            py: { xs: 1.5, md: 2 },
            pl: { xs: 'max(12px, env(safe-area-inset-left, 0px))', md: 'max(16px, env(safe-area-inset-left, 0px))' },
            pr: { xs: 'max(12px, env(safe-area-inset-right, 0px))', md: 'max(16px, env(safe-area-inset-right, 0px))' },
          }}
        >
          <Box sx={{ maxWidth: 1136, mx: 'auto' }}>
            {error !== undefined && <ErrorMessage error={error} />}
            {loading && <Loading />}
            <UpdateNotice />
            <OfflineReadyNotice />
            {data && <Notices data={data} />}
            {data && <Content data={data} />}
          </Box>
        </Box>
      </Box>
      <HealthFooter />
    </Stack>
  )
}

/** "Skip to main content": out of sight until it has the keyboard focus, then at the top left, clear of a notch. */
function SkipLink() {
  const { t } = useTranslation()
  return (
    <Box
      component="a"
      href="#main"
      sx={(theme) => ({
        ...theme.typography.body2,
        position: 'absolute',
        left: 12,
        top: -100,
        zIndex: theme.zIndex.tooltip,
        px: 1.5,
        py: 1,
        borderRadius: '9px',
        backgroundColor: theme.vars.palette.primary.solid,
        color: theme.vars.palette.primary.contrastText,
        '&:focus': { top: 'calc(12px + env(safe-area-inset-top, 0px))' },
      })}
    >
      {t('nav.skip')}
    </Box>
  )
}

function Content({ data }: { data: SessionQuery }) {
  const { t } = useTranslation()
  const { pathname } = useLocation()
  const [params] = useSearchParams()
  const resetToken = params.get('resetToken')
  const { mode, user } = data.session
  const isAdmin = user?.isAdmin === true // the full vehicle list and the administration are for administrators only
  const takenOver = useSetupTakesOver(data)

  if (takenOver)
    return (
      <ErrorBoundary>
        <Suspense fallback={<Loading />}>
          <SetupPage mode={mode} />
        </Suspense>
      </ErrorBoundary>
    )
  if (mode === 'STANDALONE' && resetToken && !user) return <ResetPasswordView token={resetToken} />
  if (mode !== 'NONE' && !user) return <LoginView mode={mode} />
  if (user?.passwordChangeRequired) return <PasswordChangeRequiredView />

  return (
    // A short fade/slide-in on every page change (honours the user's reduced-motion setting).
    // The key makes the boundary start fresh with every page, so one that crashed does not hold the next one hostage.
    <motion.div key={pathname} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.2 }}>
      <ErrorBoundary>
        <Suspense
          fallback={
            <Loading />
          }
        >
          <Routes>
            <Route path="/" element={<WelcomePage isAdmin={isAdmin} />} />
            {/* The list of every vehicle moved into the administration; the old address still leads there, its query (page, sort) kept. */}
            <Route path="/vehicles" element={<KeepQuery to="/admin/vehicles" />} />
            <Route path="/vehicles/:id/*" element={<VehiclePage isAdmin={isAdmin} />} />
            <Route path="/import" element={<ImportPage />} />
            <Route path="/trash" element={<TrashPage />} />
            <Route path="/sync" element={<SyncPage />} />
            <Route path="/notifications" element={<NotificationsPage />} />
            <Route path="/account" element={<AccountPage mode={mode} user={user} />} />
            <Route path="/admin" element={isAdmin ? <AdminHome /> : <Navigate to="/" replace />} />
            <Route path="/admin/people" element={isAdmin ? <AdminPanel /> : <Navigate to="/" replace />} />
            <Route path="/admin/vehicles" element={isAdmin ? <VehiclesPage /> : <Navigate to="/" replace />} />
            <Route path="/admin/settings" element={isAdmin ? <SystemSettingsPage /> : <Navigate to="/" replace />} />
            <Route path="/admin/backups" element={isAdmin ? <BackupsPage /> : <Navigate to="/" replace />} />
            <Route path="/setup" element={<SetupPage mode={mode} />} />
            <Route path="*" element={<Typography>{t('app.notFound')}</Typography>} />
          </Routes>
        </Suspense>
      </ErrorBoundary>
    </motion.div>
  )
}
