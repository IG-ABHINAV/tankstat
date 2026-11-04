import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Card from '@mui/material/Card'
import Chip from '@mui/material/Chip'
import Skeleton from '@mui/material/Skeleton'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import type { ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useTranslation } from 'react-i18next'
import { visuallyHidden } from '../components/visuallyHidden.ts'
import { ChartDataDocument, type DistanceUnit, type VolumeUnit } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { ChartTableDialog } from './ChartTable.tsx'
import { ChartView, MIN_PICTURE, PICTURE_HEIGHT } from './ChartView.tsx'
import { useTilePadding, useTileStrip } from './tileChrome.ts'
import { toConfig, type ChartRecipe } from './chartFormat.ts'

/**
 * One chart in a titled card: loads its own data from the server and draws it, its numbers as a table behind a button in its header (#228).
 * Used for the ready-made charts, saved charts and the builder's preview. In a dashboard tile (`fill`) the card takes the tile's height and the picture what is left under the title and above its notes,
 * laid out by the browser rather than worked out here (MUI X draws a legend above the picture and the notes come under it); on its own
 * the picture is 240 px tall.
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
  // Under the edit strip while the dashboard is arranged, only for the card that fills its tile: the context also reaches a preview in a
  // dialog opened from a tile (the Edit chart dialog), which is portalled out of the tile but not out of the React tree.
  const pt = useTilePadding(2)
  // While the dashboard is arranged the tile's strip is its header (#262): it names the tile (the heading) and carries the buttons, so the
  // card is as tall as in view mode and its picture keeps its size. Only the card that fills its tile: a dialog opened from one is outside.
  const strip = useTileStrip()
  const inStrip = fill && strip !== null
  const buttons = (
    <>
      {data && <ChartTableDialog data={data.vehicleChartData} recipe={recipe} units={units} title={title} />}
      {actions}
    </>
  )
  // Who sees and made the chart: under its title, or under the strip while arranging (kept there, #262).
  const byline = (
    <Stack direction="row" sx={{ gap: 1, alignItems: 'center', flexWrap: 'wrap' }}>
      {shared && <Chip color="primary" label={t('dashboard.shared')} />}
      {meta}
    </Stack>
  )

  return (
    <Card sx={fill ? { p: 2, pt, height: '100%', display: 'flex', flexDirection: 'column', minHeight: 0, boxSizing: 'border-box' } : { p: 2 }}>
      {inStrip ? (
        <>
          {strip.actions && createPortal(buttons, strip.actions)}
          {(shared || meta) && <Box sx={{ mb: 1.5 }}>{byline}</Box>}
        </>
      ) : (
        <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'flex-start', gap: 1.5, mb: 1.5 }}>
          <Stack sx={{ gap: 0.5, minWidth: 0 }}>
            <Typography component={`h${headingLevel}`} variant="h6">
              {title}
            </Typography>
            {byline}
          </Stack>
          <Stack direction="row" sx={{ gap: 1, flexShrink: 0 }}>
            {buttons}
          </Stack>
        </Stack>
      )}
      <Box sx={fill ? { flex: 1, minHeight: 0, overflow: 'auto', display: 'flex', flexDirection: 'column' } : undefined}>
        {error && <ErrorMessage error={error} />}
        {!data && !error && (
          <>
            <span role="status" style={visuallyHidden}>
              {t('app.loading')}
            </span>
            {/* In a tile, the room the picture will take: the tile does not jump when the data comes. */}
            <Skeleton variant="rounded" aria-hidden {...(fill ? { sx: { flex: 1, minHeight: MIN_PICTURE } } : { height: PICTURE_HEIGHT })} />
          </>
        )}
        {data && <ChartView data={data.vehicleChartData} recipe={recipe} units={units} title={title} fill={fill} />}
      </Box>
    </Card>
  )
}
