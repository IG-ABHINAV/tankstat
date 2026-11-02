import { useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import type { DateFormat, DistanceUnit, TimeFormat, VolumeUnit } from '../gql/generated.ts'
import { useFormats } from './formatStore.ts'
import { validLocale } from './locale.ts'

/** The week from Monday, as the server names the days (in its own case). */
const WEEK = ['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday']

/** The clock a time format asks for; none: the language's own. */
const HOUR_CYCLES: Record<TimeFormat, Intl.DateTimeFormatOptions> = { TWENTY_FOUR_HOUR: { hourCycle: 'h23' }, TWELVE_HOUR: { hourCycle: 'h12' } }

/** A date format as its parts in order and what goes between them (and after the last, for "2026. 10. 13."). */
const DATE_PATTERNS: Record<DateFormat, { order: ['year' | 'month' | 'day', 'year' | 'month' | 'day', 'year' | 'month' | 'day']; between: string; end?: string }> = {
  ISO: { order: ['year', 'month', 'day'], between: '-' },
  DAY_MONTH_YEAR_DOTS: { order: ['day', 'month', 'year'], between: '.' },
  DAY_MONTH_YEAR_SLASHES: { order: ['day', 'month', 'year'], between: '/' },
  MONTH_DAY_YEAR: { order: ['month', 'day', 'year'], between: '/' },
  YEAR_MONTH_DAY_DOTS: { order: ['year', 'month', 'day'], between: '. ', end: '.' },
}

/** A day in numbers the way `format` writes it (`timeZone` UTC for a calendar date, the browser's for an instant). */
export function numericDate(day: Date, format: DateFormat, timeZone?: string): string {
  const parts = new Intl.DateTimeFormat('en', { year: 'numeric', month: '2-digit', day: '2-digit', timeZone }).formatToParts(day)
  const part = (type: string) => parts.find((p) => p.type === type)?.value ?? ''
  const pattern = DATE_PATTERNS[format]
  return pattern.order.map(part).join(pattern.between) + (pattern.end ?? '')
}

/** The moment the format menu writes its examples with: a time after noon and a day after the twelfth tell every format apart. */
const SAMPLE = new Date(Date.UTC(2026, 9, 13, 13, 29))

/** An example of a time format (none: the language's own) in `language`, for a menu that offers it. */
export const sampleTime = (language: string, format: TimeFormat | null) =>
  new Intl.DateTimeFormat(validLocale(language), { timeStyle: 'short', timeZone: 'UTC', ...(format ? HOUR_CYCLES[format] : {}) }).format(SAMPLE)

/** An example of a date format (none: the language's own) in `language`, for a menu that offers it. */
export const sampleDate = (language: string, format: DateFormat | null) =>
  format ? numericDate(SAMPLE, format, 'UTC') : new Intl.DateTimeFormat(validLocale(language), { dateStyle: 'medium', timeZone: 'UTC' }).format(SAMPLE)

const DISTANCE_UNITS: Record<DistanceUnit, 'kilometer' | 'mile'> = { KILOMETERS: 'kilometer', MILES: 'mile' }

/**
 * Dates, numbers, money and measurements in the selected UI language, always through Intl. Numbers are stored exactly as
 * entered in the vehicle's own units, so the unit decides how a value is shown, never a conversion.
 */
export function useFormat() {
  const { i18n, t } = useTranslation()
  // The tag Intl takes: a language odd enough to make it throw would take every page with a number down (validLocale).
  const language = validLocale(i18n.language, i18n.resolvedLanguage)
  // The user's time and date formats, else the instance's, else the language's own.
  const formats = useFormats()
  const timeFormat = formats.time ?? formats.defaultTime
  const dateFormat = formats.date ?? formats.defaultDate

  return useMemo(() => {
    const number = (value: number, options?: Intl.NumberFormatOptions) => new Intl.NumberFormat(language, options).format(value)
    const clock = timeFormat ? HOUR_CYCLES[timeFormat] : {}
    return {
      number,
      /** Short form for axes: 12 500 becomes 12.5K (or 12,5 E in Hungarian). */
      compact: (value: number) => new Intl.NumberFormat(language, { notation: 'compact', maximumFractionDigits: 1 }).format(value),
      /** The same with its currency, for an axis of one currency's amounts: 40,000 HUF becomes "HUF 40K" ("40 E Ft"); an unknown code "40K XYZ". */
      compactMoney: (value: number, currency: string) => {
        const compact = { notation: 'compact', maximumFractionDigits: 1 } as const
        try {
          return new Intl.NumberFormat(language, { style: 'currency', currency, ...compact }).format(value)
        } catch {
          return `${new Intl.NumberFormat(language, compact).format(value)} ${currency}`
        }
      },
      /** An instant in the browser's time zone, as the date and time formats write it. */
      dateTime: (iso: string) => {
        const at = new Date(iso)
        if (!dateFormat) return new Intl.DateTimeFormat(language, { dateStyle: 'medium', timeStyle: 'short', ...clock }).format(at)
        return `${numericDate(at, dateFormat)} ${new Intl.DateTimeFormat(language, { timeStyle: 'short', ...clock }).format(at)}`
      },
      /** A time of day such as "02:00" the way the time format writes it ("2:00 AM"), as it is: never moved into another time zone. */
      timeOfDay: (hhmm: string) => {
        const match = /^(\d{1,2}):(\d{2})$/.exec(hhmm)
        if (!match) return hhmm
        return new Intl.DateTimeFormat(language, { timeStyle: 'short', timeZone: 'UTC', ...clock }).format(new Date(Date.UTC(2000, 0, 1, Number(match[1]), Number(match[2]))))
      },
      /** A day of the week by its English name in any case ("Monday", "MONDAY") as the language writes it ("hétfő"). */
      weekday: (day: string, width: 'long' | 'short' = 'long') => {
        const index = WEEK.indexOf(day.toLowerCase())
        if (index < 0) return day
        return new Intl.DateTimeFormat(language, { weekday: width, timeZone: 'UTC' }).format(new Date(Date.UTC(2024, 0, 1 + index))) // 1 January 2024 was a Monday
      },
      /** A size on disk in the unit that suits it (1.2 GB, 340 kB), counted in thousands like the browser's storage figures. */
      bytes: (value: number) => {
        const units = ['byte', 'kilobyte', 'megabyte', 'gigabyte', 'terabyte'] as const
        // The figure as it is shown (whole bytes, else one decimal): one that rounds up to 1,000 goes to the next unit (999,960 bytes is 1 MB).
        const shown = (v: number, unit: number) => (unit === 0 ? Math.round(v) : Math.round(v * 10) / 10)
        let index = 0
        while (shown(value, index) >= 1000 && index < units.length - 1) {
          value /= 1000
          index++
        }
        return number(value, { style: 'unit', unit: units[index], unitDisplay: 'short', maximumFractionDigits: index === 0 ? 0 : 1 })
      },
      /** Words joined the way the language lists them ("odometer and total"). */
      list: (items: string[]) => new Intl.ListFormat(language, { type: 'conjunction' }).format(items),
      /** Choices joined the way the language offers them ("Standalone or Oidc"). */
      either: (items: string[]) => new Intl.ListFormat(language, { type: 'disjunction' }).format(items),
      /** A calendar date such as "2026-09-01" (no time zone shifts it to another day). */
      date: (isoDate: string) => {
        const day = new Date(`${isoDate}T00:00:00Z`)
        return dateFormat ? numericDate(day, dateFormat, 'UTC') : new Intl.DateTimeFormat(language, { dateStyle: 'medium', timeZone: 'UTC' }).format(day)
      },
      /** Amount in the currency it was paid in; an unknown code falls back to "12.50 XYZ". */
      money: (amount: number, currency: string) => {
        try {
          return new Intl.NumberFormat(language, { style: 'currency', currency }).format(amount)
        } catch {
          return `${number(amount, { minimumFractionDigits: 2 })} ${currency}`
        }
      },
      distance: (value: number, unit: DistanceUnit) =>
        new Intl.NumberFormat(language, { style: 'unit', unit: DISTANCE_UNITS[unit], unitDisplay: 'short', maximumFractionDigits: 0 }).format(value),
      volume: (value: number, unit: VolumeUnit) => `${number(value, { maximumFractionDigits: 2 })} ${t(`units.volumeShort.${unit}`)}`,
      /** Fuel used per 100 distance units, in the vehicle's own units; miles with gallons read as miles per gallon, the way those countries say it. */
      consumption: (per100: number, units: { distance: DistanceUnit; volume: VolumeUnit }) => {
        const mpg = per100 > 0 && units.distance === 'MILES' && units.volume !== 'LITERS'
        if (mpg) return t(units.volume === 'US_GALLONS' ? 'units.consumption.mpgUs' : 'units.consumption.mpgImperial', { value: number(100 / per100, { maximumFractionDigits: 1 }) })
        return t('units.consumption.perDistance', {
          value: number(per100, { maximumFractionDigits: 2 }),
          volume: t(`units.volumeShort.${units.volume}`),
          distance: t(`units.distanceShort.${units.distance}`),
        })
      },
      /** Money per 100 of the vehicle's distance units ("HUF 4,500 / 100 km"); an unknown code falls back like `money`. */
      costPerDistance: (value: number, currency: string, unit: DistanceUnit) => {
        let amount: string
        try {
          amount = new Intl.NumberFormat(language, { style: 'currency', currency }).format(value)
        } catch {
          amount = `${number(value, { minimumFractionDigits: 2 })} ${currency}`
        }
        return t('units.costPerDistance', { amount, distance: t(`units.distanceShort.${unit}`) })
      },
      pricePerUnit: (value: number, currency: string, unit: VolumeUnit) => `${(() => {
        try {
          return new Intl.NumberFormat(language, { style: 'currency', currency, maximumFractionDigits: 3 }).format(value)
        } catch {
          return `${number(value, { maximumFractionDigits: 3 })} ${currency}`
        }
      })()} / ${t(`units.volumeShort.${unit}`)}`,
    }
  }, [language, t, timeFormat, dateFormat])
}

/** Parses what a person typed: both "12.5" and "12,5" are accepted (many languages write decimals with a comma). */
export function parseDecimal(text: string): number | undefined {
  const normalized = text.trim().replace(',', '.')
  if (!/^\d+(\.\d+)?$/.test(normalized)) return undefined
  return Number(normalized)
}
