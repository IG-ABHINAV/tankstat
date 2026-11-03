import Box from '@mui/material/Box'
import type { Theme } from '@mui/material/styles'
import Tooltip from '@mui/material/Tooltip'
import { useId, type ReactNode } from 'react'
import { COARSE } from '../theme/components.ts'
import { visuallyHidden } from './visuallyHidden.ts'

/** One choice of a segmented control: its words (always its name), an icon drawn instead of them, and why it cannot be chosen now. */
export interface SegmentedOption<V extends string> {
  value: V
  label: string
  /** Drawn in place of the label, which then names the segment for assistive technology and in its tooltip. */
  icon?: ReactNode
  /** Shown but not choosable; `reason` says why, in the tooltip. */
  disabled?: boolean
  reason?: string
}

const pill = (theme: Theme) => ({
  display: 'inline-flex',
  flexWrap: 'wrap' as const,
  p: '3px',
  gap: '2px',
  borderRadius: 9999,
  backgroundColor: theme.vars.palette.neutral.soft,
})

/**
 * One segment: its input (a radio or a checkbox) in a pill inside the pill, raised when chosen, dimmed when it cannot be chosen. A
 * `reason` describes the input too (a disabled one takes no focus and its tooltip is on the label, so neither reaches a keyboard or a
 * screen reader), from outside the label so it is not part of the name.
 */
function Segment({ option, type, name, chosen, onChange }: { option: SegmentedOption<string>; type: 'radio' | 'checkbox'; name: string; chosen: boolean; onChange: (checked: boolean) => void }) {
  const reasonId = useId()
  const body = (
    <Box
      component="label"
      sx={(theme) => ({
        ...theme.typography.body1,
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 0.75,
        minHeight: 36,
        minWidth: option.icon ? 44 : 80,
        [COARSE]: { minHeight: 44 },
        px: option.icon ? 0 : 2,
        borderRadius: 9999,
        cursor: option.disabled ? 'not-allowed' : 'pointer',
        color: option.disabled ? theme.vars.palette.text.disabled : theme.vars.palette.text.primary,
        // The chosen segment is raised: its own surface and shadow, and bold text, so it does not depend on a tint.
        ...(chosen ? { backgroundColor: theme.vars.palette.background.paper, boxShadow: theme.shadows[2], fontWeight: theme.typography.fontWeightBold } : {}),
        '&:has(input:focus-visible)': theme.focusVisible,
      })}
    >
      <input
        type={type}
        name={name}
        value={option.value}
        checked={chosen}
        disabled={option.disabled}
        aria-describedby={option.reason ? reasonId : undefined}
        onChange={(e) => onChange(e.target.checked)}
        style={visuallyHidden}
      />
      {option.icon ? (
        <>
          <span aria-hidden style={{ display: 'inline-flex' }}>{option.icon}</span>
          <span style={visuallyHidden}>{option.label}</span>
        </>
      ) : (
        option.label
      )}
    </Box>
  )
  // The tooltip names an icon segment (and says why one cannot be chosen); it sits on the label, which still takes the pointer when the
  // input inside is disabled.
  const title = option.reason ?? (option.icon ? option.label : undefined)
  return (
    <>
      {title ? (
        <Tooltip title={title} describeChild={!option.icon || option.reason !== undefined}>
          {body}
        </Tooltip>
      ) : (
        body
      )}
      {option.reason && (
        <span id={reasonId} style={visuallyHidden}>
          {option.reason}
        </span>
      )}
    </>
  )
}

/**
 * A choice between a few options, drawn as one pill with segments: real radio buttons (arrow keys move between them, a screen reader
 * hears a radio group), each in its segment's label; the chosen segment is raised. An option with an `icon` shows it instead of its
 * words (its name for assistive technology, and its tooltip); a `disabled` one stays in place, dimmed, its `reason` in the tooltip, so the
 * row never changes shape.
 */
export function Segmented<V extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string
  value: V
  options: readonly SegmentedOption<V>[]
  onChange: (value: V) => void
}) {
  const name = useId()
  return (
    <Box role="radiogroup" aria-label={label} sx={pill}>
      {options.map((option) => (
        <Segment key={option.value} option={option} type="radio" name={name} chosen={option.value === value} onChange={() => onChange(option.value)} />
      ))}
    </Box>
  )
}

/**
 * Independent switches drawn as one pill of segments (the same look as `Segmented`): real checkboxes, each named by its words, a raised
 * segment for one that is on. For a row of extras, where several can be on at once.
 */
export function SegmentedChecks<V extends string>({
  label,
  values,
  options,
  onChange,
}: {
  label: string
  /** The options that are on. */
  values: readonly V[]
  options: readonly SegmentedOption<V>[]
  onChange: (value: V, checked: boolean) => void
}) {
  const name = useId()
  return (
    <Box role="group" aria-label={label} sx={pill}>
      {options.map((option) => (
        <Segment key={option.value} option={option} type="checkbox" name={`${name}-${option.value}`} chosen={values.includes(option.value)} onChange={(checked) => onChange(option.value, checked)} />
      ))}
    </Box>
  )
}
