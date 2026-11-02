import { useTranslation } from 'react-i18next'
import type { DistanceUnit, VolumeUnit } from '../../gql/generated.ts'
import { useFormat } from '../../i18n/format.ts'

/** The vehicle's units, as far as the device or the server knows them. */
export type Units = { distance: DistanceUnit | null; volume: VolumeUnit | null }

/**
 * The name and the value of a field a change sets (`changes.ts` `UPDATABLE`), for showing a change next to what is on the server: the
 * value formatted as the screens show it (in the vehicle's units; an amount in the currency of the same values), "none" when empty.
 */
export function useFieldText(units: Units) {
  const { t } = useTranslation()
  const format = useFormat()

  const label = (field: string): string => t(`sync.fields.${field}` as 'sync.fields.date')

  const value = (field: string, values: Record<string, unknown>): string => {
    const v = values[field]
    if (v === null || v === undefined || v === '') return t('sync.value.empty')
    const n = Number(v)
    switch (field) {
      case 'date':
      case 'lastDoneDate':
        return typeof v === 'string' ? format.date(v) : String(v)
      case 'volume':
        return units.volume ? format.volume(n, units.volume) : format.number(n)
      case 'totalCost':
      case 'discount':
      case 'amount':
        return typeof values.currency === 'string' && values.currency ? format.money(n, values.currency) : format.number(n)
      case 'odometer':
      case 'lastDoneOdometer':
      case 'intervalDistance':
      case 'warnDistance':
        return units.distance ? format.distance(n, units.distance) : format.number(n)
      case 'isFullTank':
      case 'missedPreviousFillUp':
        return t(v ? 'sync.value.yes' : 'sync.value.no')
      case 'fuelType':
        return t(`fuel.${String(v)}` as 'fuel.PETROL')
      case 'kind':
        return t(`recurring.kind.${String(v)}` as 'recurring.kind.TIME')
      case 'units': {
        const u = v as { distance?: string | null; volume?: string | null }
        return [
          u.distance ? t(`units.distance.${u.distance}` as 'units.distance.KILOMETERS') : null,
          u.volume ? t(`units.volume.${u.volume}` as 'units.volume.LITERS') : null,
        ].filter(Boolean).join(', ')
      }
      case 'intervalMonths':
        return t('sync.value.months', { count: n })
      case 'warnDays':
        return t('sync.value.days', { count: n })
      default:
        return String(v)
    }
  }

  /** "Name: Golf GTI". */
  const pair = (field: string, values: Record<string, unknown>) => t('sync.conflict.fieldValue', { field: label(field), value: value(field, values) })

  return { label, value, pair }
}
