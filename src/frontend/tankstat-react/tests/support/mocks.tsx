import { ApolloProvider } from '@apollo/client/react'
import { render } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { MotionConfig } from 'motion/react'
import { graphql } from './graphql.ts'
import type { ReactElement } from 'react'
import { MemoryRouter } from 'react-router'
import { onTestFinished, vi } from 'vitest'
import { createApolloClient } from '../../src/apolloClient.ts'
import type { AuthMode, ColorMode, DateFormat, NotificationFieldsFragment, SessionQuery, SurfaceStyle, SystemSettingFieldsFragment, TimeFormat } from '../../src/gql/generated.ts'
import type { GridSaved } from '../../src/settings/types.ts'
import { ThemeRoot } from '../../src/theme/ThemeRoot.tsx'

/** Renders with a fresh Apollo client (and cache) talking to the msw-mocked GraphQL endpoint over HTTP; animations are instant. */
export const renderWithApollo = (ui: ReactElement, route = '/') =>
  render(
    <ApolloProvider client={createApolloClient('http://localhost/graphql')}>
      <ThemeRoot instant>
        <MotionConfig transition={{ duration: 0 }}>
          <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>
        </MotionConfig>
      </ThemeRoot>
    </ApolloProvider>,
  )

/**
 * Silences `console.error` for the rest of the test and gives the spy back: React prints a component that throws, and the error link of
 * the Apollo client prints a failed request, so a test that provokes either on purpose would fill the run with them. The console is
 * restored when the test ends, whether it passed or not.
 */
export function silenceConsoleError() {
  const spy = vi.spyOn(console, 'error').mockImplementation(() => undefined)
  onTestFinished(() => spy.mockRestore())
  return spy
}

type SessionUser = NonNullable<SessionQuery['session']['user']>
type Notice = SessionQuery['notices'][number]

export const user = (over: Partial<SessionUser> = {}): SessionUser => ({
  id: 'u1',
  displayName: 'Alice',
  email: 'alice@example.com',
  isAdmin: false,
  avatarUrl: null,
  passwordChangeRequired: false,
  ...over,
})

export const authWarning: Notice = {
  code: 'AUTH_DISABLED',
  severity: 'WARNING',
  message: 'Authentication is disabled: anyone can see and change all data.',
}

/** Which history the server's data belongs to (`Session.instance`); a device starts afresh when it changes (a restore). */
export const fakeInstance = (generation = 'gen-1', restorePoint: string | null = null) => ({ __typename: 'InstanceInfo', generation, restorePoint })

export const sessionHandler = (mode: AuthMode, current: () => SessionUser | null, notices: Notice[] = [], instance = fakeInstance()) =>
  graphql.query('Session', () => HttpResponse.json({ data: { session: { mode, user: current(), instance }, notices } }))

/** A signed-in administrator: the full vehicle list and the administration are for administrators only. */
export const adminSession = (notices: Notice[] = []) => sessionHandler('STANDALONE', () => user({ isAdmin: true }), notices)

export const healthHandler = graphql.query('Health', () =>
  HttpResponse.json({ data: { health: { status: 'ok', version: '1.2.3', databaseReachable: true } } }),
)

// A setting of the Server settings page, typed as the server answers it: a fragment never matches an untyped object.
export const systemSetting = (over: Partial<SystemSettingFieldsFragment> & { key: string }): SystemSettingFieldsFragment & { __typename: 'SystemSetting' } => ({
  __typename: 'SystemSetting',
  kind: 'TEXT',
  choices: [],
  value: null,
  isSet: false,
  isSecret: false,
  source: 'DEFAULT',
  editable: true,
  restartPending: false,
  defaultValue: null,
  appliesAtOnce: false,
  ...over,
})

const SIGN_IN_MODES = ['None', 'Standalone', 'Oidc', 'ProxyHeader']

/**
 * The settings the setup's wizard walks through, as `systemSettings(code)` answers: the sign-in (the environment's mode, read-only, except
 * with sign-in off, where the shipped None is open to choice; `chosen` is the mode the wizard saved into the file, which waits for a
 * restart), its administrators' addresses, the e-mail server, the defaults.
 */
export const wizardSettings = (running: AuthMode, chosen?: string) => {
  const open = running === 'NONE'
  const runningValue = { NONE: 'None', STANDALONE: 'Standalone', OIDC: 'Oidc', PROXY_HEADER: 'ProxyHeader' }[running]
  const waits = chosen !== undefined && chosen !== runningValue
  return {
    __typename: 'SystemSettingsInfo',
    file: { __typename: 'SettingsFileInfo', path: '/data/settings.json', exists: chosen !== undefined, ignored: false, problems: [] },
    restartPending: waits,
    groups: [
      {
        __typename: 'SystemSettingsGroup',
        name: 'AUTH',
        appliesAtOnce: false,
        settings: [
          open
            ? systemSetting({ key: 'Auth:Mode', kind: 'CHOICE', choices: SIGN_IN_MODES, value: chosen ?? 'None', isSet: true, source: chosen ? 'FILE' : 'SHIPPED', restartPending: waits })
            : systemSetting({ key: 'Auth:Mode', kind: 'CHOICE', choices: SIGN_IN_MODES, value: runningValue, isSet: true, source: 'ENVIRONMENT', editable: false }),
          systemSetting({ key: 'Auth:AdminEmails', kind: 'LIST' }),
        ],
      },
      { __typename: 'SystemSettingsGroup', name: 'SMTP', appliesAtOnce: false, settings: [systemSetting({ key: 'Smtp:Host' })] },
      { __typename: 'SystemSettingsGroup', name: 'DEFAULTS', appliesAtOnce: true, settings: [systemSetting({ key: 'Defaults:Currency', defaultValue: 'EUR' })] },
    ],
  }
}

/** What the setup's done screen asks of the backups: the recipient new backups are encrypted for, none by default. */
export const backupRecipientHandler = (recipient: string | null = null) =>
  graphql.query('BackupRecipient', () =>
    HttpResponse.json({ data: { backups: { __typename: 'BackupOverviewInfo', settings: { __typename: 'BackupSettingsInfo', recipient, passphraseMinLength: 12 } } } }),
  )

/** A GraphQL error as the API sends it: coarse `code`, specific translation `key`, `args`, English fallback `message`. */
export const gqlError = (message: string, code: string, key?: string, args?: Record<string, unknown>) => ({
  errors: [{ message, extensions: { code, ...(key ? { key, args: args ?? {} } : {}) } }],
})

type Person = { id: string; displayName: string; avatarUrl: string | null }

export interface FakeSummary {
  lastFillUpDate: string | null
  latestOdometer: number | null
  averageConsumption: number | null
  currency: string | null
  thisMonthSpend: number
  lastMonthSpend: number
  fillUpCount: number
  expenseCount: number
  spendTrend: { month: string; amount: number }[]
  spending: { currency: string; thisMonth: number; lastMonth: number }[]
}

/**
 * Without `spending` given, it follows the main currency's figures (a getter), so the fakes that add a log to `thisMonthSpend` keep it
 * in step; give `spending` to show other currencies too.
 */
export const fakeSummary = (over: Partial<FakeSummary> = {}): FakeSummary => {
  const summary = {
    lastFillUpDate: '2026-09-17',
    latestOdometer: 12000,
    averageConsumption: 6.5,
    currency: 'HUF',
    thisMonthSpend: 50000,
    lastMonthSpend: 40000,
    fillUpCount: 5,
    expenseCount: 2,
    spendTrend: ['2026-05', '2026-06', '2026-07', '2026-08', '2026-09', '2026-10'].map((month, i) => ({ month, amount: 10000 * i })),
    ...over,
  } as FakeSummary
  if (over.spending) return summary
  return Object.defineProperty(summary, 'spending', {
    enumerable: true,
    get(this: FakeSummary) {
      return this.currency ? [{ currency: this.currency, thisMonth: this.thisMonthSpend, lastMonth: this.lastMonthSpend }] : []
    },
  })
}

