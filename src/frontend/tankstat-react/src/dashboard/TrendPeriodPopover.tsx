import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Popover from '@mui/material/Popover'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import useMediaQuery from '@mui/material/useMediaQuery'
import { useId, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Form } from '../forms/Form.tsx'
import type { DistanceUnit, VolumeUnit } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { MEDIA } from '../theme/media.ts'
import { samePeriod, type TrendFigureId, type TrendPeriod } from './trendPeriod.ts'
import { TrendFigure } from './TrendFigure.tsx'
import { TrendPeriodForm } from './TrendPeriodForm.tsx'
import { useTrendPreview } from './useTrendPreview.ts'

/** What the tile's chip can do with a period while the dashboard is arranged (#260): where Apply puts it (worded), whether it can, and the change itself. */
export interface PeriodControl {
  /** Where Apply puts the period: the arrangement being edited, kept by Save dashboard. */
  hint: string
  canApply: boolean
  apply: (period: TrendPeriod) => Promise<void>
}

/**
 * A trend tile's period, chosen in a popover from its chip (#230): the form, the figure as the tile will show it (the server's answer for
 * the period being chosen), where Apply puts it, Cancel and Apply. Enter applies (the form never goes to the browser: a sent form
 * would load the page again); it closes once the period is applied.
 */
export function TrendPeriodPopover({ anchor, figure, vehicleId, units, period, control, onClose }: { anchor: HTMLElement | null; figure: TrendFigureId; vehicleId: string; units: { distance: DistanceUnit; volume: VolumeUnit }; period: TrendPeriod; control: PeriodControl; onClose: () => void }) {
  const { t } = useTranslation()
  const id = useId()
  const [draft, setDraft] = useState(period)
  const [busy, setBusy] = useState(false)
  const [failure, setFailure] = useState<unknown>()
  const preview = useTrendPreview(figure, vehicleId, draft)
  const label = t(`dashboard.kpi.${figure}`)
  // On a phone it is nearly a sheet of the screen's width: dimmed behind like a dialog, so its see-through panel reads over any tile.
  const phone = useMediaQuery(MEDIA.narrow)

  const apply = async () => {
    if (samePeriod(draft, period)) return onClose()
    setBusy(true)
    setFailure(undefined)
    try {
      await control.apply(draft)
      onClose()
    } catch (e) {
      setFailure(e)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Popover
      open={anchor !== null}
      anchorEl={anchor}
      onClose={busy ? undefined : onClose}
      // From the chip's left edge: the chip sits at the left of its tile's edit strip (#260), so the popover opens under its own tile.
      anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}
      transformOrigin={{ vertical: 'top', horizontal: 'left' }}
      slotProps={{ backdrop: { invisible: !phone }, paper: { role: 'dialog', 'aria-labelledby': `${id}-title`, sx: { width: { xs: 'calc(100vw - 32px)', sm: 400 }, maxWidth: 'calc(100vw - 32px)', p: 2 } } }}
    >
      <Form
        onSubmit={(e) => {
          e.preventDefault() // the form is the app's, never sent by the browser
          void apply()
        }}
      >
        <Stack sx={{ gap: 2 }}>
          <Typography id={`${id}-title`} component="h2" variant="h5">
            {t('dashboard.period.title', { figure: label })}
          </Typography>
          <TrendPeriodForm figure={figure} value={draft} onChange={setDraft} preview={preview} />
          <Box role="status" aria-live="polite" aria-label={t('dashboard.period.preview')} sx={{ minHeight: 132 }}>
            {preview.data && <TrendFigure figure={figure} period={draft} data={preview.data} rows={3} units={units} label={t('dashboard.period.preview')} />}
          </Box>
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {control.hint}
          </Typography>
          {failure !== undefined && <ErrorMessage error={failure} />}
          <Stack direction="row" sx={{ gap: 1, justifyContent: 'flex-end', flexWrap: 'wrap' }}>
            <Button type="button" variant="ghost" disabled={busy} onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button type="submit" loading={busy} disabled={!control.canApply || preview.problem !== null}>
              {t('dashboard.period.apply')}
            </Button>
          </Stack>
        </Stack>
      </Form>
    </Popover>
  )
}
