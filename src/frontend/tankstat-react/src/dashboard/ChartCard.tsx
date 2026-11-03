import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Card from '@mui/material/Card'
import Chip from '@mui/material/Chip'
import Skeleton from '@mui/material/Skeleton'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { visuallyHidden } from '../components/visuallyHidden.ts'
import { ChartDataDocument, type DistanceUnit, type VolumeUnit } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { ChartView, MIN_PICTURE } from './ChartView.tsx'
import { toConfig, type ChartRecipe } from './chartFormat.ts'

/**
 * One chart in a titled card: loads its own data from the server and draws it. Used for the ready-made charts, saved charts and the builder's
 * preview. In a dashboard tile (`fill`) the card takes the tile's height and the picture what is left under the title and above its notes,
 * laid out by the browser rather than worked out here (MUI X draws a legend above the picture and the notes come under it), the table
 * scrolling inside the card once it is opened; on its own the picture is 240 px tall.
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
      <Box sx={fill ? { flex: 1, minHeight: 0, overflow: 'auto', display: 'flex', flexDirection: 'column' } : undefined}>
        {error && <ErrorMessage error={error} />}
        {!data && !error && (
          <>
            <span role="status" style={visuallyHidden}>
              {t('app.loading')}
            </span>
            {/* In a tile, the room the picture will take: the tile does not jump when the data comes. */}
            <Skeleton variant="rounded" aria-hidden {...(fill ? { sx: { flex: 1, minHeight: MIN_PICTURE } } : { height: 240 })} />
          </>
        )}
        {data && <ChartView data={data.vehicleChartData} recipe={recipe} units={units} title={title} fill={fill} />}
      </Box>
    </Card>
  )
}
