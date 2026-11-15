import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { ExpenseTyreInput, TyreEntryKind, VehicleTyresFieldsFragment } from '../gql/generated.ts'
import { isTyreCategory, tyreCategories } from './tyreCategory.ts'

type TyreLine = VehicleTyresFieldsFragment['entries'][number]

/**
 * The section and the car picture (`TyreDetailsFields`), and the input's making (`expenseTyreInput.ts`), are chunks of their own (the dialogs
 * are eager: the home cards open them); the section is loaded ahead once a vehicle has tyre sets.
 */
export const loadSection = () => import('./ExpenseTyreSection.tsx')

/** The tyre details as picked in a dialog (#290); kept while the section is closed, so they come back if it opens again (decision 59). */
export interface TyreDetailsState {
  /** The switch as the person left it: on, off, or not touched (null: the category decides). */
  on: boolean | null
  kind: TyreEntryKind | ''
  /** The set (none: *No set*). */
  setId: string
  whole: boolean
  /** The tyres tapped: their ids on a broken-up set, their corners on a whole one. */
  picked: string[]
  /** A whole set's labels as typed (empty: the suggested ones). */
  labels: string[]
}

/** A whole set's corners in the order its labels are kept (as `singleTyres.ts` `EXACT`, which this eager module does not import). */
export const EXACT_ORDER = ['FRONT_LEFT', 'FRONT_RIGHT', 'REAR_LEFT', 'REAR_RIGHT'] as const

/** The device's ids of what a save makes (see `ExpenseTyreSection`). */
export interface TyreDetailsIds {
  line: string
  lines: readonly string[]
  newTyres: readonly string[]
}

/**
 * An expense's tyre details in a dialog (#290, the owner's decisions 51, 58, 59, 62, 65 and 68): a vehicle with a tyre set gets the switch;
 * the section opens by itself while the category is a tyre category, until the person switches it off, and closes when the category stops
 * being one unless the person switched it on; switched on, an empty category takes the last tyre cost's (else Tyres or Gumik). *What
 * was done* starts unchosen, *Set* at the set on the car. Opening and closing by the category are announced, never moving the focus.
 */
