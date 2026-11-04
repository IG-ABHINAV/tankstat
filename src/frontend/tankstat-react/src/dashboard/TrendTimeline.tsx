import Box from '@mui/material/Box'
import Typography from '@mui/material/Typography'
import { useFormat } from '../i18n/format.ts'

type Window = { start: string; end: string }
const day = (iso: string) => Date.UTC(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10)))

/**
 * The two windows a trend figure compares, on one line (#230): the earlier one lighter, the recent one solid, their days under them. A
 * picture only (`aria-hidden`): the sentence says the same. A window of one day (fill-ups not there yet) still shows as a sliver.
 */
export function TrendTimeline({ recent, earlier }: { recent: Window; earlier: Window }) {
  const format = useFormat()
  const from = Math.min(day(earlier.start), day(recent.start))
  const to = Math.max(day(earlier.end), day(recent.end)) + 86_400_000
  const place = (w: Window) => ({ left: `${((day(w.start) - from) / (to - from)) * 100}%`, width: `max(4px, ${((day(w.end) + 86_400_000 - day(w.start)) / (to - from)) * 100}%)` })
  const span = (w: Window) => `${format.date(w.start)} – ${format.date(w.end)}`
  return (
    <Box aria-hidden data-testid="trend-timeline" sx={{ display: 'grid', gap: 0.5 }}>
      <Box sx={{ position: 'relative', height: 10, borderRadius: 5, bgcolor: 'action.hover' }}>
        <Box data-window="earlier" sx={{ position: 'absolute', top: 0, height: 10, borderRadius: 5, bgcolor: 'primary.main', opacity: 0.35, ...place(earlier) }} />
        <Box data-window="recent" sx={{ position: 'absolute', top: 0, height: 10, borderRadius: 5, bgcolor: 'primary.main', ...place(recent) }} />
      </Box>
      <Box sx={{ display: 'flex', justifyContent: 'space-between', flexWrap: 'wrap', columnGap: 2 }}>
        <Typography variant="caption" sx={{ color: 'text.secondary' }}>
          {span(earlier)}
        </Typography>
        <Typography variant="caption" sx={{ fontWeight: 700 }}>
          {span(recent)}
        </Typography>
      </Box>
    </Box>
  )
}
