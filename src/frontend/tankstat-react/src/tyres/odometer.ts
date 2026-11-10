/**
 * The odometer of a day, worked out from the vehicle's readings exactly as the server does (`TyreCalculator.OdometerOn`, #287): the highest
 * reading of that day, else a straight line between the nearest readings before and after it (approximate), else after the latest reading
 * the latest one (approximate), and before the first reading it is not known. Only shown (a form's hint, a tyre log line), never saved.
 */
export interface Reading {
  date: string
  value: number
}

export interface OdometerOnDay {
  value: number
  approximate: boolean
}

const dayNumber = (iso: string) => Math.round(Date.parse(`${iso}T00:00:00Z`) / 86_400_000)

/** By date, then by value (as the server sorts them). */
export const sortedReadings = (readings: readonly Reading[]): Reading[] =>
  [...readings].sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : a.value - b.value))

export function odometerOn(sorted: readonly Reading[], day: string): OdometerOnDay | null {
  if (sorted.length === 0 || day < sorted[0].date) return null
  let before: Reading | undefined
  let after: Reading | undefined
  let sameDay: number | undefined
  for (const r of sorted) {
    if (r.date === day) sameDay = sameDay === undefined ? r.value : Math.max(sameDay, r.value)
    else if (r.date < day) before = r
    else {
      after = r
      break
    }
  }
  if (sameDay !== undefined) return { value: sameDay, approximate: false }
  if (!after) return { value: before!.value, approximate: true }
  const prev = before!
  const span = dayNumber(after.date) - dayNumber(prev.date)
  const part = dayNumber(day) - dayNumber(prev.date)
  // Rounded half away from zero, as .NET's MidpointRounding.AwayFromZero
  const exact = ((after.value - prev.value) * part) / span
  return { value: prev.value + Math.sign(exact) * Math.round(Math.abs(exact)), approximate: true }
}

/** What a form says about the reading of a day: the car's reading then, the worked-out one, before the log, or no reading at all. */
export type OdometerHint =
  | { kind: 'none' }
  | { kind: 'before'; firstDate: string }
  | { kind: 'exact'; value: number }
  | { kind: 'worked'; value: number }

export function odometerHint(sorted: readonly Reading[], day: string): OdometerHint {
  if (sorted.length === 0) return { kind: 'none' }
  const on = odometerOn(sorted, day)
  if (!on) return { kind: 'before', firstDate: sorted[0].date }
  return on.approximate ? { kind: 'worked', value: on.value } : { kind: 'exact', value: on.value }
}

/** The stored readings next to a day (strictly before and after it), which a reading typed for that day must lie between. */
export function neighbours(sorted: readonly Reading[], day: string): { previous?: Reading; next?: Reading } {
  let previous: Reading | undefined
  let next: Reading | undefined
  for (const r of sorted) {
    if (r.date < day) previous = r
    else if (r.date > day && !next) next = r
  }
  return { previous, next }
}

/** Whole months from one day to another (the set's age), never below 0. */
export function monthsBetween(from: string, to: string): number {
  const [fy, fm, fd] = from.split('-').map(Number)
  const [ty, tm, td] = to.split('-').map(Number)
  return Math.max(0, (ty - fy) * 12 + (tm - fm) - (td < fd ? 1 : 0))
}

/** The first day of the DOT week of manufacture (week w of year y: 1 January plus 7·(w−1) days), as the age's start. */
export function madeDay(week: number, year: number): string {
  const d = new Date(Date.UTC(year, 0, 1 + (week - 1) * 7))
  return d.toISOString().slice(0, 10)
}

/** The DOT made date as the form takes it ("wwyy": 0523 is week 5 of 2023), or null when it is not one. */
export function parseMade(text: string): { week: number; year: number } | null {
  const m = /^(\d{2})(\d{2})$/.exec(text.trim())
  if (!m) return null
  const week = Number(m[1])
  return week >= 1 && week <= 53 ? { week, year: 2000 + Number(m[2]) } : null
}

/** "wwyy" of a set's made date, for a form. */
export const madeText = (week: number | null | undefined, year: number | null | undefined) =>
  week != null && year != null ? `${String(week).padStart(2, '0')}${String(year % 100).padStart(2, '0')}` : ''
