import { useApolloClient } from '@apollo/client/react'
import { CombinedGraphQLErrors } from '@apollo/client/errors'
import type { TypedDocumentNode } from '@apollo/client'
import { useCallback } from 'react'
import { useTranslation } from 'react-i18next'
import { VehicleTyresDocument, type DistanceUnit, type VehicleTyresFieldsFragment } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { madeDay, monthsBetween, odometerHint, sortedReadings, type Reading } from './odometer.ts'
import { todayIso } from '../forms/dates.ts'

export type Tyres = VehicleTyresFieldsFragment
export type TyreSetInfo = Tyres['sets'][number]
export type TyreSet = TyreSetInfo['set']
export type TyreStretchInfo = TyreSetInfo['stretches'][number]
export type TyreLogDay = Tyres['days'][number]

/**
 * Runs a tyre mutation and puts its answer (the vehicle's tyres after it) in the place of the tab's query, so the tab shows it at once; the
 * vehicle's details and starting values are asked again, since a reading typed with it may be the latest now. A refusal is thrown as it
 * came, for the dialog to say (or, for `tyres.swapExists`, to ask whether to replace).
 */
export function useTyreMutation(vehicleId: string) {
  const client = useApolloClient()
  return useCallback(
    async <TData extends Record<string, unknown>, TVars extends Record<string, unknown>>(document: TypedDocumentNode<TData, TVars>, variables: TVars) => {
      const result = await client.mutate({ mutation: document, variables, refetchQueries: ['VehicleTyres', 'VehicleDetails', 'LogDefaults'] }) // VehicleTyres asked again so the device's kept answer follows
      const answer = result.data ? (Object.values(result.data)[0] as Tyres | undefined) : undefined
      if (answer && typeof answer === 'object' && 'sets' in answer)
        client.writeQuery({ query: VehicleTyresDocument, variables: { vehicleId }, data: { vehicleTyres: answer } })
      return answer
    },
    [client, vehicleId],
  )
}

/** The key and arguments of a server's refusal (`tyres.swapExists` and its day and set), when it is one. */
export function keyedOf(error: unknown): { key: string; args: Record<string, unknown> } | null {
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
