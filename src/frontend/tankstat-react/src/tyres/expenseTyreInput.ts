import type { ExpenseTyreInput, TyreEntryKind } from '../gql/generated.ts'
import { willKeep } from '../offline/submitChange.ts'
import { ENTRY_KINDS } from './entries.ts'
import { applyTyreChange } from './replay.ts'
import { EXACT, liveTyresOf, suggestLabels } from './singleTyres.ts'
import { TyreRuleError } from './tyreHistory.ts'
import type { Tyres } from './tyres.ts'
import type { TyreDetailsIds, TyreDetailsState } from './useExpenseTyreDetails.ts'

// What the Expense dialog's tyre details (#290) send and check: loaded with the section, never with the dialog.


/** What an expense's line may say: a kind that takes an amount (a note takes none; the swap kinds come with the swap). */
export const EXPENSE_KINDS: readonly TyreEntryKind[] = ENTRY_KINDS.filter((k) => k !== 'NOTE')

/** The sets an expense's line may name: those not retired (a set in storage too: a storage fee). */
export const offered = (tyres: Tyres) => tyres.sets.filter((s) => !s.set.isRetired)

/** A whole set's labels as typed, else the next free ones (as *Add entry* and the break-up suggest). */
export function labelsOf(state: TyreDetailsState, tyres: Tyres): string[] {
  const info = offered(tyres).find((s) => s.set.id === state.setId)
  if (state.labels.length > 0 || !info || info.set.brokenUpOn) return state.labels
  return suggestLabels(tyres, info.set.season)
}

/**
 * An expense change with tyre details that will wait on the device (#290, the owner's decision 70): its tyre part replayed over the tyres
 * shown first, so a refusal the device can foresee is said in the dialog (thrown as the server's key), not parked later. Sent at once, the
 * server judges it.
 */
export function checkKept(
  vehicleId: string,
  tyres: Tyres,
  change: { entity: 'expenses'; action: 'add' | 'update'; targetId: string } | { entity: 'recurring'; action: 'markDone'; targetId: string },
  input: Record<string, unknown>,
) {
  if (!willKeep(vehicleId)) return
  applyTyreChange(tyres, { id: 'check', seq: 0, createdAt: 0, ...change, vehicleId, input })
}

/**
 * The tyre details as the server takes them (#290), and the tyres they name for the toast. *The whole set* (or no set) is one line; tyres
 * tapped are a line each, a whole set broken up on save first (the owner's decision 19) on the expense's day, each tyre at its corner.
 */
export function tyreInputOf(
  state: TyreDetailsState,
  tyres: Tyres,
  ids: TyreDetailsIds,
  day: string,
  /**
   * The break-up a change edited on Waiting to sync carries (decision 70): on the device its set shows broken up already (the change is
   * replayed), its tyres the ones that break-up makes, so a line for them still needs it sent along.
   */
  waitingBreakUp?: ExpenseTyreInput['breakUp'],
): { tyre: ExpenseTyreInput; named: string } {
  if (!state.kind) throw new Error('A kind is chosen before the input is made.')
  const info = offered(tyres).find((s) => s.set.id === state.setId)
  if (!info || state.whole) return { tyre: { kind: state.kind, setId: info?.set.id ?? null, lineId: ids.line }, named: '' }
  if (state.picked.length === 0) throw new TyreRuleError('tyres.tapTyres')
  if (info.set.brokenUpOn && waitingBreakUp?.tyres.some((t) => state.picked.includes(t.id ?? ''))) {
    // Its own break-up's tyres, picked by their ids: the break-up goes along, in the car's order.
    const ordered = EXACT.flatMap((c) => waitingBreakUp.tyres.filter((t) => t.corner === c && state.picked.includes(t.id ?? '')))
    return {
      tyre: { kind: state.kind, setId: info.set.id, breakUp: waitingBreakUp, tyres: ordered.map((t, n) => ({ tyreId: t.id!, id: ids.lines[n] })) },
      named: ordered.map((t) => t.label).join(', '),
    }
  }
  if (info.set.brokenUpOn) {
    const ordered = liveTyresOf(info).filter((x) => state.picked.includes(x.tyre.id))
    return {
      tyre: { kind: state.kind, setId: info.set.id, tyres: ordered.map((x, n) => ({ tyreId: x.tyre.id, id: ids.lines[n] })) },
      named: ordered.map((x) => x.tyre.label).join(', '),
    }
  }
  const labels = labelsOf(state, tyres)
  const breakUp = { setId: info.set.id, day, tyres: EXACT.map((corner, n) => ({ id: ids.newTyres[n], label: (labels[n] ?? '').trim(), corner })) }
  const ordered = EXACT.filter((c) => state.picked.includes(c))
  return {
    tyre: { kind: state.kind, setId: info.set.id, breakUp, tyres: ordered.map((c, n) => ({ tyreId: ids.newTyres[EXACT.indexOf(c)], id: ids.lines[n] })) },
    named: ordered.map((c) => (labels[EXACT.indexOf(c)] ?? '').trim()).join(', '),
  }
}