export interface FakeVehicle {
  /** The card fragment only matches a typed object, and this normalises `Vehicle:<id>` like production. */
  __typename: 'Vehicle'
  id: string
  /** Counts its saves (from 1): a change kept on the device says which one it was made from. */
  version: number
  name: string
  licensePlate: string | null
  fuelType: 'PETROL' | 'DIESEL' | 'LPG'
  owner: Person | null
  canEdit: boolean
  logAccess: 'NONE' | 'VIEW' | 'EDIT' | 'DELETE'
  pictureUrl: string | null
  summary: FakeSummary
  units: { distance: 'KILOMETERS' | 'MILES'; volume: 'LITERS' | 'US_GALLONS' | 'IMPERIAL_GALLONS' }
  refuelingCount: number
  recurring: FakeRecurring[]
}

export const person = (displayName: string, over: Partial<Person> = {}): Person => ({ id: `p-${displayName}`, displayName, avatarUrl: null, ...over })

/** `ownerName` is a shortcut for a person without a picture. */
export const fakeVehicle = (over: Partial<FakeVehicle> & { ownerName?: string | null } = {}): FakeVehicle => {
  const { ownerName, ...rest } = over
  return {
    __typename: 'Vehicle',
    version: 1,
    id: 'v1',
    name: 'Octavia',
    licensePlate: 'ABC-123',
    fuelType: 'DIESEL',
    owner: ownerName === null ? null : person(ownerName ?? 'Alice'),
    canEdit: true,
    logAccess: 'DELETE',
    pictureUrl: null,
    summary: fakeSummary(),
    units: { distance: 'KILOMETERS', volume: 'LITERS' },
    refuelingCount: 2,
    recurring: [],
    ...rest,
  }
}

type Trashed = FakeVehicle & { deletedAt: string }

const sorters: Record<string, (v: Trashed) => string | number> = {
  NAME: (v) => v.name.toLowerCase(),
  LICENSE_PLATE: (v) => (v.licensePlate ?? '').toLowerCase(),
  FUEL_TYPE: (v) => v.fuelType,
  OWNER: (v) => (v.owner?.displayName ?? '').toLowerCase(),
  REFUELING_COUNT: (v) => v.refuelingCount,
  DELETED_AT: (v) => v.deletedAt,
}

/** Emulates what the real server does with the grid arguments: sorting and paging happen here, not in the UI. */
type GridVars = { orderBy: string; direction: string; skip: number; take: number }

function page<T extends Trashed>(rows: T[], vars: GridVars) {
  const key = sorters[vars.orderBy]
  const sorted = [...rows].sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0))
  if (vars.direction === 'DESC') sorted.reverse()
  return sorted.slice(vars.skip, vars.skip + vars.take)
}

/** A small in-memory backend for vehicles and trash, so mutations show up in the refetched lists. */
export function fakeVehicleBackend(initial: FakeVehicle[] = [], trashed: FakeVehicle[] = []) {
  const state = {
    /** Shallow copies: `summary` and `recurring` stay the caller's objects, which the log, expense and recurring fakes change in place (hand them `state.vehicles[i]`). */
    vehicles: initial.map((v) => ({ ...v, deletedAt: '' })) as Trashed[],
    trash: trashed.map((v) => ({ ...v, deletedAt: '2026-10-01T08:00:00Z' })) as Trashed[],
    calls: {} as Record<string, unknown[]>,
    /** Variables of every Vehicles / Trash / Welcome / VehicleCard query the UI sent, and how often the Arrange dialog asked for its list. */
    requests: { Vehicles: [] as Record<string, unknown>[], Trash: [] as Record<string, unknown>[], Welcome: [] as Record<string, unknown>[], VehicleCard: [] as Record<string, unknown>[], ArrangeVehicles: 0 },
    /** The user's own order of the vehicles (ids); the home page lists these first, the rest by name, like the server. */
    order: [] as string[],
    nextId: 100,
    /** How many trashed vehicles this user may delete for good (defaults to all of them). */
    trashDeletable: undefined as number | undefined,
    failWith: undefined as { message: string; key?: string; args?: Record<string, unknown> } | undefined,
  }
  const record = (name: string, vars: unknown) => (state.calls[name] ??= []).push(vars)
  /** The home page order, like the server's: the user's own arrangement first, the rest by name. */
  const ordered = (vehicles: Trashed[]) => {
    const position = (v: { id: string }) => (state.order.includes(v.id) ? state.order.indexOf(v.id) : Number.MAX_SAFE_INTEGER)
    return [...vehicles].sort((a, b) => position(a) - position(b) || a.name.localeCompare(b.name))
  }
  /** What a card asks for: the schedules with their types (the card fragment reads them through typed fragments). */
  const asCard = (v: Trashed) => ({ ...v, recurring: v.recurring.map(typedRecurring) })

  const handlers = [
    graphql.query('Vehicles', ({ variables }) => {
      state.requests.Vehicles.push(variables)
      return HttpResponse.json({
        data: { vehicles: page(state.vehicles, variables as unknown as GridVars), vehicleCount: state.vehicles.length },
      })
    }),
    graphql.query('Welcome', ({ variables }) => {
      state.requests.Welcome.push(variables)
      const term = String(variables.search ?? '').toLowerCase()
      const found = ordered(state.vehicles.filter((v) => !term || v.name.toLowerCase().includes(term) || (v.licensePlate ?? '').toLowerCase().includes(term)))
      const skip = Number(variables.skip ?? 0)
      return HttpResponse.json({ data: { myVehicles: found.slice(skip, skip + Number(variables.take ?? 50)).map(asCard), myVehicleCount: found.length, vehicleTotal: state.vehicles.length } })
    }),
    graphql.query('ImportTargets', () => HttpResponse.json({ data: { myVehicles: state.vehicles } })),
    graphql.query('ArrangeVehicles', () => {
      state.requests.ArrangeVehicles++
      return HttpResponse.json({ data: { myVehicles: ordered(state.vehicles).map((v) => ({ id: v.id, name: v.name, licensePlate: v.licensePlate })) } })
    }),
    graphql.mutation('SetVehicleOrder', ({ variables }) => {
      record('SetVehicleOrder', variables)
      state.order = [...(variables.vehicleIds as string[])]
      return HttpResponse.json({ data: { setVehicleOrder: true } })
    }),
    graphql.query('VehicleCard', ({ variables }) => {
      state.requests.VehicleCard.push(variables)
      const found = state.vehicles.find((x) => x.id === variables.id)
      return HttpResponse.json({ data: { vehicle: found ? asCard(found) : null } })
    }),
    graphql.query('Trash', ({ variables }) => {
      state.requests.Trash.push(variables)
      return HttpResponse.json({
        data: { trash: page(state.trash, variables as unknown as GridVars), trashCount: state.trash.length, trashDeletableCount: state.trashDeletable ?? state.trash.length },
      })
    }),
    graphql.query('VehicleDetails', ({ variables }) => {
      const v = state.vehicles.find((x) => x.id === variables.id)
      return HttpResponse.json({
        data: { vehicle: v ?? null },
      })
    }),
    graphql.query('VehicleDefaults', () =>
      HttpResponse.json({ data: { vehicleDefaults: { distanceUnit: 'KILOMETERS', volumeUnit: 'LITERS', currency: 'HUF', recurringWarnDays: 30, recurringWarnDistance: 500 } } }),
    ),
    graphql.mutation('AddVehicle', ({ variables }) => {
      record('AddVehicle', variables)
      if (state.failWith) {
        return HttpResponse.json(gqlError(state.failWith.message, 'VALIDATION_FAILED', state.failWith.key, state.failWith.args))
      }
      const id = variables.input.id ?? `v${state.nextId++}` // the browser's id, like the server
      state.vehicles.push({ ...fakeVehicle({ ...variables.input, id, refuelingCount: 0 }), deletedAt: '' })
      return HttpResponse.json({ data: { addVehicle: { id } } })
    }),
    graphql.mutation('UpdateVehicle', ({ variables }) => {
      record('UpdateVehicle', variables)
      if (state.failWith) {
        return HttpResponse.json(gqlError(state.failWith.message, 'VALIDATION_FAILED', state.failWith.key, state.failWith.args))
      }
      const i = state.vehicles.findIndex((v) => v.id === variables.input.id)
      state.vehicles[i] = { ...state.vehicles[i], ...variables.input }
      return HttpResponse.json({ data: { updateVehicle: { id: variables.input.id } } })
    }),
    graphql.mutation('DeleteVehicle', ({ variables }) => {
      record('DeleteVehicle', variables)
      const v = state.vehicles.find((x) => x.id === variables.id)!
      state.vehicles = state.vehicles.filter((x) => x.id !== variables.id)
      state.trash.push({ ...v, deletedAt: '2026-10-02T09:30:00Z' })
      return HttpResponse.json({ data: { deleteVehicle: { id: variables.id } } })
    }),
    graphql.mutation('RestoreVehicle', ({ variables }) => {
      record('RestoreVehicle', variables)
      const t = state.trash.find((x) => x.id === variables.id)!
      state.trash = state.trash.filter((x) => x.id !== variables.id)
      state.vehicles.push({ ...t, deletedAt: '' })
      return HttpResponse.json({ data: { restoreVehicle: { id: variables.id } } })
    }),
    graphql.mutation('EmptyTrash', () => {
      record('EmptyTrash', {})
      const removed = state.trash.length
      state.trash = []
      return HttpResponse.json({ data: { emptyTrash: removed } })
    }),
  ]
  return { state, handlers }
}

