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
export const BARE_ID: ReadonlySet<TyreAction> = new Set(['unretireTyreSet', 'deleteTyreSet', 'restoreTyreSet', 'deleteTyreInstallation', 'restoreTyreInstallation', 'deleteTyreEntry', 'restoreTyreEntry'])

type Input = Record<string, unknown> | undefined

const str = (v: unknown): string[] => (typeof v === 'string' && v !== '' ? [v] : [])
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
      return [...str(input?.id), ...str(input?.installationId)]
    case 'swapTyres':
    case 'changeTyrePositions':
      return str(input?.installationId)
    case 'addPastTyreSwaps':
      return swapsOf(input).flatMap((s) => str(s.installationId))
    case 'addTyreInstallation':
    case 'addTyreEntry':
      return str(input?.id)
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
