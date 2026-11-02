import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Card from '@mui/material/Card'
import Chip from '@mui/material/Chip'
import Skeleton from '@mui/material/Skeleton'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { visuallyHidden } from '../components/visuallyHidden.ts'
import { ChartDataDocument, type DistanceUnit, type VolumeUnit } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { ChartView } from './ChartView.tsx'
import { toConfig, type ChartRecipe } from './chartFormat.ts'

/** The room under a chart's picture for its table toggle and notes, and the least a picture gets, in a card that fills its tile. */
const BELOW_PICTURE = 44
const MIN_PICTURE = 120

/** How tall an element is now (0 until it is laid out, and in jsdom). */
function useHeight(): [React.RefObject<HTMLDivElement | null>, number] {
  const ref = useRef<HTMLDivElement>(null)
  const [height, setHeight] = useState(0)
  useEffect(() => {
    const element = ref.current
    if (!element || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(([entry]) => setHeight(Math.round(entry.contentRect.height)))
    observer.observe(element)
    return () => observer.disconnect()
  }, [])
  return [ref, height]
}

/**
 * One chart in a titled card: loads its own data from the server and draws it. Used for the ready-made charts, saved charts and the builder's
 * preview. In a dashboard tile (`fill`) the card takes the tile's height and the picture what is left under the title, the table scrolling
 * inside it; on its own the picture is 240 px tall.
 */
export function ChartCard({
  vehicleId,
  title,
  recipe,
  units,
  shared,
  meta,
  actions,
  headingLevel = 3,
  fill = false,
}: {
  vehicleId: string
  title: string
  recipe: ChartRecipe
  units: { distance: DistanceUnit; volume: VolumeUnit }
  shared?: boolean
  meta?: ReactNode
  actions?: ReactNode
  headingLevel?: 2 | 3
  fill?: boolean
}) {
  const { t } = useTranslation()
  const { data, error } = useQuery(ChartDataDocument, { variables: { vehicleId, config: toConfig(recipe) }, fetchPolicy: 'cache-and-network' })
  const [body, bodyHeight] = useHeight()
  const height = fill && bodyHeight > 0 ? Math.max(MIN_PICTURE, bodyHeight - BELOW_PICTURE) : 240

  return (
    <Card sx={fill ? { p: 2, height: '100%', display: 'flex', flexDirection: 'column', minHeight: 0, boxSizing: 'border-box' } : { p: 2 }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'flex-start', gap: 1.5, mb: 1.5 }}>
        <Stack sx={{ gap: 0.5, minWidth: 0 }}>
          <Typography component={`h${headingLevel}`} variant="h6">
            {title}
          </Typography>
          <Stack direction="row" sx={{ gap: 1, alignItems: 'center', flexWrap: 'wrap' }}>
            {shared && <Chip color="primary" label={t('dashboard.shared')} />}
            {meta}
          </Stack>
        </Stack>
        {actions}
      </Stack>
      <Box ref={body} sx={fill ? { flex: 1, minHeight: 0, overflow: 'auto' } : undefined}>
        {error && <ErrorMessage error={error} />}
        {!data && !error && (
          <>
            <span role="status" style={visuallyHidden}>
              {t('app.loading')}
            </span>
            <Skeleton variant="rounded" height={height} aria-hidden />
          </>
        )}
        {data && <ChartView data={data.vehicleChartData} recipe={recipe} units={units} title={title} height={height} />}
      </Box>
    </Card>
  )
}