export interface FakeGridSettings extends GridSaved {
  gridId: string
}

/** The user's UI settings as the server keeps them: serves what is stored, stores what the UI saves, and records every call. */
export function fakeSettingsBackend(
  initial: {
    navOpen?: boolean | null
    language?: string | null
    colorMode?: ColorMode | null
    surface?: SurfaceStyle | null
    timeFormat?: TimeFormat | null
    dateFormat?: DateFormat | null
    defaultTimeFormat?: TimeFormat | null
    defaultDateFormat?: DateFormat | null
    grids?: FakeGridSettings[]
  } = {},
) {
  const state = {
    settings: {
      navOpen: initial.navOpen ?? null,
      language: initial.language ?? null,
      colorMode: initial.colorMode ?? null,
      surface: initial.surface ?? null,
      timeFormat: initial.timeFormat ?? null,
      dateFormat: initial.dateFormat ?? null,
      defaultTimeFormat: initial.defaultTimeFormat ?? null,
      defaultDateFormat: initial.defaultDateFormat ?? null,
      grids: initial.grids ?? [],
    } as {
      navOpen: boolean | null
      language: string | null
      colorMode: ColorMode | null
      surface: SurfaceStyle | null
      timeFormat: TimeFormat | null
      dateFormat: DateFormat | null
      defaultTimeFormat: TimeFormat | null
      defaultDateFormat: DateFormat | null
      grids: FakeGridSettings[]
    },
    calls: {} as Record<string, unknown[]>,
    requests: { UiSettings: 0 },
  }
  const record = (name: string, vars: unknown) => (state.calls[name] ??= []).push(vars)
  const handlers = [
    graphql.query('UiSettings', () => {
      state.requests.UiSettings++
      return HttpResponse.json({ data: { uiSettings: state.settings } })
    }),
    graphql.mutation('UpdateUiSettings', ({ variables }) => {
      record('UpdateUiSettings', variables)
      const input = variables.input as {
        navOpen?: boolean | null
        language?: string | null
        clearLanguage?: boolean | null
        colorMode?: ColorMode | null
        surface?: SurfaceStyle | null
        timeFormat?: TimeFormat | null
        clearTimeFormat?: boolean | null
        dateFormat?: DateFormat | null
        clearDateFormat?: boolean | null
      }
      if (input.navOpen != null) state.settings.navOpen = input.navOpen
      if (input.clearLanguage) state.settings.language = null
      else if (input.language != null) state.settings.language = input.language
      if (input.colorMode != null) state.settings.colorMode = input.colorMode
      if (input.surface != null) state.settings.surface = input.surface
      if (input.clearTimeFormat) state.settings.timeFormat = null
      else if (input.timeFormat != null) state.settings.timeFormat = input.timeFormat
      if (input.clearDateFormat) state.settings.dateFormat = null
      else if (input.dateFormat != null) state.settings.dateFormat = input.dateFormat
      const { grids: _, ...settings } = state.settings
      return HttpResponse.json({ data: { updateUiSettings: settings } })
    }),
    graphql.mutation('SaveGridSettings', ({ variables }) => {
      record('SaveGridSettings', variables)
      const input = variables.input as FakeGridSettings
      state.settings.grids = [...state.settings.grids.filter((g) => g.gridId !== input.gridId), input]
      return HttpResponse.json({ data: { saveGridSettings: input } })
    }),
    graphql.mutation('ResetGridSettings', ({ variables }) => {
      record('ResetGridSettings', variables)
      const had = state.settings.grids.some((g) => g.gridId === variables.gridId)
      state.settings.grids = state.settings.grids.filter((g) => g.gridId !== variables.gridId)
      return HttpResponse.json({ data: { resetGridSettings: had } })
    }),
  ]
  return { state, handlers }
}

/** The devices a test can pretend to be: a phone is narrow with a touch screen, a desktop is wide with a mouse. */
const DEVICES = {
  phone: { width: 390, hover: 'none', pointer: 'coarse' },
  desktop: { width: 1280, hover: 'hover', pointer: 'fine' },
} as const

/**
 * Whether a media query holds on a device: width ranges, hover, pointer, its colour scheme (dark unless the test says light) and no
 * reduced motion, joined with "and", alternatives with commas, with or without "@media" and spaces (MUI asks "(min-width:768px)").
 * Anything else does not match.
 */
export function matchesMedia(query: string, kind: keyof typeof DEVICES, scheme: 'light' | 'dark' = 'dark'): boolean {
  const device = DEVICES[kind]
  const feature = (f: string) => {
    const [name, value = ''] = f.replace(/[()]/g, '').split(':').map((x) => x.trim())
    const px = Number.parseFloat(value)
    switch (name) {
      case 'min-width':
        return device.width >= px
      case 'max-width':
        return device.width <= px
      case 'hover':
      case 'any-hover':
        return value === device.hover
      case 'pointer':
      case 'any-pointer':
        return value === device.pointer
      case 'prefers-color-scheme':
        return value === scheme
      case 'prefers-reduced-motion':
        return value === 'no-preference'
      default:
        return false
    }
  }
  return query
    .replace(/^@media\s*/, '')
    .split(',')
    .some((alternative) => alternative.split(/\band\b/).every((part) => part.trim() !== '' && feature(part.trim())))
}

/**
 * Pretends to be a phone (narrow, touch) or a desktop (wide, mouse) browser window for media queries (CSS and useMediaQuery), set to
 * a dark colour scheme unless `scheme` says light.
 */
export function stubViewport(kind: keyof typeof DEVICES, { scheme = 'dark' }: { scheme?: 'light' | 'dark' } = {}) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: matchesMedia(query, kind, scheme),
    media: query,
    onchange: null,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
  }))
}

export interface FakeRefueling {
  id: string
  vehicleId: string
  date: string
  /** Volume, total and odometer are null while the log waits for its photos (`reviewState`). */
  volume: number | null
  totalCost: number | null
  /** What was taken off at the till; the total is what was paid after it. */
  discount: number | null
  currency: string | null
  odometer: number | null
  isFullTank: boolean
  /** A fill-up before this one was not logged (the server then stores no consumption for it). */
  missedPreviousFillUp: boolean
  note: string | null
  /** Fuel per 100 distance units between this and the previous full fill-up (the server calculates and stores it). */
  consumption: number | null
  canEdit: boolean
  canDelete: boolean
  createdBy: Person
  deletedAt?: string
  reviewState: 'NONE' | 'AWAITING_PHOTOS' | 'NEEDS_REVIEW' | 'INCOMPLETE'
  filledFromPhoto: ('ODOMETER' | 'VOLUME' | 'TOTAL' | 'DISCOUNT')[]
  /** Counts its saves (from 1): a change kept on the device says which one it was made from. */
  version: number
}

