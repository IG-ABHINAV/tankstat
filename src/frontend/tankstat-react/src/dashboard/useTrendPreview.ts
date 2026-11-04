import { useQuery } from '@apollo/client/react'
import { todayIso } from '../forms/dates.ts'
import { VehicleTrendDocument } from '../gql/generated.ts'
import { periodProblem, toInput, TREND_FIGURE, type TrendFigureId, type TrendPeriod } from './trendPeriod.ts'

/**
 * A trend figure over a period being chosen (#230), as the builder's preview asks for a chart: refetched as the period changes, not asked
 * while the period is not valid. The popover's timeline and sentence and the Add widget dialog's tile read the same answer.
 */
export function useTrendPreview(figure: TrendFigureId, vehicleId: string, period: TrendPeriod) {
  const problem = periodProblem(period, todayIso())
  const { data, error } = useQuery(VehicleTrendDocument, {
    variables: { vehicleId, figure: TREND_FIGURE[figure], period: toInput(period) },
    fetchPolicy: 'cache-and-network',
    skip: problem !== null,
  })
  return { problem, data: problem ? undefined : data?.vehicleTrend, error: problem ? undefined : error }
}
