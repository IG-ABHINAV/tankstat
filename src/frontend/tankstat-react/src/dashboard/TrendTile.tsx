import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import ButtonBase from '@mui/material/ButtonBase'
import { useContext, useState } from 'react'
import { createPortal } from 'react-dom'
import { useTranslation } from 'react-i18next'
import { VehicleTrendDocument, type DistanceUnit, type VolumeUnit } from '../gql/generated.ts'
import { COARSE } from '../theme/components.ts'
import { TileInsetContext, TileStripSlotContext } from './tileChrome.ts'
import { periodOf, toInput, TREND_FIGURE, type TrendFigureId, type TrendPeriod } from './trendPeriod.ts'
import { TrendFigure } from './TrendFigure.tsx'
import { TrendPeriodPopover, type PeriodControl } from './TrendPeriodPopover.tsx'
import { usePeriodText } from './usePeriodText.ts'

type Units = { distance: DistanceUnit; volume: VolumeUnit }

/**
 * A trend figure over its tile's period (#230): its own `vehicleTrend` answer, its value, and the comparison worded from the period and the
 * answer. Without a period of its own, its figure's default, which reads as the figure always did. The period's chip is shown only while the
 * dashboard is arranged (#260), on the tile's edit strip (`TileStripSlotContext`), a button that opens the period's popover; outside
 * Customize the card has no chip, and its arrow says the period instead.
 */
export function TrendTile({ figure, vehicleId, period, rows, units, control }: { figure: TrendFigureId; vehicleId: string; period: TrendPeriod | null | undefined; rows: number; units: Units; control?: PeriodControl }) {
  const { t } = useTranslation()
  const p = periodOf(figure, period)
  const { data, error } = useQuery(VehicleTrendDocument, { variables: { vehicleId, figure: TREND_FIGURE[figure], period: toInput(p) }, fetchPolicy: 'cache-and-network' })
  const periodText = usePeriodText()
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const label = t(`dashboard.kpi.${figure}`)
  const words = periodText(figure, p)
  // Null outside Customize (and `control` undefined), so the chip is only ever on a strip being shown.
  const slot = useContext(TileStripSlotContext)
  const chip = control && slot && createPortal(<PeriodChip words={words} name={t('dashboard.period.chipName', { figure: label, period: words })} open={anchor !== null} onClick={(e) => setAnchor(e.currentTarget)} />, slot)
  // The popover's preview is no tile under a strip (`TileInsetContext` off): it keeps a card's own padding, not the strip's inset.
  const popover = control && anchor && (
    <TileInsetContext.Provider value={false}>
      <TrendPeriodPopover anchor={anchor} figure={figure} vehicleId={vehicleId} units={units} period={p} control={control} onClose={() => setAnchor(null)} />
    </TileInsetContext.Provider>
  )
  return (
    <>
      <TrendFigure figure={figure} period={p} data={data?.vehicleTrend} error={error} rows={rows} units={units} label={label} periodWords={words} />
      {chip}
      {popover}
    </>
  )
}

/**
 * The chip as a button on the tile's edit strip (#260): the period's words in a pill outlined in the primary colour, so it is seen and pressed,
 * named "Spending period: 12 full months. Change it". On a touch screen the button is 44 px tall while the pill keeps its height (its
 * margins give the room back).
 */
function PeriodChip({ words, name, open, onClick }: { words: string; name: string; open: boolean; onClick: (event: React.MouseEvent<HTMLButtonElement>) => void }) {
  return (
    <ButtonBase
      aria-label={name}
      aria-haspopup="dialog"
      aria-expanded={open}
      onClick={onClick}
      sx={{ flex: '0 1 auto', maxWidth: '100%', minWidth: 0, borderRadius: 999, [COARSE]: { minHeight: 44, minWidth: 44, my: '-10px' }, '&:focus-visible > span': { outline: 2, outlineColor: 'primary.main', outlineOffset: 2 } }}
    >
      <Box
        component="span"
        sx={(theme) => ({
          display: 'block',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
          px: 1,
          py: 0.25,
          borderRadius: 999,
          border: 2,
          borderColor: 'primary.main',
          bgcolor: open ? theme.vars.palette.primary.soft : theme.vars.palette.background.paper,
          color: theme.vars.palette.primary.softText,
          typography: 'caption',
          fontWeight: 600,
        })}
      >
        {words}
      </Box>
    </ButtonBase>
  )
}
