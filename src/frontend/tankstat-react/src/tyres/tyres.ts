import { useApolloClient } from '@apollo/client/react'
import { CombinedGraphQLErrors } from '@apollo/client/errors'
import type { DocumentNode, TypedDocumentNode } from '@apollo/client'
import { Kind } from 'graphql'
import { useCallback } from 'react'
import { useTranslation } from 'react-i18next'
import { VehicleTyresDocument, type DistanceUnit, type VehicleTyresFieldsFragment } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { madeDay, monthsBetween, odometerHint, sortedReadings, type Reading } from './odometer.ts'
import { todayIso } from '../forms/dates.ts'
import { discardChange, submitChange, willKeep } from '../offline/submitChange.ts'
import { isTyreAction, tyreTarget, type TyreAction } from '../offline/tyreChanges.ts'
import { uuidV4 } from '../offline/uuid.ts'
import { useToast } from '../toast/toastContext.ts'
import { applyTyreChange, noTyres } from './replay.ts'
import { TyreRuleError } from './tyreHistory.ts'

export type Tyres = VehicleTyresFieldsFragment
export type TyreSetInfo = Tyres['sets'][number]
export type TyreSet = TyreSetInfo['set']
export type TyreStretchInfo = TyreSetInfo['stretches'][number]
export type TyreLogDay = Tyres['days'][number]

/** A tyre change saved: sent (the server applied it), or kept on this device to be sent once the server can be reached. */
export interface TyreSaved {
  queued: boolean
  /** The kept change's id, so an Undo takes it back instead of sending the opposite change. */
  changeId: string
}

/** The mutation's field (`swapTyres`): the action of the change kept on the device (`offline/tyreChanges.ts`). */
const actionOf = (document: DocumentNode): TyreAction => {
  const operation = document.definitions.find((d) => d.kind === Kind.OPERATION_DEFINITION)
  const field = operation?.selectionSet.selections[0]
  const name = field?.kind === Kind.FIELD ? field.name.value : ''
  if (!isTyreAction(name)) throw new Error(`${name} is not a tyre change.`)
  return name
}

const SET_ACTIONS = new Set<TyreAction>(['updateTyreSet', 'setTyreRoughDistance', 'retireTyreSet', 'unretireTyreSet', 'deleteTyreSet', 'restoreTyreSet'])
const STRETCH_ACTIONS = new Set<TyreAction>(['updateTyreStretch', 'deleteTyreStretch', 'restoreTyreStretch'])

/** The version of the set or stretch a change is made on, as the tab shows it; none for one added here (version 0) or not shown. */
function versionOf(shown: Tyres | null | undefined, action: TyreAction, input: Record<string, unknown>): number | null {
  const sets = shown?.sets ?? []
  const version = SET_ACTIONS.has(action)
    ? sets.find((s) => s.set.id === input.id)?.set.version
    : STRETCH_ACTIONS.has(action)
      ? sets.flatMap((s) => s.stretches).find((x) => x.stretch.id === input.id)?.stretch.version
      : undefined
  return version ? version : null
}

/**
 * Runs a tyre change, the way a log's change goes (`offline/submitChange.ts`): kept on this device while the server is out of reach or the
 * vehicle has changes waiting (checked first by the server's rules over what the tab shows, so a refusal such as `tyres.swapExists` comes
 * back to the dialog at once, #287), else sent. A sent one's answer (the vehicle's tyres after it) takes the place of the tab's query; it,
 * the vehicle's details and starting values are asked again, since a reading typed with it may be the latest now. A kept one makes the
 * screen ask again, so the device's own answer shows it done (`localResolvers.ts`). A refusal is thrown as it came, for the dialog to say
 * (or, for `tyres.swapExists`, to ask whether to replace).
 */