export const fakeRefueling = (over: Partial<FakeRefueling> = {}): FakeRefueling => ({
  id: 'r1',
  vehicleId: 'v1',
  date: '2026-09-01',
  volume: 40.5,
  totalCost: 20250,
  discount: null,
  currency: 'HUF',
  odometer: 12000,
  isFullTank: true,
  missedPreviousFillUp: false,
  note: null,
  consumption: null,
  canEdit: true,
  canDelete: false,
  createdBy: person('Alice'),
  reviewState: 'NONE',
  filledFromPhoto: [],
  version: 1,
  ...over,
})

const pricePerUnit = (r: FakeRefueling) => (r.totalCost == null || !r.volume ? null : r.totalCost / r.volume)
/** The pump's price: the cost plus the discount, per unit (as the server works it out). */
const pricePerUnitBeforeDiscount = (r: FakeRefueling) => (r.totalCost == null || !r.volume ? null : (r.totalCost + (r.discount ?? 0)) / r.volume)

const logSorters: Record<string, (r: FakeRefueling) => string | number> = {
  DATE: (r) => r.date,
  VOLUME: (r) => r.volume ?? 0,
  TOTAL_COST: (r) => r.totalCost ?? 0,
  ODOMETER: (r) => r.odometer ?? 0,
  PRICE_PER_UNIT: (r) => pricePerUnit(r) ?? 0,
  CONSUMPTION: (r) => r.consumption ?? 0,
  CREATED_BY: (r) => r.createdBy.displayName.toLowerCase(),
  VEHICLE: (r) => r.vehicleId,
  DELETED_AT: (r) => r.deletedAt ?? '',
}

export interface FakePhoto {
  id: string
  url: string
  /** Added to a saved log with `?form=…` (its edit dialog while photo reading is on): it is read. */
  read?: boolean
}

/**
 * In-memory photos of logs behind the upload endpoints (`PUT/DELETE /media/<kind>/<logId>/photos`) and the drafts of logs that are not
 * saved yet (`PUT /media/vehicles/<id>/photo-drafts`, `DELETE /media/photo-drafts/<id>`), as the real API answers them. `failWith`
 * makes uploads (photos and drafts) fail with a stable error key; `puts`, `draftPuts` and the delete lists record the requests.
 * `attach` is what saving a new log does with its drafts; ids in `unattachable` stay drafts (the server could not attach them).
 */
export function fakePhotoStore(initial: Record<string, FakePhoto[]> = {}) {
  const state = {
    byLog: Object.fromEntries(Object.entries(initial).map(([k, v]) => [k, [...v]])) as Record<string, FakePhoto[]>,
    drafts: [] as FakePhoto[],
    puts: [] as { kind: string; logId: string; bytes: number }[],
    /** The query of each photo added to a saved log (what it is for and the language, for photo reading). */
    putQueries: [] as string[],
    draftPuts: [] as { vehicleId: string; bytes: number }[],
    /** The query of each draft upload (what the photo is for and the language, for photo reading). */
    draftQueries: [] as string[],
    deletes: [] as { kind: string; logId: string; imageId: string }[],
    draftDeletes: [] as string[],
    unattachable: new Set<string>(),
    failWith: undefined as { key: string; status?: number; args?: Record<string, unknown> } | undefined,
    nextId: 1,
  }
  const refuse = () =>
    state.failWith ? HttpResponse.json({ key: state.failWith.key, args: state.failWith.args ?? {}, message: 'refused' }, { status: state.failWith.status ?? 400 }) : undefined
  const attach = (logId: string, ids: readonly string[] = []) => {
    const taken = state.drafts.filter((d) => ids.includes(d.id) && !state.unattachable.has(d.id))
    state.drafts = state.drafts.filter((d) => !taken.includes(d))
    ;(state.byLog[logId] ??= []).push(...taken)
    return taken.map((p) => ({ id: p.id }))
  }
  const draftHandlers = [
    http.put('/media/vehicles/:vehicleId/photo-drafts', async ({ params, request }) => {
      state.draftPuts.push({ vehicleId: String(params.vehicleId), bytes: (await request.arrayBuffer()).byteLength })
      state.draftQueries.push(new URL(request.url).search)
      const refused = refuse()
      if (refused) return refused
      const id = `draft${state.nextId++}`
      state.drafts.push({ id, url: `/media/${id}` })
      return HttpResponse.json({ id, url: `/media/${id}` })
    }),
    http.delete('/media/photo-drafts/:id', ({ params }) => {
      state.draftDeletes.push(String(params.id))
      state.drafts = state.drafts.filter((d) => d.id !== params.id)
      return new HttpResponse(null, { status: 204 })
    }),
  ]
  const handlers = [...draftHandlers, ...(['expenses', 'refuelings'] as const).flatMap((kind) => [
    http.put(`/media/${kind}/:logId/photos`, async ({ params, request }) => {
      const logId = String(params.logId)
      state.puts.push({ kind, logId, bytes: (await request.arrayBuffer()).byteLength })
      const query = new URL(request.url).search
      state.putQueries.push(query)
      const refused = refuse()
      if (refused) return refused
      const id = `img${state.nextId++}`
      ;(state.byLog[logId] ??= []).push({ id, url: `/media/${id}`, read: query.includes('form=') })
      return HttpResponse.json({ id, url: `/media/${id}` })
    }),
    http.delete(`/media/${kind}/:logId/photos/:imageId`, ({ params }) => {
      const logId = String(params.logId)
      state.deletes.push({ kind, logId, imageId: String(params.imageId) })
      state.byLog[logId] = (state.byLog[logId] ?? []).filter((p) => p.id !== params.imageId)
      return new HttpResponse(null, { status: 204 })
    }),
  ])]
  return { state, handlers, attach, photosOf: (logId: string) => state.byLog[logId] ?? [] }
}

export interface FakeReadValue {
  name: 'ODOMETER' | 'TOTAL' | 'VOLUME' | 'UNIT_PRICE' | 'DISCOUNT' | 'CURRENCY' | 'DATE' | 'TITLE'
  value: string
  confidence?: number
}

/** Why a photo gave less than it might have: about one value (`field`), or about the photo as a whole (none). */
export interface FakeReadIssue {
  code: string
  field?: FakeReadValue['name']
}

/**
 * Photo reading on the server (`recognitionStatus`, `photoDrafts`). `results[i]` is what the i-th uploaded draft (draft1, draft2, ...)
 * shows (none: nothing could be read) and `issues[i]` why it gave less than it might have; each draft is answered as queued for its first
 * `queuedPolls` polls, then as read (or failed, when its number is in `failed`). `asked` records the ids of every poll.
 */
export function fakeRecognition({
  available = true,
  results = [] as FakeReadValue[][],
  queuedPolls = 1,
  issues = [] as FakeReadIssue[][],
  failed = [] as number[],
  photos = undefined as { photosOf: (logId: string) => FakePhoto[] } | undefined,
} = {}) {
  const state = { available, results, queuedPolls, polls: {} as Record<string, number>, asked: [] as string[][], statusAsked: 0, logAsked: 0 }
  // A saved log's photos are numbered in the order they are first asked for (a draft's number is in its id).
  const logOrder: string[] = []
  const reading = (id: string, nth?: number) => {
    const polls = (state.polls[id] = (state.polls[id] ?? 0) + 1)
    if (polls <= state.queuedPolls) return { __typename: 'PhotoReadingInfo', status: 'QUEUED', kind: null, values: [], issues: [] }
    const n = nth ?? Number(id.replace(/\D/g, '')) - 1
    if (failed.includes(n + 1)) return { __typename: 'PhotoReadingInfo', status: 'FAILED', kind: null, values: [], issues: [] }
    const values = state.results[n] ?? []
    return {
      __typename: 'PhotoReadingInfo',
      status: 'READ',
      kind: values.length > 0 ? 'FUEL_RECEIPT' : 'UNKNOWN',
      values: values.map((v) => ({ __typename: 'ReadingValueInfo', confidence: 0.9, ...v })),
      issues: (issues[n] ?? []).map((i) => ({ __typename: 'ReadingIssueInfo', field: null, ...i })),
    }
  }
  const handlers = [
    graphql.query('RecognitionStatus', () => {
      state.statusAsked++
      return HttpResponse.json({ data: { recognitionStatus: { __typename: 'RecognitionStatusInfo', available: state.available } } })
    }),
    graphql.query('PhotoDraftReadings', ({ variables }) => {
      const ids = variables.ids as string[]
      state.asked.push(ids)
      return HttpResponse.json({ data: { photoDrafts: ids.map((id) => ({ __typename: 'PhotoDraftInfo', id, reading: reading(id) })) } })
    }),
    ...(['Refueling', 'Expense'] as const).map((type) =>
      graphql.query(`${type}PhotoReadings`, ({ variables }) => {
        state.logAsked++
        const id = String(variables.id)
        const shown = (photos?.photosOf(id) ?? []).map((p) => {
          if (p.read && !logOrder.includes(p.id)) logOrder.push(p.id)
          return { __typename: 'LogPhotoInfo', id: p.id, reading: p.read ? reading(p.id, logOrder.indexOf(p.id)) : null }
        })
        return HttpResponse.json({ data: { [type.toLowerCase()]: { __typename: type, id, photos: shown } } })
      }),
    ),
  ]
  return { state, handlers }
}

