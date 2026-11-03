import Box from '@mui/material/Box'
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

const pill = (theme: Parameters<Parameters<typeof Box>[0]['sx'] extends infer S ? (S extends (t: infer T) => unknown ? (t: T) => unknown : never) : never>[0]) => ({
  display: 'inline-flex',
  flexWrap: 'wrap' as const,
  p: '3px',
  gap: '2px',
  borderRadius: 9999,
  backgroundColor: theme.vars.palette.neutral.soft,
})

/** The look of one segment: a pill inside the pill, raised when chosen, dimmed when it cannot be chosen. */
function Segment({ option, name, chosen, control }: { option: SegmentedOption<string>; name: string; chosen: boolean; control: ReactNode }) {
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
      {control}
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
  return title ? (
    <Tooltip key={name} title={title} describeChild={!option.icon || option.reason !== undefined}>
      {body}
    </Tooltip>
  ) : (
    body
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
        <Segment
          key={option.value}
          option={option}
          name={`${name}-${option.value}`}
          chosen={option.value === value}
          control={<input type="radio" name={name} value={option.value} checked={option.value === value} disabled={option.disabled} onChange={() => onChange(option.value)} style={visuallyHidden} />}
        />
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
        <Segment
          key={option.value}
          option={option}
          name={`${name}-${option.value}`}
          chosen={values.includes(option.value)}
          control={<input type="checkbox" name={`${name}-${option.value}`} checked={values.includes(option.value)} disabled={option.disabled} onChange={(e) => onChange(option.value, e.target.checked)} style={visuallyHidden} />}
        />
      ))}
    </Box>
  )
}
