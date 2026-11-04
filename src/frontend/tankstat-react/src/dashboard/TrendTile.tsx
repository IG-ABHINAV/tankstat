import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import ButtonBase from '@mui/material/ButtonBase'
import Chip from '@mui/material/Chip'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { VehicleTrendDocument, type DistanceUnit, type VolumeUnit } from '../gql/generated.ts'
import { COARSE } from '../theme/components.ts'
import { periodOf, toInput, TREND_FIGURE, type TrendFigureId, type TrendPeriod } from './trendPeriod.ts'
import { TrendFigure } from './TrendFigure.tsx'
import { TrendPeriodPopover, type PeriodControl } from './TrendPeriodPopover.tsx'
import { usePeriodText } from './usePeriodText.ts'

type Units = { distance: DistanceUnit; volume: VolumeUnit }

/**
 * A trend figure over its tile's period (#230): its own `vehicleTrend` answer, its value, the chip naming the period, and the comparison
 * worded from the period and the answer. Without a period of its own, its figure's default, which reads as the figure always did. With a
 * `control` the chip is a button that opens the period's popover; without one (the Add widget dialog's preview) it only names it.
 */
export function TrendTile({ figure, vehicleId, period, rows, units, control }: { figure: TrendFigureId; vehicleId: string; period: TrendPeriod | null | undefined; rows: number; units: Units; control?: PeriodControl }) {
  const { t } = useTranslation()
  const p = periodOf(figure, period)
  const { data, error } = useQuery(VehicleTrendDocument, { variables: { vehicleId, figure: TREND_FIGURE[figure], period: toInput(p) }, fetchPolicy: 'cache-and-network' })
  const periodText = usePeriodText()
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const label = t(`dashboard.kpi.${figure}`)
  const words = periodText(figure, p)
  const chip = control ? (
    <PeriodChip words={words} name={t('dashboard.period.chipName', { figure: label, period: words })} open={anchor !== null} onClick={(e) => setAnchor(e.currentTarget)} />
  ) : (
    <Chip size="small" variant="outlined" label={words} sx={{ flex: 'none', maxWidth: '60%' }} />
  )
  const popover = control && anchor && <TrendPeriodPopover anchor={anchor} figure={figure} vehicleId={vehicleId} units={units} period={p} control={control} onClose={() => setAnchor(null)} />
  return (
    <>
      <TrendFigure figure={figure} period={p} data={data?.vehicleTrend} error={error} rows={rows} units={units} label={label} chip={chip} periodWords={words} />
      {popover}
    </>
  )
}

/**
 * The chip as a button: the period's words in a pill, named "Spending period: 12 full months. Change it". On a touch screen the button is
 * 44 px tall while the pill and the label row keep their height (its margins give the room back), so a two-row figure still holds its
 * label and value.
 */
function PeriodChip({ words, name, open, onClick }: { words: string; name: string; open: boolean; onClick: (event: React.MouseEvent<HTMLButtonElement>) => void }) {
  return (
    <ButtonBase
      aria-label={name}
      aria-haspopup="dialog"
      aria-expanded={open}
      onClick={onClick}
      sx={{ flex: 'none', maxWidth: '60%', minWidth: 0, borderRadius: 999, [COARSE]: { minHeight: 44, minWidth: 44, my: '-10px' }, '&:focus-visible > span': { outline: 2, outlineColor: 'primary.main', outlineOffset: 2 } }}
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
          border: 1,
          borderColor: open ? 'primary.main' : 'divider',
          bgcolor: open ? theme.vars.palette.primary.soft : 'transparent',
          color: open ? theme.vars.palette.primary.softText : 'text.primary',
          typography: 'caption',
          '&:hover': { borderColor: 'primary.main' },
        })}
      >
        {words}
      </Box>
    </ButtonBase>
  )
}