/** A small in-memory backend for one vehicle's logs (and their trash), including the sharing list. */
export function fakeLogBackend(vehicle: FakeVehicle, logs: FakeRefueling[] = [], photos = fakePhotoStore()) {
  const state = {
    vehicle,
    logs: [...logs],
    trash: [] as FakeRefueling[],
    calls: {} as Record<string, unknown[]>,
    requests: [] as Record<string, unknown>[],
    grants: [] as { user: Person; level: 'EDIT' | 'DELETE' }[],
    candidates: [person('Bob'), person('Carol')],
    failWith: undefined as { message: string; key: string; args?: Record<string, unknown> } | undefined,
    lastOdometer: logs.some((l) => l.odometer != null) ? Math.max(...logs.map((l) => l.odometer ?? 0)) : null,
    nextId: 200,
  }
  const record = (name: string, vars: unknown) => (state.calls[name] ??= []).push(vars)
  const fail = (): Response | undefined => (state.failWith ? HttpResponse.json(gqlError(state.failWith.message, 'VALIDATION_FAILED', state.failWith.key, state.failWith.args)) : undefined)
  const rows = (list: FakeRefueling[], vars: GridVars) => {
    const key = logSorters[vars.orderBy]
    const sorted = [...list].sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0))
    if (vars.direction === 'DESC') sorted.reverse()
    return sorted.slice(vars.skip, vars.skip + vars.take).map((r) => ({
      ...r,
      pricePerUnit: pricePerUnit(r),
      pricePerUnitBeforeDiscount: pricePerUnitBeforeDiscount(r),
      vehicle: { id: state.vehicle.id, name: state.vehicle.name, units: state.vehicle.units },
    }))
  }

  const handlers = [
    graphql.query('VehicleDetails', ({ variables }) =>
      HttpResponse.json({ data: { vehicle: variables.id === state.vehicle.id ? { ...state.vehicle, refuelingCount: state.logs.length } : null } }),
    ),
    graphql.query('Refuelings', ({ variables }) => {
      state.requests.push(variables)
      return HttpResponse.json({ data: { refuelings: rows(state.logs, variables as unknown as GridVars), refuelingCount: state.logs.length } })
    }),
    graphql.query('RefuelingTrash', ({ variables }) =>
      HttpResponse.json({
        data: { refuelingTrash: rows(state.trash, variables as unknown as GridVars), refuelingTrashCount: state.trash.length, refuelingTrashDeletableCount: state.trash.length },
      }),
    ),
    graphql.query('RefuelingDetails', ({ variables }) => {
      const r = state.logs.find((x) => x.id === variables.id)
      return HttpResponse.json({ data: { refueling: r ? { ...r, photos: photos.photosOf(r.id) } : null } })
    }),
    graphql.query('LogDefaults', () =>
      HttpResponse.json({ data: { logDefaults: { lastOdometer: state.lastOdometer, lastDate: state.lastOdometer ? '2026-09-01' : null, currency: 'HUF' } } }),
    ),
    graphql.mutation('LogRefueling', ({ variables }) => {
      record('LogRefueling', variables)
      const failure = fail()
      if (failure) return failure
      const id = variables.input.id ?? `r${state.nextId++}` // the browser's id, like the server
      const { photoIds, ...input } = variables.input
      // Values left empty wait for the photos, like on the server (which only allows it while one is still being read).
      const reviewState = input.odometer == null || input.volume == null || input.totalCost == null ? 'AWAITING_PHOTOS' : 'NONE'
      state.logs.push(fakeRefueling({ ...input, id, reviewState }))
      if (input.odometer != null) state.lastOdometer = Math.max(state.lastOdometer ?? 0, input.odometer)
      // The server recomputes the card's summary; the fake does the minimum for a card to show the change (the summary object is shared with the vehicle fakes).
      Object.assign(vehicle.summary, { lastFillUpDate: input.date, fillUpCount: vehicle.summary.fillUpCount + 1 })
      if (input.odometer != null) vehicle.summary.latestOdometer = Math.max(vehicle.summary.latestOdometer ?? 0, input.odometer)
      return HttpResponse.json({ data: { logRefueling: { id, reviewState, photos: photos.attach(id, photoIds) } } })
    }),
    graphql.mutation('UpdateRefueling', ({ variables }) => {
      record('UpdateRefueling', variables)
      const failure = fail()
      if (failure) return failure
      const i = state.logs.findIndex((l) => l.id === variables.input.id)
      // A person saved it: checked. Like the server, a discount left out stays and 0 is none.
      const discount = variables.input.discount === undefined || variables.input.discount === null ? state.logs[i].discount : variables.input.discount || null
      state.logs[i] = { ...state.logs[i], ...variables.input, discount, reviewState: 'NONE', filledFromPhoto: [] }
      return HttpResponse.json({ data: { updateRefueling: { id: variables.input.id } } })
    }),
    graphql.mutation('DeleteRefueling', ({ variables }) => {
      record('DeleteRefueling', variables)
      const log = state.logs.find((l) => l.id === variables.id)!
      state.logs = state.logs.filter((l) => l.id !== variables.id)
      state.trash.push({ ...log, deletedAt: '2026-10-02T09:30:00Z' })
      return HttpResponse.json({ data: { deleteRefueling: { id: variables.id } } })
    }),
    graphql.mutation('RestoreRefueling', ({ variables }) => {
      record('RestoreRefueling', variables)
      const log = state.trash.find((l) => l.id === variables.id)!
      state.trash = state.trash.filter((l) => l.id !== variables.id)
      state.logs.push(log)
      return HttpResponse.json({ data: { restoreRefueling: { id: variables.id } } })
    }),
    graphql.mutation('EmptyRefuelingTrash', () => {
      record('EmptyRefuelingTrash', {})
      const removed = state.trash.length
      state.trash = []
      return HttpResponse.json({ data: { emptyRefuelingTrash: removed } })
    }),
    graphql.query('LogAccess', () =>
      HttpResponse.json({
        data: {
          vehicleLogAccess: state.grants.map((g) => ({ level: g.level, user: g.user })),
          shareCandidates: state.candidates.filter((c) => !state.grants.some((g) => g.user.id === c.id)),
        },
      }),
    ),
    graphql.mutation('SetVehicleLogAccess', ({ variables }) => {
      record('SetVehicleLogAccess', variables)
      const { userId, level } = variables.input
      state.grants = state.grants.filter((g) => g.user.id !== userId)
      if (level !== 'NONE') state.grants.push({ user: state.candidates.find((c) => c.id === userId)!, level })
      return HttpResponse.json({ data: { setVehicleLogAccess: true } })
    }),
  ]
  return { state, handlers: [...handlers, ...photos.handlers], photos }
}

