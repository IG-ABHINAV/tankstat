import Box from '@mui/material/Box'
import Typography from '@mui/material/Typography'
import { useFormat } from '../i18n/format.ts'

type Window = { start: string; end: string }
const day = (iso: string) => Date.UTC(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10)))

/**
 * The two windows a key figure compares, on one line (#230): the earlier one lighter, the recent one solid, their days under them; a period
 * without a comparison (#274) its one window. A picture only (`aria-hidden`): the sentence says the same. A window of one day (fill-ups
 * not there yet) still shows as a sliver.
 */
export function TrendTimeline({ recent, earlier }: { recent: Window; earlier: Window | null }) {
  const format = useFormat()
  const windows = earlier ? [earlier, recent] : [recent]
  const from = Math.min(...windows.map((w) => day(w.start)))
  const to = Math.max(...windows.map((w) => day(w.end))) + 86_400_000
  const place = (w: Window) => ({ left: `${((day(w.start) - from) / (to - from)) * 100}%`, width: `max(4px, ${((day(w.end) + 86_400_000 - day(w.start)) / (to - from)) * 100}%)` })
  const span = (w: Window) => `${format.date(w.start)} – ${format.date(w.end)}`
  return (
    <Box aria-hidden data-testid="trend-timeline" sx={{ display: 'grid', gap: 0.5 }}>
      <Box sx={{ position: 'relative', height: 10, borderRadius: 5, bgcolor: 'action.hover' }}>
        {earlier && <Box data-window="earlier" sx={{ position: 'absolute', top: 0, height: 10, borderRadius: 5, bgcolor: 'primary.main', opacity: 0.35, ...place(earlier) }} />}
        <Box data-window="recent" sx={{ position: 'absolute', top: 0, height: 10, borderRadius: 5, bgcolor: 'primary.main', ...place(recent) }} />
      </Box>
      <Box sx={{ display: 'flex', justifyContent: 'space-between', flexWrap: 'wrap', columnGap: 2 }}>
        {earlier && (
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            {span(earlier)}
          </Typography>
        )}
        <Typography variant="caption" sx={{ fontWeight: 700 }}>
          {span(recent)}
        </Typography>
      </Box>
    </Box>
  )
}
