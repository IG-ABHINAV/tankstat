import type { ReadingFieldName, ReadingIssueCode } from '../gql/generated.ts'
import type { ReadValues } from './readValues.ts'

/** Why a photo gave less than it might have: about one value (`field`), or about the photo as a whole (none). Codes only, never a value. */
export interface ReadingIssue {
  code: ReadingIssueCode
  field: ReadingFieldName | null
}

/** What the open dialog knows about why its photos gave less than they might have. */
export interface ReadingExplanation {
  /** The reasons of the finished readings, each once. */
  issues: readonly ReadingIssue[]
  /** A reading failed: the photo could not be read at all. */
  failed: boolean
}

interface ReadingLike {
  status: string
  issues: readonly ReadingIssue[]
}

/** The reasons of every finished reading, each (reason, field) once, in the order they came. */
export function mergeIssues(readings: Iterable<ReadingLike | null | undefined>): ReadingIssue[] {
  const seen = new Set<string>()
  const merged: ReadingIssue[] = []
  for (const reading of readings) {
    if (reading?.status !== 'READ') continue
    for (const { code, field } of reading.issues) {
      const key = `${code}:${field ?? ''}`
      if (seen.has(key)) continue
      seen.add(key)
      merged.push({ code, field })
    }
  }
  return merged
}

/** Reasons that only say the model was not sure of a value: told when nothing more specific explains the field. */
const NOT_SURE: ReadonlySet<ReadingIssueCode> = new Set(['NO_CONFIDENCE', 'UNSURE'])

/** Reasons whose sentence starts with the field's name; the others say what they mean on their own (the odometer, the date). */
const NAMES_THE_FIELD: ReadonlySet<ReadingIssueCode> = new Set(['NOT_UNDERSTOOD', 'OUT_OF_RANGE', 'AMOUNTS_DO_NOT_ADD', 'NO_CONFIDENCE', 'UNSURE'])

/** Reasons about several values at once (the litres and the price that do not add up to the total; the total and its discount): told once, about the first. */
const SHARED: ReadonlySet<ReadingIssueCode> = new Set(['AMOUNTS_DO_NOT_ADD', 'DISCOUNT_NOT_TAKEN_OFF'])

/** A reason to tell, with the name of its field in the dialog where the sentence starts with it. */
export interface Problem {
  code: ReadingIssueCode
  field?: string
}

/**
 * The reasons worth telling in a dialog the photos were read for:
 * - one about a value, when the dialog has a field for it and no photo filled that field (another photo may have);
 * - at most one per field, a specific reason before "not sure enough";
 * - one about the photo as a whole (nothing recognised, nothing legible) only when nothing was filled at all.
 *
 * @param fieldLabel the field's name in the dialog; undefined where the dialog has no such field (a unit price, say)
 * @param read       what the photos showed and the dialog took, as values sure enough
 */
export function problemsToTell(issues: readonly ReadingIssue[], fieldLabel: (name: ReadingFieldName) => string | undefined, read: ReadValues): Problem[] {
  const byField = new Map<ReadingFieldName, ReadingIssue[]>()
  for (const issue of issues) {
    if (issue.field === null || fieldLabel(issue.field) === undefined || read[issue.field] !== undefined) continue
    byField.set(issue.field, [...(byField.get(issue.field) ?? []), issue])
  }
  const told: Problem[] = []
  for (const [field, list] of byField) {
    const chosen = list.find((i) => !NOT_SURE.has(i.code)) ?? list[0]!
    told.push({ code: chosen.code, field: NAMES_THE_FIELD.has(chosen.code) ? fieldLabel(field) : undefined })
  }
  if (Object.keys(read).length === 0) {
    const whole = issues.find((i) => i.field === null && i.code === 'UNRECOGNISED') ?? issues.find((i) => i.field === null)
    if (whole) told.push({ code: whole.code })
  }
  // Two fields can come to the same sentence, and a reason about several values is told once even where the dialog has a field for each.
  return told.filter((p, i) => told.findIndex((q) => q.code === p.code && (q.field === p.field || SHARED.has(p.code))) === i)
}