export interface FakeExpense {
  id: string
  vehicleId: string
  date: string
  title: string
  category: string | null
  /** Null while the expense waits for its photos (`reviewState`). */
  amount: number | null
  /** What was taken off the amount, which is what was paid. */
  discount: number | null
  currency: string | null
  odometer: number | null
  note: string | null
  canEdit: boolean
  canDelete: boolean
  createdBy: Person
  deletedAt?: string
  reviewState: 'NONE' | 'AWAITING_PHOTOS' | 'NEEDS_REVIEW' | 'INCOMPLETE'
  filledFromPhoto: ('ODOMETER' | 'VOLUME' | 'TOTAL' | 'DISCOUNT')[]
  /** The recurring expenses it covered when they were marked done. */
  schedules?: { id: string; title: string }[]
  version: number
}

export const fakeExpense = (over: Partial<FakeExpense> = {}): FakeExpense => ({
  id: 'e1',
  vehicleId: 'v1',
  date: '2026-09-01',
  title: 'Oil change',
  category: 'Service',
  amount: 35000,
  discount: null,
  currency: 'HUF',
  odometer: 12000,
  note: null,
  canEdit: true,
  canDelete: false,
  createdBy: person('Alice'),
  reviewState: 'NONE',
  filledFromPhoto: [],
  version: 1,
  ...over,
})

const expenseSorters: Record<string, (e: FakeExpense) => string | number> = {
  DATE: (e) => e.date,
  TITLE: (e) => e.title.toLowerCase(),
  CATEGORY: (e) => (e.category ?? '').toLowerCase(),
  AMOUNT: (e) => e.amount ?? 0,
  ODOMETER: (e) => e.odometer ?? 0,
  CREATED_BY: (e) => e.createdBy.displayName.toLowerCase(),
  VEHICLE: (e) => e.vehicleId,
  DELETED_AT: (e) => e.deletedAt ?? '',
}

/** A small in-memory backend for one vehicle's expenses (and their trash). */
export function fakeExpenseBackend(vehicle: FakeVehicle, expenses: FakeExpense[] = [], photos = fakePhotoStore()) {
  const state = {
    vehicle,
    expenses: [...expenses],
    trash: [] as FakeExpense[],
    calls: {} as Record<string, unknown[]>,
    requests: [] as Record<string, unknown>[],
    failWith: undefined as { message: string; key: string; args?: Record<string, unknown> } | undefined,
    nextId: 300,
  }
  const record = (name: string, vars: unknown) => (state.calls[name] ??= []).push(vars)
  const fail = (): Response | undefined => (state.failWith ? HttpResponse.json(gqlError(state.failWith.message, 'VALIDATION_FAILED', state.failWith.key, state.failWith.args)) : undefined)
  const rows = (list: FakeExpense[], vars: GridVars) => {
    const key = expenseSorters[vars.orderBy]
    const sorted = [...list].sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0))
    if (vars.direction === 'DESC') sorted.reverse()
    return sorted.slice(vars.skip, vars.skip + vars.take).map((e) => ({ ...e, schedules: e.schedules ?? [], vehicle: { id: state.vehicle.id, name: state.vehicle.name } }))
  }

  const handlers = [
    graphql.query('VehicleDetails', ({ variables }) =>
      HttpResponse.json({ data: { vehicle: variables.id === state.vehicle.id ? { ...state.vehicle, refuelingCount: 0 } : null } }),
    ),
    graphql.query('Expenses', ({ variables }) => {
      state.requests.push(variables)
      return HttpResponse.json({ data: { expenses: rows(state.expenses, variables as unknown as GridVars), expenseCount: state.expenses.length } })
    }),
    graphql.query('ExpenseTrash', ({ variables }) =>
      HttpResponse.json({ data: { expenseTrash: rows(state.trash, variables as unknown as GridVars), expenseTrashCount: state.trash.length, expenseTrashDeletableCount: state.trash.length } }),
    ),
    graphql.query('ExpenseDetails', ({ variables }) => {
      const e = state.expenses.find((x) => x.id === variables.id)
      return HttpResponse.json({ data: { expense: e ? { ...e, photos: photos.photosOf(e.id) } : null } })
    }),
    graphql.query('ExpenseCategories', () =>
      HttpResponse.json({ data: { expenseCategories: [...new Set(state.expenses.map((e) => e.category).filter((c): c is string => c !== null))].sort() } }),
    ),
    graphql.query('LogDefaults', () => HttpResponse.json({ data: { logDefaults: { lastOdometer: null, lastDate: null, currency: 'HUF' } } })),
    graphql.mutation('AddExpense', ({ variables }) => {
      record('AddExpense', variables)
      const failure = fail()
      if (failure) return failure
      const id = variables.input.id ?? `e${state.nextId++}` // the browser's id, like the server
      const { photoIds, ...input } = variables.input
      const reviewState = input.amount == null ? 'AWAITING_PHOTOS' : 'NONE'
      state.expenses.push(fakeExpense({ ...input, id, reviewState }))
      Object.assign(vehicle.summary, { expenseCount: vehicle.summary.expenseCount + 1, thisMonthSpend: vehicle.summary.thisMonthSpend + (input.amount ?? 0) }) // for the card, see LogRefueling
      return HttpResponse.json({ data: { addExpense: { id, reviewState, photos: photos.attach(id, photoIds) } } })
    }),
    graphql.mutation('UpdateExpense', ({ variables }) => {
      record('UpdateExpense', variables)
      const i = state.expenses.findIndex((e) => e.id === variables.input.id)
      // Like the server: a discount left out stays, 0 is none.
      const discount = variables.input.discount === undefined || variables.input.discount === null ? state.expenses[i].discount : variables.input.discount || null
      state.expenses[i] = { ...state.expenses[i], ...variables.input, discount, reviewState: 'NONE', filledFromPhoto: [] }
      return HttpResponse.json({ data: { updateExpense: { id: variables.input.id } } })
    }),
    graphql.mutation('DeleteExpense', ({ variables }) => {
      record('DeleteExpense', variables)
      const e = state.expenses.find((x) => x.id === variables.id)!
      state.expenses = state.expenses.filter((x) => x.id !== variables.id)
      state.trash.push({ ...e, deletedAt: '2026-10-02T09:30:00Z' })
      return HttpResponse.json({ data: { deleteExpense: { id: variables.id } } })
    }),
    graphql.mutation('RestoreExpense', ({ variables }) => {
      record('RestoreExpense', variables)
      const e = state.trash.find((x) => x.id === variables.id)!
      state.trash = state.trash.filter((x) => x.id !== variables.id)
      state.expenses.push(e)
      return HttpResponse.json({ data: { restoreExpense: { id: variables.id } } })
    }),
    graphql.mutation('EmptyExpenseTrash', () => {
      record('EmptyExpenseTrash', {})
      const removed = state.trash.length
      state.trash = []
      return HttpResponse.json({ data: { emptyExpenseTrash: removed } })
    }),
  ]
  return { state, handlers: [...handlers, ...photos.handlers], photos }
}

export interface FakeChart {
  id: string
  title: string
  metric: string
  grouping: string
  kind: string
  range: string
  rangeFrom: string | null
  rangeTo: string | null
  stacked: boolean
  trend: string
  runningTotal: boolean
  compareYearEarlier: boolean
  secondMetric: string | null
  secondKind: string | null
  isShared: boolean
  canEdit: boolean
  createdBy: Person
}

export interface FakeTile {
  id: string
  x: number
  y: number
  w: number
  h: number
  pinned: boolean
}

export interface FakeLayout {
  tiles: FakeTile[]
  hidden: string[]
}

/** A tile of a fake layout: a chart two columns wide and eight rows tall unless said otherwise. */
export const fakeTile = (id: string, x: number, y: number, w = 2, h = 8, pinned = false): FakeTile => ({ id, x, y, w, h, pinned })

export const fakeChart = (over: Partial<FakeChart> = {}): FakeChart => ({
  id: 'c1',
  title: 'My chart',
  metric: 'FUEL_COST',
  grouping: 'MONTH',
  kind: 'BAR',
  range: 'LAST6_MONTHS',
  rangeFrom: null,
  rangeTo: null,
  stacked: false,
  trend: 'NONE',
  runningTotal: false,
  compareYearEarlier: false,
  secondMetric: null,
  secondKind: null,
  isShared: false,
  canEdit: true,
  createdBy: person('Alice'),
  ...over,
})

