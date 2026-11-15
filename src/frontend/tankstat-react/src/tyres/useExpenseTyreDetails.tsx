import { lazy, Suspense, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { LabeledSwitch } from '../components/LabeledSwitch.tsx'
import { Loading } from '../components/Loading.tsx'
import type { ExpenseTyreInput, TyreEntryKind, VehicleTyresFieldsFragment } from '../gql/generated.ts'
import { isTyreCategory, tyreCategories } from './tyreCategory.ts'

// The section, the car picture and the input's making are a chunk of their own (the dialogs are eager: the home cards open them).
const loadSection = () => import('./ExpenseTyreSection.tsx')
const ExpenseTyreSection = lazy(loadSection)

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
export function useExpenseTyreDetails({ tyres, category, setCategory }: { tyres: VehicleTyresFieldsFragment | null | undefined; category: string; setCategory: (category: string) => void }) {
  const { t } = useTranslation()
  const available = !!tyres && tyres.sets.length > 0
  const last = tyres?.expenseCategory ?? null
  const [state, setState] = useState<TyreDetailsState>(() => {
    const sets = tyres?.sets.filter((s) => !s.set.isRetired) ?? []
    return { on: null, kind: '', setId: (sets.find((s) => s.onTheCar) ?? sets[0])?.set.id ?? '', whole: true, picked: [], labels: [] }
  })
  const open = available && (state.on === true || (state.on !== false && isTyreCategory(category, last)))
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
    if (!state.kind) {
      setKindError(t('expenses.tyreDetails.chooseKind'))
      kindRef.current?.focus()
      return 'invalid'
    }
    return (await loadSection()).tyreInputOf(state, tyres, ids, day)
  }

  return { available, open, state, change, toggle, kindError, kindRef, title, announcement, inputOf, last }
}

export type ExpenseTyreDetails = ReturnType<typeof useExpenseTyreDetails>

/** The switch and, while it is on, the section (a lazy chunk with a boundary of its own, so the fields above it stay as they are). */
export function TyreDetailsFields({ details, tyres, hint, note }: { details: ExpenseTyreDetails; tyres: VehicleTyresFieldsFragment; hint: string; note: string }) {
  const { t } = useTranslation()
  if (!details.available) return null
  return (
    <>
      <LabeledSwitch label={t('expenses.tyreDetails.switch')} hint={hint} checked={details.open} onChange={details.toggle} />
      {details.open && (
        <Suspense fallback={<Loading />}>
          <ExpenseTyreSection tyres={tyres} state={details.state} onChange={details.change} kindError={details.kindError} kindRef={details.kindRef} note={note} />
        </Suspense>
      )}
    </>
  )
}

/** The switch's hint in *Add expense* and *Edit expense* (decisions 51, 54): it names the tyre categories that open it by itself. */
export function useSwitchHint(details: ExpenseTyreDetails, editing: boolean) {
  const { t } = useTranslation()
  return editing ? t('expenses.tyreDetails.hintEdit') : t('expenses.tyreDetails.hintAdd', { list: tyreCategories(details.last).join(', ') })
}
