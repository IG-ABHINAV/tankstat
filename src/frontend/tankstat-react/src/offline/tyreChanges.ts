/**
 * The tyre changes a device keeps (#287): one action per mutation, named as the mutation and the field of `ChangeInput` it is sent as
 * (`swapTyres`), the change's input the mutation's own (a bare id as `{ id }`). Pure: what each one targets, creates and needs, so the
 * outbox never folds them (each waits as it was made, in order: a swap depends on the ones before it), the push sends what a change was
 * made on first, and taking back an add takes along what was made on it.
 */
export const TYRE_ACTIONS = [
  'addTyreSet',
  'updateTyreSet',
  'setTyreRoughDistance',
  'retireTyreSet',
  'unretireTyreSet',
  'deleteTyreSet',
  'restoreTyreSet',
  'swapTyres',
  'changeTyrePositions',
  'addPastTyreSwaps',
  'addTyreInstallation',
  'updateTyreInstallation',
  'deleteTyreInstallation',
  'restoreTyreInstallation',
  'addTyreEntry',
  'updateTyreEntry',
  'deleteTyreEntry',
  'restoreTyreEntry',
  // Single tyres and their times on the car (#289).
  'breakUpTyreSet',
  'updateTyre',
  'deleteTyre',
  'restoreTyre',
  'addTyrePlace',
  'updateTyrePlace',
  'deleteTyrePlace',
  'restoreTyrePlace',
  // Tyres replaced early (#289 step 3).
  'replaceTyres',
] as const

export type TyreAction = (typeof TYRE_ACTIONS)[number]

export const isTyreAction = (action: string): action is TyreAction => (TYRE_ACTIONS as readonly string[]).includes(action)

/** The actions whose `ChangeInput` field is a bare id (`deleteTyreSet: "<id>"`), kept as `{ id }`. */
export const BARE_ID: ReadonlySet<TyreAction> = new Set([
  'unretireTyreSet', 'deleteTyreSet', 'restoreTyreSet', 'deleteTyreInstallation', 'restoreTyreInstallation', 'restoreTyreEntry',
  'deleteTyre', 'restoreTyre', 'deleteTyrePlace', 'restoreTyrePlace',
])

type Input = Record<string, unknown> | undefined

/**
 * What a kept change holds for the device only and never sends (#289): an edit or trash of a time on the car keeps its tyre's and
 * installation's ids beside it, so it waits for the break-up or swap that made it (`tyreNeeds`) and goes when that is taken back.
 */
const DEVICE_ONLY: Partial<Record<TyreAction, readonly string[]>> = { updateTyrePlace: ['tyreId', 'installationId'] }

/** The input as the server takes it: without what is the device's only. */
export function sentInput(action: TyreAction, input: Input): Input {
  const dropped = DEVICE_ONLY[action]
  if (!dropped || !input) return input
  return Object.fromEntries(Object.entries(input).filter(([key]) => !dropped.includes(key)))
}

const str = (v: unknown): string[] => (typeof v === 'string' && v !== '' ? [v] : [])
/** The expense an amount makes (#288): its id, when the fee carries one. */
const expenseOf = (fee: unknown): string[] =>
  fee && typeof fee === 'object' && typeof (fee as Record<string, unknown>).amount === 'number' ? str((fee as Record<string, unknown>).expenseId) : []
const swapsOf = (input: Input) => (Array.isArray(input?.swaps) ? (input.swaps as Record<string, unknown>[]) : [])
const listOf = (v: unknown) => (Array.isArray(v) ? (v as Record<string, unknown>[]) : [])

/** The set, installation or tyre log line a change is about, as the server files it (`SyncGraphQL` `Tyres`): a swap's is the installation it creates (its line has the same id). */
export function tyreTarget(action: TyreAction, input: Input): string {
  switch (action) {
    case 'swapTyres':
    case 'changeTyrePositions':
      return str(input?.installationId)[0] ?? ''
    case 'addPastTyreSwaps':
      return str(swapsOf(input)[0]?.installationId)[0] ?? ''
    case 'breakUpTyreSet':
    case 'replaceTyres':
      return str(input?.setId)[0] ?? ''
    default:
      return str(input?.id)[0] ?? ''
  }
}