/**
 * A small in-memory backend for a vehicle's dashboard: key figures, saved charts and chart data (a fixed answer per unit; a trend line or a
 * year earlier asked for comes as a series of its own, as the server's do).
 */
export function fakeDashboardBackend(vehicle: FakeVehicle, charts: FakeChart[] = []) {
  const state = {
    vehicle,
    charts: [...charts],
    calls: {} as Record<string, unknown[]>,
    chartRequests: [] as { vehicleId: string; config: Record<string, unknown> }[],
    failWith: undefined as { message: string; key: string; args?: Record<string, unknown> } | undefined,
    nextId: 400,
    /** How the dashboard is arranged: the vehicle's and the user's own (null for none), and whether the user may change the vehicle's. */
    dashboard: { shared: null as FakeLayout | null, personal: null as FakeLayout | null, canChangeShared: true },
  }
  const record = (name: string, vars: unknown) => (state.calls[name] ??= []).push(vars)
  const months = ['2026-08', '2026-09', '2026-10']
  const unitOf = (metric: string) =>
    metric === 'FUEL_VOLUME' ? 'VOLUME'
    : metric === 'DISTANCE' ? 'DISTANCE'
    : metric === 'AVERAGE_CONSUMPTION' ? 'CONSUMPTION'
    : metric === 'FILL_UPS' ? 'COUNT'
    : metric === 'COST_PER_DISTANCE' || metric === 'FUEL_COST_PER_DISTANCE' ? 'COST_PER_DISTANCE'
    : 'CURRENCY'
  const answer = (config: Record<string, unknown>) => {
    const metric = String(config.metric)
    const unit = unitOf(metric)
    const cost = unit === 'CURRENCY' || unit === 'COST_PER_DISTANCE'
    const keys = config.grouping === 'CATEGORY' ? ['Service', 'Parking', ''] : months
    // As the server names them: a cost or a distance is a total, the other fuel figures are fuel.
    const kinds = config.stacked ? ['fuel', 'expenses'] : [config.metric === 'EXPENSE_COST' ? 'expenses' : cost || metric === 'DISTANCE' ? 'total' : 'fuel']
    const line = (kind: string, role: string, value: (i: number) => number | null, slope: number | null = null, of = metric) => {
      const own = unitOf(of)
      return { kind, currency: own === 'CURRENCY' || own === 'COST_PER_DISTANCE' ? 'HUF' : null, role, slope, metric: of, unit: own, points: keys.map((key, i) => ({ key, value: value(i) })) }
    }
    const values = kinds.map((kind, k) => line(kind, 'VALUE', (i) => (i + 1) * 1000 * (k + 1)))
    const base = config.stacked ? 'total' : kinds[0]
    const second = config.secondMetric ? String(config.secondMetric) : null
    return {
      unit,
      series: [
        ...values,
        ...(config.compareYearEarlier ? [line(base, 'YEAR_EARLIER', (i) => (i + 1) * 500)] : []),
        ...(config.trend === 'MOVING_AVERAGE' ? [line(base, 'MOVING_AVERAGE', (i) => (i < 2 ? null : 2000))] : []),
        ...(config.trend === 'LINEAR' ? [line(base, 'LINEAR', (i) => 1000 + i * 1000, 1000)] : []),
        ...(second ? [line(second === 'DISTANCE' ? 'total' : 'fuel', 'VALUE', (i) => 5 + i, null, second)] : []),
      ],
    }
  }

  const handlers = [
    graphql.query('VehicleDashboard', ({ variables }) => {
      const layout = (l: FakeLayout | null) => (l ? { __typename: 'DashboardLayoutInfo', ...l } : null)
      const dashboard = { __typename: 'DashboardInfo', shared: layout(state.dashboard.shared), personal: layout(state.dashboard.personal), canChangeShared: state.dashboard.canChangeShared }
      return HttpResponse.json({ data: { vehicle: variables.id === state.vehicle.id ? { __typename: 'Vehicle', id: state.vehicle.id, summary: state.vehicle.summary, dashboard } : null, vehicleCharts: state.charts } })
    }),
    graphql.mutation('SaveDashboardLayout', ({ variables }) => {
      record('SaveDashboardLayout', variables)
      const { personal, tiles, hidden } = variables.input as FakeLayout & { personal: boolean }
      if (!personal && !state.dashboard.canChangeShared) return HttpResponse.json(gqlError('no', 'FORBIDDEN', 'dashboard.needEditToChange'))
      state.dashboard[personal ? 'personal' : 'shared'] = { tiles: tiles.map((t) => ({ ...t, pinned: t.pinned ?? false })), hidden }
      return HttpResponse.json({ data: { saveDashboardLayout: true } })
    }),
    graphql.mutation('ResetDashboardLayout', ({ variables }) => {
      record('ResetDashboardLayout', variables)
      state.dashboard[variables.personal ? 'personal' : 'shared'] = null
      return HttpResponse.json({ data: { resetDashboardLayout: true } })
    }),
    graphql.query('ChartData', ({ variables }) => {
      state.chartRequests.push(variables as never)
      return HttpResponse.json({ data: { vehicleChartData: answer(variables.config as Record<string, unknown>) } })
    }),
    graphql.mutation('SaveChart', ({ variables }) => {
      record('SaveChart', variables)
      if (state.failWith) return HttpResponse.json(gqlError(state.failWith.message, 'VALIDATION_FAILED', state.failWith.key, state.failWith.args))
      const { id, title, shared, config } = variables.input as { id: string | null; title: string; shared: boolean; config: Record<string, unknown> }
      const entry = fakeChart({
        id: id ?? `c${state.nextId++}`,
        title,
        isShared: shared,
        metric: String(config.metric),
        grouping: String(config.grouping),
        kind: String(config.kind),
        range: String(config.range),
        stacked: Boolean(config.stacked),
        rangeFrom: (config.from as string) ?? null,
        rangeTo: (config.to as string) ?? null,
        trend: String(config.trend ?? 'NONE'),
        runningTotal: Boolean(config.runningTotal),
        compareYearEarlier: Boolean(config.compareYearEarlier),
        secondMetric: (config.secondMetric as string) ?? null,
        secondKind: (config.secondKind as string) ?? null,
      })
      state.charts = id ? state.charts.map((c) => (c.id === id ? entry : c)) : [...state.charts, entry]
      return HttpResponse.json({ data: { saveVehicleChart: { id: entry.id } } })
    }),
    graphql.mutation('DeleteChart', ({ variables }) => {
      record('DeleteChart', variables)
      state.charts = state.charts.filter((c) => c.id !== variables.id)
      return HttpResponse.json({ data: { deleteVehicleChart: true } })
    }),
  ]
  return { state, handlers }
}

export interface FakeRecurring {
  version?: number
  /** The fragment on RecurringExpenseInfo only matches when the type is named. */
  __typename: 'RecurringExpenseInfo'
  id: string
  title: string
  category: string | null
  note: string | null
  kind: 'TIME' | 'ODOMETER' | 'COMBINED'
  intervalMonths: number | null
  intervalDistance: number | null
  lastDoneDate: string
  lastDoneOdometer: number | null
  warnDays: number
  warnDistance: number
  status: {
    __typename?: 'RecurrenceStatusInfo'
    state: 'UPCOMING' | 'DUE_SOON' | 'OVERDUE'
    limit: 'TIME' | 'ODOMETER' | null
    dueDate: string | null
    dueOdometer: number | null
    daysLeft: number | null
    distanceLeft: number | null
  }
}

/** A schedule as the server answers it: its type on the status too, or the status fragment of the documents would not match it in the cache. */
export const typedRecurring = <T extends { status: object }>(item: T) => ({ __typename: 'RecurringExpenseInfo' as const, ...item, status: { __typename: 'RecurrenceStatusInfo' as const, ...item.status } })

