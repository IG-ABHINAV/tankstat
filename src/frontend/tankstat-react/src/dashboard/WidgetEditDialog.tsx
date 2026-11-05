import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { Form } from '../forms/Form.tsx'
import type { DistanceUnit, VolumeUnit } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { draftProblem, type WidgetDraft } from './widgetDraft.ts'
import { CheckRow, WidgetForm } from './WidgetForm.tsx'

/**
 * What the pencil edits (#275): a tile in the arrangement (`arrangement`), one's own saved widget (`own`), or someone else's (`copy`: saved
 * as one's own in its place). What a save does: changed in the arrangement (with `saveToo` saved to one's widgets too), only on this
 * dashboard (`here`: a copy in the arrangement) or everywhere (`everywhere`: the saved widget itself), or saved as one's own (`copy`).
 */
export type EditMode = 'arrangement' | 'own' | 'copy'
export type EditChoice = { kind: 'arrangement'; saveToo: boolean } | { kind: 'here' } | { kind: 'everywhere' } | { kind: 'copy' }

/**
 * The builder as *Edit widget* or *Customize widget* (#275), opened from a tile's pencil while the dashboard is customized: the same form as
 * Add widget's (`WidgetForm`), keeping the widget's kind, with what its save means said above it (*In this arrangement only.*, where else a
 * saved widget is, or that a copy takes the tile's place) and its buttons by that: *Save* with *Also save to my widgets*; *Only on this
 * dashboard* or *Change it everywhere*, asked each time (the owner's choice); *Save as my own*.
 */
export function WidgetEditDialog({
  open,
  onOpenChange,
  onClosed,
  vehicle,
  mode,
  start,
  notes,
  onSave,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  onClosed?: () => void
  vehicle: { id: string; units: { distance: DistanceUnit; volume: VolumeUnit } }
  mode: EditMode
  start: WidgetDraft
  /** What the dialog says about where the widget is (one line each). */
  notes: readonly string[]
  onSave: (draft: WidgetDraft, choice: EditChoice) => Promise<unknown>
}) {
  const { t } = useTranslation()
  const [draft, setDraft] = useState(start)
  const [saveToo, setSaveToo] = useState(false)
  const [busy, setBusy] = useState<EditChoice['kind'] | null>(null)
  const [failure, setFailure] = useState<unknown>()
  const problem = draftProblem(draft)

  const save = async (choice: EditChoice) => {
    if (problem || busy) return
    setBusy(choice.kind)
    setFailure(undefined)
    try {
      await onSave(draft, choice)
      onOpenChange(false)
    } catch (e) {
      setFailure(e)
    } finally {
      setBusy(null)
    }
  }
  // Enter submits the choice the dialog leads with: Save, Change it everywhere, or Save as my own.
  const primary: EditChoice = mode === 'arrangement' ? { kind: 'arrangement', saveToo } : mode === 'own' ? { kind: 'everywhere' } : { kind: 'copy' }

  return (
    <DialogFrame open={open} onClose={() => onOpenChange(false)} onClosed={onClosed} maxWidth={640} title={t(mode === 'copy' ? 'dashboard.widget.customizeTitle' : 'dashboard.widget.editTitle')} busy={busy !== null}>
      <Form
        onSubmit={(e) => {
          e.preventDefault()
          void save(primary)
        }}
      >
        <Stack sx={{ gap: 2 }}>
          {notes.map((n) => (
            <Typography key={n} variant="body2" sx={{ color: 'text.secondary' }}>
              {n}
            </Typography>
          ))}
          <WidgetForm vehicle={vehicle} draft={draft} onChange={setDraft} chooseKind={false} />
          {failure !== undefined && <ErrorMessage error={failure} />}
          <DialogButtons>
            {mode === 'arrangement' && (
              <Box sx={{ mr: 'auto' }}>
                <CheckRow label={t('dashboard.widget.saveToo')} checked={saveToo} onChange={setSaveToo} />
              </Box>
            )}
            <DialogCancel disabled={busy !== null} />
            {mode === 'own' && (
              <Button type="button" variant="soft" loading={busy === 'here'} disabled={problem !== null || busy !== null} onClick={() => void save({ kind: 'here' })}>
                {t('dashboard.widget.onlyHere')}
              </Button>
            )}
            <Button type="submit" loading={busy === primary.kind} disabled={problem !== null || busy !== null}>
              {mode === 'arrangement' ? t('dashboard.widget.save') : mode === 'own' ? t('dashboard.widget.everywhere') : t('dashboard.widget.saveAsMine')}
            </Button>
          </DialogButtons>
          {problem === 'titleRequired' && (
            <Typography variant="body2" sx={{ color: 'text.secondary', textAlign: 'right' }}>
              {t('errors.chart.titleRequired')}
            </Typography>
          )}
        </Stack>
      </Form>
    </DialogFrame>
  )
}