export function useTyreMutation(vehicleId: string) {
  const client = useApolloClient()
  return useCallback(
    async <TData extends Record<string, unknown>, TVars extends Record<string, unknown>>(document: TypedDocumentNode<TData, TVars>, variables: TVars): Promise<TyreSaved> => {
      const action = actionOf(document)
      // The mutation's input; one that takes its values as variables (a retire's id and day, a bare id) keeps them all.
      const input = ('input' in variables ? variables.input : { ...variables }) as Record<string, unknown>
      const shown = client.readQuery({ query: VehicleTyresDocument, variables: { vehicleId } })?.vehicleTyres
      const changeId = uuidV4()
      const draft = { id: changeId, entity: 'tyres' as const, action, vehicleId, targetId: tyreTarget(action, input), input, expectedVersion: versionOf(shown, action, input) }
      if (willKeep(vehicleId)) applyTyreChange(shown ?? noTyres(vehicleId), { ...draft, seq: 0, createdAt: 0 }) // the refusal the server would give
      const result = await submitChange(client, draft, () =>
        client.mutate({ mutation: document, variables, refetchQueries: ['VehicleTyres', 'VehicleDetails', 'LogDefaults'] }), // VehicleTyres asked again so the device's kept answer follows
      )
      if (result.queued) return { queued: true, changeId }
      const answer = result.result.data ? (Object.values(result.result.data)[0] as Tyres | undefined) : undefined
      if (answer && typeof answer === 'object' && 'sets' in answer)
        client.writeQuery({ query: VehicleTyresDocument, variables: { vehicleId }, data: { vehicleTyres: answer } })
      return { queued: false, changeId }
    },
    [client, vehicleId],
  )
}

/**
 * The toasts after a tyre change: `saved` says a kept one is saved on this device; `undoable` offers Undo, which takes a kept change back
 * (never sending the opposite change for what the server never had) or, for a sent one, runs `restore`.
 */
export function useTyreToasts() {
  const { t } = useTranslation()
  const client = useApolloClient()
  const { toast, undoable } = useToast()
  return {
    saved: (result: TyreSaved, message: string) => toast(result.queued ? t('toast.savedOnDevice') : message),
    undoable: (result: TyreSaved, message: string, restore: () => Promise<unknown>) =>
      undoable(result.queued ? t('toast.savedOnDevice') : message, () => (result.queued ? discardChange(client, result.changeId) : restore())),
  }
}

/** The key and arguments of a refusal (`tyres.swapExists` and its day and set), the server's or the device's, when it is one. */
export function keyedOf(error: unknown): { key: string; args: Record<string, unknown> } | null {
  if (error instanceof TyreRuleError) return { key: error.key, args: error.args } // the device's check of a change it keeps
  if (!CombinedGraphQLErrors.is(error)) return null
  const ext = error.errors[0]?.extensions
  return typeof ext?.key === 'string' ? { key: ext.key, args: (ext.args as Record<string, unknown>) ?? {} } : null
}

/** The words a tyre screen shares: distances in the vehicle's unit, a set's pairs, its age, a form's odometer hint. */
export function useTyreWords(unit: DistanceUnit) {
  const { t } = useTranslation()
  const format = useFormat()
  const short = t(`units.distanceShort.${unit}`)
  const distance = (value: number, approximate = false) => {
    const text = `${format.number(value)} ${short}`
    return approximate ? t('tyres.approx', { value: text }) : text
  }
  const pairs = (pairOneFront: boolean | null | undefined) =>
    pairOneFront === true ? t('tyres.pairs.one') : pairOneFront === false ? t('tyres.pairs.two') : t('tyres.pairs.none')
  /** The set's age: from its DOT week when known, else from "in use since"; a retired set's up to the day it was retired. */
  const age = (set: Pick<TyreSet, 'since' | 'madeWeek' | 'madeYear' | 'retiredOn'>) => {
    const from = set.madeWeek != null && set.madeYear != null ? madeDay(set.madeWeek, set.madeYear) : set.since
    const months = monthsBetween(from, set.retiredOn ?? todayIso())
    const years = Math.floor(months / 12)
    return years > 0 ? t('tyres.ageYears', { years, months: months % 12 }) : t('tyres.ageMonths', { months })
  }
  const odometerText = (readings: readonly Reading[], day: string) => {
    const hint = odometerHint(sortedReadings(readings), day)
    switch (hint.kind) {
      case 'none':
        return t('tyres.odoHint.none')
      case 'before':
        return t('tyres.odoHint.before', { date: format.date(hint.firstDate) })
      case 'exact':
        return t('tyres.odoHint.exact', { value: distance(hint.value) })
      default:
        return t('tyres.odoHint.worked', { value: distance(hint.value) })
    }
  }
  return { unitShort: short, distance, pairs, age, odometerText }
}

/** A whole number typed in a field, or null when the field is empty. */
export const wholeOrNull = (text: string) => (text.trim() === '' ? null : Number(text.trim()))

/** A whole number field's check: empty is fine, anything else must be digits. */
export const wholeInvalid = (message: string) => ({ message, test: (v: string) => v.trim() !== '' && !/^\d+$/.test(v.trim()) })
