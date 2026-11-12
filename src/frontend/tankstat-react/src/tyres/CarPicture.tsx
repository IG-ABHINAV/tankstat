import Box from '@mui/material/Box'
import ButtonBase from '@mui/material/ButtonBase'
import Typography from '@mui/material/Typography'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useCornerWords, type ExactCorner } from './singleTyres.ts'

/**
 * The car seen from above, its front at the top, with something at each corner (a tyre's button, a picked tyre): only a picture, the
 * buttons carry their own names. An empty corner says so in words.
 */
export function CarPicture({ cells, dim = false }: { cells: Partial<Record<ExactCorner, ReactNode>>; dim?: boolean }) {
  const { t } = useTranslation()
  const corners = useCornerWords()
  const cell = (corner: ExactCorner, area: string) => (
    <Box sx={{ gridArea: area, minWidth: 0, display: 'flex' }}>
      {cells[corner] ?? (
        <Box sx={{ flex: 1, minHeight: 60, display: 'grid', placeItems: 'center', p: 1, borderRadius: 2, border: 1, borderStyle: 'dashed', borderColor: 'divider' }}>
          <Typography variant="caption" sx={{ color: 'text.secondary', textAlign: 'center' }}>
            {corners.title(corner)}
          </Typography>
        </Box>
      )}
    </Box>
  )
  return (
    <Box
      sx={{
        display: 'grid', gridTemplateColumns: 'minmax(0,1fr) 56px minmax(0,1fr)', gridTemplateAreas: '"fl body fr" "rl body rr"', gap: '16px 8px',
        alignItems: 'stretch', opacity: dim ? 0.45 : 1,
      }}
    >
      {cell('FRONT_LEFT', 'fl')}
      <Box aria-hidden sx={{ gridArea: 'body', border: 2, borderColor: 'divider', borderRadius: '24px 24px 12px 12px', bgcolor: 'action.hover', display: 'flex', justifyContent: 'center', pt: 1 }}>
        <Typography variant="caption" sx={{ textTransform: 'uppercase', letterSpacing: '0.1em', fontWeight: 700, color: 'text.secondary', fontSize: '0.625rem' }}>
          {t('tyres.fields.front')}
        </Typography>
      </Box>
      {cell('FRONT_RIGHT', 'fr')}
      {cell('REAR_LEFT', 'rl')}
      {cell('REAR_RIGHT', 'rr')}
    </Box>
  )
}

/** A tyre at a corner of the car picture: a button with its label and lines under it, named in full for a screen reader. */
export function TyreButton({
  corner, label, lines, name, pressed, onClick,
}: {
  corner: ExactCorner
  label: string
  lines: ReactNode[]
  name: string
  /** Picked (a swap's exchange): the button is a toggle. */
  pressed?: boolean
  onClick: () => void
}) {
  const right = corner === 'FRONT_RIGHT' || corner === 'REAR_RIGHT'
  return (
    <ButtonBase
      onClick={onClick}
      aria-label={name}
      aria-pressed={pressed}
      sx={{
        flex: 1, minWidth: 0, minHeight: 60, p: 1, borderRadius: 2, border: 1, borderColor: pressed ? 'primary.main' : 'divider',
        [right ? 'borderRight' : 'borderLeft']: 8, [right ? 'borderRightColor' : 'borderLeftColor']: 'text.secondary',
        bgcolor: pressed ? 'action.selected' : 'background.paper', display: 'flex', flexDirection: 'column', alignItems: right ? 'flex-end' : 'flex-start',
        textAlign: right ? 'right' : 'left', justifyContent: 'center', gap: 0.25,
        '&:focus-visible': { outline: 2, outlineColor: 'primary.main', outlineOffset: 2 },
      }}
    >
      <Typography component="span" variant="body2" sx={{ fontWeight: 700 }}>
        {label}
      </Typography>
      {lines.map((line, n) => (
        <Typography key={n} component="span" variant="caption" sx={{ color: 'text.secondary', fontVariantNumeric: 'tabular-nums', overflowWrap: 'anywhere' }}>
          {line}
        </Typography>
      ))}
    </ButtonBase>
  )
}
