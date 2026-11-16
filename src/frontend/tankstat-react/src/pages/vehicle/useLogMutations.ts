import { useMutation } from '@apollo/client/react'
import { savedFrom, type Saved } from '../../components/usePhotoQueue.ts'
import type { ExpenseValues } from '../../ExpenseFormDialog.tsx'
import { AddExpenseDocument, LogRefuelingDocument } from '../../gql/generated.ts'
import type { RefuelingValues } from '../../RefuelingFormDialog.tsx'
import { useLogChange } from '../../offline/useLogChange.ts'
import { uuidV4 } from '../../offline/uuid.ts'

/** What a new, changed or trashed refuelling or expense makes the vehicle page ask again (only the queries that are showing). */
export const REFUELING_QUERIES = ['Refuelings', 'VehicleDetails', 'LogDefaults']
/** `VehicleTyres` too: an expense the tyre log made, or one saved with tyre details (#290), changes the tyre log and its costs. */
export const EXPENSE_QUERIES = ['Expenses', 'VehicleDetails', 'LogDefaults', 'ExpenseCategories', 'VehicleTyres']

/**
 * Adding a refuelling or an expense on the vehicle page, from its tabs and from the floating add button alike: once saved, the lists and
 * the vehicle's figures on the page are asked again (a tab that is not showing asks when it is opened).
 */
export function useLogMutations(vehicleId: string) {
  const [logRefueling] = useMutation(LogRefuelingDocument, { refetchQueries: REFUELING_QUERIES, awaitRefetchQueries: true })
  const [addExpense] = useMutation(AddExpenseDocument, { refetchQueries: EXPENSE_QUERIES, awaitRefetchQueries: true })
  const refuelings = useLogChange('refuelings', vehicleId)
  const expenses = useLogChange('expenses', vehicleId)
  return {
    // Kept on the device while the server is out of reach (`offline/submitChange.ts`); the id is the dialog's, so it never logs twice.
    refuel: async (values: RefuelingValues, photoIds: string[]): Promise<Saved> => {
      const input = { ...values, id: values.id ?? uuidV4(), vehicleId, photoIds }
      const done = await refuelings.add(input.id, input, () => logRefueling({ variables: { input } }))
      return done.queued ? { id: input.id, photoCount: photoIds.length, queued: true } : savedFrom(done.result.data?.logRefueling)
    },
    expense: async (values: ExpenseValues, photoIds: string[]): Promise<Saved> => {
      const input = { ...values, id: values.id ?? uuidV4(), vehicleId, photoIds }
      const done = await expenses.add(input.id, input, () => addExpense({ variables: { input } }))
      return done.queued ? { id: input.id, photoCount: photoIds.length, queued: true } : savedFrom(done.result.data?.addExpense)
    },
  }
}