/** The ids of the sets, installations and lines a change creates (an installation's line has its id). */
export function tyreCreates(action: TyreAction, input: Input): string[] {
  switch (action) {
    case 'addTyreSet':
      // A price makes a Bought line and its expense (#288).
      return [...str(input?.id), ...str(input?.installationId), ...(expenseOf(input?.price).length ? [...str(input?.boughtEntryId), ...expenseOf(input?.price)] : [])]
    case 'swapTyres':
    case 'changeTyrePositions':
      return [...str(input?.installationId), ...expenseOf(input?.fee)]
    case 'addPastTyreSwaps':
      return swapsOf(input).flatMap((s) => str(s.installationId))
    case 'addTyreInstallation':
      return str(input?.id)
    case 'addTyreEntry':
      // For several tyres (#289) a line each, and a whole set's tyres when it breaks it up.
      return [
        ...str(input?.id), ...listOf(input?.tyres).flatMap((t) => str(t.id)), ...listOf((input?.breakUp as Input)?.tyres).flatMap((t) => str(t.id)), ...expenseOf(input?.fee),
      ]
    case 'breakUpTyreSet':
      return listOf(input?.tyres).flatMap((t) => str(t.id))
    case 'replaceTyres':
      // The new tyres and their Bought lines, a whole set's tyres it breaks up, and the price's expense.
      return [
        ...listOf(input?.tyres).flatMap((t) => [...str(t.id), ...str(t.lineId)]), ...listOf((input?.breakUp as Input)?.tyres).flatMap((t) => str(t.id)),
        ...expenseOf(input?.price),
      ]
    case 'addTyrePlace':
      return str(input?.id)
    default:
      return []
  }
}

/** The ids of the sets, installations and lines a change is made on (any of them may have been added here, and must reach the server first). */
export function tyreNeeds(action: TyreAction, input: Input): string[] {
  const creates = new Set(tyreCreates(action, input))
  const ids = [
    ...str(input?.id), ...str(input?.setId), ...swapsOf(input).flatMap((s) => str(s.setId)),
    // Single tyres (#289): the tyre a time on the car is of, and the tyres a swap places, may have been made here by a break-up; a time on
    // the car a break-up or a swap worked out (its id from its tyre's and installation's) is kept with those two ids beside it, not sent.
    ...str(input?.tyreId), ...str(input?.installationId), ...listOf(input?.corners).flatMap((c) => str(c.tyreId)),
    // The tyres an entry is for, and the other lines an edit changes too (*Also change it for*).
    // The tyres an entry is for, or a replacement replaces, and the other lines an edit changes too (*Also change it for*).
    ...listOf(input?.tyres).flatMap((t) => str(t.tyreId)), ...(Array.isArray(input?.also) ? (input.also as unknown[]).flatMap(str) : []),
  ]
  return [...new Set(ids)].filter((id) => !creates.has(id))
}

/** The expense a waiting tyre change's amount makes or changes (#288), as the Expenses list shows it until the server has it. */
export interface TyreFeeExpense {
  expenseId: string
  /** The line it belongs to (a swap's: its installation's id). */
  lineId: string
  kind: string
  day: string
  odometer: number | null
  title: string
  category: string | null
  amount: number
  currency: string | null
  /** A line it was already on (an edit): the expense may be the server's already. */
  edit: boolean
}

export function tyreFeeExpense(action: string, input: Input): TyreFeeExpense | null {
  const fee = (action === 'addTyreSet' || action === 'replaceTyres' ? input?.price : input?.fee) as Record<string, unknown> | null | undefined
  if (!fee || typeof fee.amount !== 'number' || typeof fee.expenseId !== 'string') return null
  const odometer = typeof input?.odometer === 'number' ? input.odometer : null
  const base = { expenseId: fee.expenseId, title: String(fee.title ?? ''), category: (fee.category as string | null) ?? null, amount: fee.amount, currency: (fee.currency as string | null) ?? null }
  switch (action) {
    case 'addTyreEntry':
    case 'updateTyreEntry':
      // For several tyres (#289) the expense names the first line, as the server's `Expense.tyreEntry`.
      return { ...base, lineId: String(listOf(input?.tyres)[0]?.id ?? input?.id ?? ''), kind: String(input?.kind ?? 'OTHER'), day: String(input?.day ?? ''), odometer, edit: action === 'updateTyreEntry' }
    case 'swapTyres':
    case 'changeTyrePositions':
      return {
        ...base, lineId: String(input?.installationId ?? ''), kind: action === 'swapTyres' ? 'SWAP' : 'POSITIONS_CHANGED', day: String(input?.day ?? ''), odometer, edit: false,
      }
    case 'addTyreSet':
      return { ...base, lineId: String(input?.boughtEntryId ?? ''), kind: 'BOUGHT', day: String(input?.boughtOn ?? input?.day ?? ''), odometer: null, edit: false }
    case 'replaceTyres':
      return { ...base, lineId: String(listOf(input?.tyres)[0]?.lineId ?? ''), kind: 'BOUGHT', day: String(input?.day ?? ''), odometer, edit: false }
    default:
      return null
  }
}
