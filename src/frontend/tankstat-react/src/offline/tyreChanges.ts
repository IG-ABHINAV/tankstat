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
] as const

export type TyreAction = (typeof TYRE_ACTIONS)[number]

export const isTyreAction = (action: string): action is TyreAction => (TYRE_ACTIONS as readonly string[]).includes(action)

/** The actions whose `ChangeInput` field is a bare id (`deleteTyreSet: "<id>"`), kept as `{ id }`. */
export const BARE_ID: ReadonlySet<TyreAction> = new Set(['unretireTyreSet', 'deleteTyreSet', 'restoreTyreSet', 'deleteTyreInstallation', 'restoreTyreInstallation', 'restoreTyreEntry'])

type Input = Record<string, unknown> | undefined

const str = (v: unknown): string[] => (typeof v === 'string' && v !== '' ? [v] : [])
/** The expense an amount makes (#288): its id, when the fee carries one. */
const expenseOf = (fee: unknown): string[] =>
  fee && typeof fee === 'object' && typeof (fee as Record<string, unknown>).amount === 'number' ? str((fee as Record<string, unknown>).expenseId) : []
const swapsOf = (input: Input) => (Array.isArray(input?.swaps) ? (input.swaps as Record<string, unknown>[]) : [])

/** The set, installation or tyre log line a change is about, as the server files it (`SyncGraphQL` `Tyres`): a swap's is the installation it creates (its line has the same id). */
export function tyreTarget(action: TyreAction, input: Input): string {
  switch (action) {
    case 'swapTyres':
    case 'changeTyrePositions':
      return str(input?.installationId)[0] ?? ''
    case 'addPastTyreSwaps':
      return str(swapsOf(input)[0]?.installationId)[0] ?? ''
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
      return [...str(input?.id), ...expenseOf(input?.fee)]
    default:
      return []
  }
}

/** The ids of the sets, installations and lines a change is made on (any of them may have been added here, and must reach the server first). */
export function tyreNeeds(action: TyreAction, input: Input): string[] {
  const creates = new Set(tyreCreates(action, input))
  const ids = [...str(input?.id), ...str(input?.setId), ...swapsOf(input).flatMap((s) => str(s.setId))]
  return ids.filter((id) => !creates.has(id))
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
  const fee = (action === 'addTyreSet' ? input?.price : input?.fee) as Record<string, unknown> | null | undefined
  if (!fee || typeof fee.amount !== 'number' || typeof fee.expenseId !== 'string') return null
  const odometer = typeof input?.odometer === 'number' ? input.odometer : null
  const base = { expenseId: fee.expenseId, title: String(fee.title ?? ''), category: (fee.category as string | null) ?? null, amount: fee.amount, currency: (fee.currency as string | null) ?? null }
  switch (action) {
    case 'addTyreEntry':
    case 'updateTyreEntry':
      return { ...base, lineId: String(input?.id ?? ''), kind: String(input?.kind ?? 'OTHER'), day: String(input?.day ?? ''), odometer, edit: action === 'updateTyreEntry' }
    case 'swapTyres':
    case 'changeTyrePositions':
      return {
        ...base, lineId: String(input?.installationId ?? ''), kind: action === 'swapTyres' ? 'SWAP' : 'POSITIONS_CHANGED', day: String(input?.day ?? ''), odometer, edit: false,
      }
    case 'addTyreSet':
      return { ...base, lineId: String(input?.boughtEntryId ?? ''), kind: 'BOUGHT', day: String(input?.boughtOn ?? input?.day ?? ''), odometer: null, edit: false }
    default:
      return null
  }
}
