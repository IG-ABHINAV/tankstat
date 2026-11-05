import { useQuery } from '@apollo/client/react'
import { todayIso } from '../forms/dates.ts'
import { VehicleFigureDocument, type FigureMeasure } from '../gql/generated.ts'
import { periodProblem, toInput, type TrendPeriod } from './trendPeriod.ts'

/**
 * A key figure over a period being chosen (#230, #274), as the builder's preview asks for a chart: refetched as the period changes, not
 * asked while the period is not valid. The popover's timeline and sentence and the Add widget dialog's tile read the same answer.
 */
export function useFigurePreview(measure: FigureMeasure, vehicleId: string, period: TrendPeriod) {
  const problem = periodProblem(period, todayIso())
  const { data, error } = useQuery(VehicleFigureDocument, {
    variables: { vehicleId, measure, period: toInput(period) },
    fetchPolicy: 'cache-and-network',
    skip: problem !== null,
  })
  return { problem, data: problem ? undefined : data?.vehicleFigure, error: problem ? undefined : error }
}
