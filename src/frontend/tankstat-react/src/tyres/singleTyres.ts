import { useTranslation } from 'react-i18next'
import type { Tyres, TyreSetInfo } from './tyres.ts'

/** A single tyre of a broken-up set (#289), as the Tyres tab reads it. */
export type TyreInfo = TyreSetInfo['tyres'][number]

export type ExactCorner = 'FRONT_LEFT' | 'FRONT_RIGHT' | 'REAR_LEFT' | 'REAR_RIGHT'
export type AnyCorner = ExactCorner | 'FRONT' | 'REAR'

export const EXACT: readonly ExactCorner[] = ['FRONT_LEFT', 'FRONT_RIGHT', 'REAR_LEFT', 'REAR_RIGHT']
export const ALL_CORNERS: readonly AnyCorner[] = [...EXACT, 'FRONT', 'REAR']
export const isExactCorner = (c: string | null | undefined): c is ExactCorner => !!c && (EXACT as readonly string[]).includes(c)

/** Front ↔ rear on the same side. */
export const CROSS: Record<ExactCorner, ExactCorner> = { FRONT_LEFT: 'REAR_LEFT', REAR_LEFT: 'FRONT_LEFT', FRONT_RIGHT: 'REAR_RIGHT', REAR_RIGHT: 'FRONT_RIGHT' }

/** The words of a corner: "front left", "front, side not recorded", "corner not recorded"; `title` the four as a heading ("Front left"). */
export function useCornerWords() {
  const { t } = useTranslation()
  return {
    of: (corner: string | null | undefined) => t(`tyres.single.corners.${(corner ?? 'none') as AnyCorner | 'none'}`),
    title: (corner: ExactCorner) => t(`tyres.single.cornerTitles.${corner}`),
  }
}

const PREFIX: Record<string, string> = { SUMMER: 'S', WINTER: 'W', ALL_SEASON: 'A' }

/**
 * The labels a break-up suggests (the owner's decision 28): S, W or A by the season, then the next numbers no tyre of the vehicle not in the
 * trash has (S1–S4, then S5…). Only a starting value: the person may write anything.
 */
export function suggestLabels(tyres: Tyres, season: string, count = 4): string[] {
  const prefix = PREFIX[season] ?? 'T'
  const taken = new Set(tyres.sets.flatMap((s) => s.tyres ?? []).map((t) => t.tyre.label.toUpperCase()))
  const labels: string[] = []
  for (let n = 1; labels.length < count; n++) if (!taken.has(`${prefix}${n}`)) labels.push(`${prefix}${n}`)
  return labels
}

/** The set's tyres that are not retired. */
export const liveTyresOf = (info: TyreSetInfo | undefined) => (info?.tyres ?? []).filter((t) => !t.tyre.isRetired)