export function useExpenseTyreDetails({
  tyres,
  category,
  setCategory,
  linked,
  initial,
}: {
  tyres: VehicleTyresFieldsFragment | null | undefined
  category: string
  setCategory: (category: string) => void
  /**
   * An expense the tyre log made (#290, decisions 53 and 60): its first live line. The section is shown without the switch, from what
   * that line says; a save sends what was done and the set only when they changed.
   */
  linked?: TyreLine | null
  /**
   * A change edited on Waiting to sync (decision 70): the tyre details it carries, shown to fix or switch off. Its set while it is still
   * offered (else the usual start: one the server refused may be gone), its tyres, a whole set's labels and the break-up it carries.
   */
  initial?: ExpenseTyreInput | null
}) {
  const { t } = useTranslation()
  const available = !!tyres && (tyres.sets.length > 0 || !!linked)
  const last = tyres?.expenseCategory ?? null
  const [state, setState] = useState<TyreDetailsState>(() => {
    if (linked) return { on: true, kind: linked.kind, setId: linked.setId ?? '', whole: true, picked: [], labels: [] }
    const sets = tyres?.sets.filter((s) => !s.set.isRetired) ?? []
    const start = (sets.find((s) => s.onTheCar) ?? sets[0])?.set.id ?? ''
    if (!initial) return { on: null, kind: '', setId: start, whole: true, picked: [], labels: [] }
    const offered = !initial.setId || sets.some((s) => s.set.id === initial.setId)
    // Its set no longer offered: the usual start, the whole set (its tyres were the gone set's, never the start's).
    if (!offered) return { on: true, kind: initial.kind, setId: start, whole: true, picked: [], labels: [] }
    const lines = initial.tyres ?? []
    // A whole set it breaks up, shown whole (a parked change is not replayed here): its tyres picked by their corners, its labels as typed.
    // Shown broken up (a waiting change is replayed, its break-up made), or a broken-up set: by their ids.
    const made = initial.breakUp?.tyres ?? []
    const shownWhole = !tyres?.sets.find((s) => s.set.id === initial.setId)?.set.brokenUpOn
    const byCorner = made.length > 0 && shownWhole
    const picked = byCorner ? made.filter((t) => lines.some((l) => l.tyreId === t.id)).map((t) => t.corner ?? '').filter(Boolean) : lines.map((l) => l.tyreId)
    const labels = byCorner ? EXACT_ORDER.map((corner) => made.find((t) => t.corner === corner)?.label ?? '') : []
    return { on: true, kind: initial.kind, setId: initial.setId ?? '', whole: lines.length === 0, picked, labels }
  })
  const open = available && (!!linked || state.on === true || (state.on !== false && isTyreCategory(category, last)))
  const [kindError, setKindError] = useState<string>()
  const kindRef = useRef<{ focus: () => void }>(null)

  // Opened or closed by the category: said once in the dialog's live region; the switch says its own state.
  const [announcement, setAnnouncement] = useState('')
  const seen = useRef(open)
  const bySwitch = useRef(false)
  useEffect(() => {
    if (seen.current === open) return
    seen.current = open
    if (!bySwitch.current) setAnnouncement(t(open ? 'expenses.tyreDetails.opened' : 'expenses.tyreDetails.closed'))
    bySwitch.current = false
  }, [open, t])
  useEffect(() => {
    if (available) void loadSection() // ready before a tyre category is typed
  }, [available])

  const toggle = (on: boolean) => {
    bySwitch.current = true
    setState((s) => ({ ...s, on }))
    if (on && !category.trim()) setCategory(last?.trim() || t('tyres.fee.defaultCategory'))
  }
  const change = (next: TyreDetailsState) => {
    setState(next)
    if (next.kind) setKindError(undefined)
  }
  const setName = tyres?.sets.find((s) => s.set.id === state.setId)?.set.name ?? null
  /** The expense's title while the person has not typed one (decision 52): "{kind}: {set}", as *Add entry*'s fee. */
  const title = open && state.kind ? (setName ? `${t(`tyres.entries.kinds.${state.kind}`)}: ${setName}` : t(`tyres.entries.kinds.${state.kind}`)) : null

  /**
   * The input to send, worked out on save: undefined while the section is closed, `'invalid'` when no kind was chosen (said at the field,
   * which takes the focus: "Choose what was done."). A tyre that must be tapped and was not throws the tyre log's own refusal.
   */
  async function inputOf(day: string, ids: TyreDetailsIds): Promise<{ tyre: ExpenseTyreInput; named: string } | 'invalid' | undefined> {
    if (!open || !tyres) return undefined
    if (linked) {
      // Its lines change only when what was done or the set did; the set always goes with it (left out, it would be no set).
      const same = state.kind === linked.kind && state.setId === (linked.setId ?? '')
      return same || !state.kind ? undefined : { tyre: { kind: state.kind, setId: state.setId || null }, named: '' }
    }
    if (!state.kind) {
      setKindError(t('expenses.tyreDetails.chooseKind'))
      kindRef.current?.focus()
      return 'invalid'
    }
    return (await import('./expenseTyreInput.ts')).tyreInputOf(state, tyres, ids, day, initial?.setId === state.setId ? initial.breakUp : null)
  }

  return { available, open, state, change, toggle, kindError, kindRef, title, announcement, inputOf, last, linked: linked ?? null }
}

export type ExpenseTyreDetails = ReturnType<typeof useExpenseTyreDetails>

/** The switch's hint in *Add expense* and *Edit expense* (decisions 51, 54): it names the tyre categories that open it by itself. */
export function useSwitchHint(details: ExpenseTyreDetails, editing: boolean) {
  const { t } = useTranslation()
  return editing ? t('expenses.tyreDetails.hintEdit') : t('expenses.tyreDetails.hintAdd', { list: tyreCategories(details.last).join(', ') })
}