/** A combined (12 months or 15,000 km) schedule that is upcoming, unless the test says otherwise. */
export const fakeRecurring = (over: Partial<FakeRecurring> = {}): FakeRecurring => ({
  __typename: 'RecurringExpenseInfo',
  version: 1,
  id: 'rc1',
  title: 'Oil change',
  category: 'Service',
  note: null,
  kind: 'COMBINED',
  intervalMonths: 12,
  intervalDistance: 15000,
  lastDoneDate: '2026-01-15',
  lastDoneOdometer: 50000,
  warnDays: 30,
  warnDistance: 500,
  status: { __typename: 'RecurrenceStatusInfo', state: 'UPCOMING', limit: 'TIME', dueDate: '2027-01-15', dueOdometer: 65000, daysLeft: 106, distanceLeft: 9000 },
  ...over,
})

/**
 * In-memory recurring expenses of one vehicle behind the Recurring* queries and mutations; a finished item is upcoming again. With a
 * `vehicle`, its `recurring` list is kept in step (in place, the array is shared with the vehicle fakes), so a home card sees the change.
 */
export function fakeRecurringBackend(items: FakeRecurring[] = [], vehicle?: FakeVehicle) {
  const state = {
    items: [...items],
    calls: {} as Record<string, unknown[]>,
    failWith: undefined as { message: string; key: string; args?: Record<string, unknown> } | undefined,
    nextId: 500,
    /** The instance's default warnings a new schedule starts with. */
    warnDefaults: { recurringWarnDays: 30, recurringWarnDistance: 500 },
  }
  const record = (name: string, vars: unknown) => (state.calls[name] ??= []).push(vars)
  const fail = (): Response | undefined => (state.failWith ? HttpResponse.json(gqlError(state.failWith.message, 'VALIDATION_FAILED', state.failWith.key, state.failWith.args)) : undefined)
  const upcoming = (): FakeRecurring['status'] => ({ state: 'UPCOMING', limit: 'TIME', dueDate: '2027-12-01', dueOdometer: null, daysLeft: 400, distanceLeft: null })
  const sync = () => vehicle?.recurring.splice(0, vehicle.recurring.length, ...state.items)
  const handlers = [
    graphql.query('RecurringExpenses', ({ variables }) => HttpResponse.json({ data: { vehicle: { __typename: 'Vehicle', id: variables.vehicleId, recurring: state.items.map(typedRecurring) } } })),
    graphql.query('VehicleDefaults', () =>
      HttpResponse.json({ data: { vehicleDefaults: { distanceUnit: 'KILOMETERS', volumeUnit: 'LITERS', currency: 'HUF', ...state.warnDefaults } } }),
    ),
    graphql.mutation('AddRecurringExpense', ({ variables }) => {
      record('AddRecurringExpense', variables)
      const failed = fail()
      if (failed) return failed
      const input = variables.input as Partial<FakeRecurring>
      const item = fakeRecurring({ ...input, id: input.id ?? `rc${state.nextId++}`, status: upcoming() })
      state.items.push(item)
      sync()
      return HttpResponse.json({ data: { addRecurringExpense: typedRecurring(item) } })
    }),
    graphql.mutation('UpdateRecurringExpense', ({ variables }) => {
      record('UpdateRecurringExpense', variables)
      const failed = fail()
      if (failed) return failed
      const input = variables.input as Partial<FakeRecurring> & { id: string }
      const i = state.items.findIndex((x) => x.id === input.id)
      state.items[i] = { ...state.items[i], ...input }
      sync()
      return HttpResponse.json({ data: { updateRecurringExpense: typedRecurring(state.items[i]) } })
    }),
    graphql.mutation('DeleteRecurringExpense', ({ variables }) => {
      record('DeleteRecurringExpense', variables)
      state.items = state.items.filter((x) => x.id !== variables.id)
      sync()
      return HttpResponse.json({ data: { deleteRecurringExpense: true } })
    }),
    graphql.mutation('MarkRecurringExpensesDone', ({ variables }) => {
      record('MarkRecurringExpensesDone', variables)
      const failed = fail()
      if (failed) return failed
      const input = variables.input as { ids: string[]; date: string; odometer: number | null; amount?: number | null; photoIds?: string[] | null; expenseId?: string | null }
      const done = input.ids.map((id) => {
        const i = state.items.findIndex((x) => x.id === id)
        state.items[i] = { ...state.items[i], lastDoneDate: input.date, lastDoneOdometer: input.odometer ?? state.items[i].lastDoneOdometer, status: upcoming() }
        return state.items[i]
      })
      sync()
      // Like the server: one expense for the whole visit when an amount (or a photo that may give it) came.
      const logged = input.amount != null || (input.photoIds?.length ?? 0) > 0
      if (vehicle && logged) {
        // The logged expense, as the card shows it (see LogRefueling in fakeLogBackend).
        Object.assign(vehicle.summary, { expenseCount: vehicle.summary.expenseCount + 1, thisMonthSpend: vehicle.summary.thisMonthSpend + (input.amount ?? 0) })
        if (input.odometer != null) vehicle.summary.latestOdometer = Math.max(vehicle.summary.latestOdometer ?? 0, input.odometer)
      }
      const expense = logged ? { __typename: 'Expense', id: input.expenseId ?? `ex${state.nextId++}`, photos: (input.photoIds ?? []).map((id) => ({ __typename: 'LogPhotoInfo', id })) } : null
      return HttpResponse.json({ data: { markRecurringExpensesDone: { __typename: 'MarkRecurringExpensesDonePayload', schedules: done.map(typedRecurring), expense } } })
    }),
  ]
  return { state, handlers }
}

export type FakeNotification = NotificationFieldsFragment

export const fakeNotification = (over: Partial<FakeNotification> = {}): FakeNotification => ({
  id: 'n1',
  kind: 'LOG_ACCESS_CHANGED',
  read: false,
  readAt: null,
  count: 1,
  createdAt: '2026-09-30T10:00:00Z',
  updatedAt: '2026-09-30T10:00:00Z',
  subject: { type: 'VEHICLE', id: 'v1' },
  context: { type: 'VEHICLE', id: 'v1' },
  args: [
    { name: 'actorName', value: 'Bob' },
    { name: 'level', value: 'EDIT' },
    { name: 'vehicleName', value: 'Family car' },
  ],
  ...over,
})

/** The current user's inbox behind the notification queries and the mark-read mutation, newest change first like the server. */
export function fakeNotificationBackend(items: FakeNotification[] = []) {
  const state = { items: [...items], calls: {} as Record<string, unknown[]> }
  const record = (name: string, vars: unknown) => (state.calls[name] ??= []).push(vars)
  const typed = (n: FakeNotification) => ({
    __typename: 'NotificationInfo',
    ...n,
    subject: { __typename: 'NotificationRefInfo', ...n.subject },
    context: n.context && { __typename: 'NotificationRefInfo', ...n.context },
    args: n.args.map((a) => ({ __typename: 'NotificationArg', ...a })),
  })
  const sorted = () => [...state.items].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
  const unread = () => state.items.filter((n) => !n.read).length
  const handlers = [
    graphql.query('UnreadNotificationCount', () => HttpResponse.json({ data: { notificationCount: unread() } })),
    graphql.query('LatestNotifications', ({ variables }) =>
      HttpResponse.json({ data: { notifications: sorted().slice(0, variables.take as number).map(typed), notificationCount: unread() } }),
    ),
    graphql.query('Notifications', ({ variables }) => {
      record('Notifications', variables)
      const v = variables as { unreadOnly: boolean; skip: number; take: number }
      const matching = sorted().filter((n) => !v.unreadOnly || !n.read)
      return HttpResponse.json({ data: { notifications: matching.slice(v.skip, v.skip + v.take).map(typed), notificationCount: matching.length } })
    }),
    graphql.mutation('MarkNotificationsRead', ({ variables }) => {
      record('MarkNotificationsRead', variables)
      const ids = variables.ids as string[] | null | undefined
      const changed = state.items.filter((n) => !n.read && (!ids || ids.includes(n.id))).map((n) => ({ ...n, read: true, readAt: '2026-10-01T12:00:00Z' }))
      state.items = state.items.map((n) => changed.find((c) => c.id === n.id) ?? n)
      return HttpResponse.json({ data: { markNotificationsRead: changed.map(typed) } })
    }),
  ]
  return { state, handlers }
}
