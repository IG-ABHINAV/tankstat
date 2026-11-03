import FormLabel from '@mui/material/FormLabel'
import ListItemIcon from '@mui/material/ListItemIcon'
import MenuItem from '@mui/material/MenuItem'
import Select from '@mui/material/Select'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useId, type ReactNode, type Ref } from 'react'
import { LabelBadges, type LabelBadge } from './LabelBadges.tsx'

/** An option of a select: its words, and an icon drawn before them (in the list and in the closed field alike). */
export interface SelectOption<T extends string> {
  value: T
  label: string
  icon?: ReactNode
}

/**
 * A labelled choice (units, fuel, a chart's metric, a person): the label names the select (aria-labelledby), the options open in a list.
 * An empty `value` shows the `placeholder`; a `hint` under it describes the select (aria-describedby), as a Field's hint does, and so does
 * an `error` (a choice the form needs and does not have), which marks it invalid. `selectRef` gets what focuses it.
 */
export function LabeledSelect<T extends string>({
  label,
  value,
  options,
  onChange,
  disabled,
  placeholder,
  hint,
  badges,
  error,
  selectRef,
}: {
  label: string
  value: T
  options: SelectOption<T>[]
  onChange: (value: T) => void
  disabled?: boolean
  placeholder?: string
  hint?: string
  /** Short words beside the label (where a value comes from); read as the start of the select's description. */
  badges?: readonly LabelBadge[]
  error?: string
  selectRef?: Ref<{ focus: () => void }>
}) {
  const labelId = useId()
  const hintId = `${labelId}-hint`
  const errorId = `${labelId}-error`
  const badgesId = `${labelId}-badges`
  const hasBadges = (badges?.length ?? 0) > 0
  const describedBy = [hasBadges && badgesId, hint && hintId, error && errorId].filter(Boolean).join(' ') || undefined
  return (
    <Stack sx={{ gap: 0.5, minWidth: 0 }}>
      <Stack direction="row" sx={{ gap: 1, alignItems: 'center', flexWrap: 'wrap' }}>
        <FormLabel id={labelId}>{label}</FormLabel>
        {hasBadges && <LabelBadges id={badgesId} badges={badges!} />}
      </Stack>
      <ChoiceSelect
        labelId={labelId}
        describedBy={describedBy}
        value={value}
        options={options}
        onChange={onChange}
        disabled={disabled}
        placeholder={placeholder}
        invalid={error !== undefined}
        selectRef={selectRef}
      />
      {hint && (
        <Typography id={hintId} variant="caption" sx={{ color: 'text.secondary' }}>
          {hint}
        </Typography>
      )}
      {error && (
        <Typography id={errorId} variant="caption" sx={{ color: 'error.main' }}>
          {error}
        </Typography>
      )}
    </Stack>
  )
}

/** A choice inside a sentence ("Give [person] access to …"): no label of its own on screen, `label` names it for assistive technology. */
export function InlineSelect<T extends string>({
  label,
  value,
  options,
  onChange,
  placeholder,
}: {
  label: string
  value: T
  options: SelectOption<T>[]
  onChange: (value: T) => void
  placeholder?: string
}) {
  return <ChoiceSelect ariaLabel={label} value={value} options={options} onChange={onChange} placeholder={placeholder} />
}

function ChoiceSelect<T extends string>({
  labelId,
  ariaLabel,
  describedBy,
  value,
  options,
  onChange,
  disabled,
  placeholder,
  invalid,
  selectRef,
}: {
  labelId?: string
  ariaLabel?: string
  /** The id of what describes the select (MUI puts it on the combobox). */
  describedBy?: string
  value: T
  options: SelectOption<T>[]
  onChange: (value: T) => void
  disabled?: boolean
  placeholder?: string
  invalid?: boolean
  /** MUI's handle of the select: its `focus()` focuses the combobox. */
  selectRef?: Ref<{ focus: () => void }>
}) {
  return (
    <Select<T>
      labelId={labelId}
      inputRef={selectRef}
      error={invalid}
      inputProps={ariaLabel || describedBy || invalid ? { 'aria-label': ariaLabel, 'aria-describedby': describedBy, 'aria-invalid': invalid || undefined } : undefined}
      value={value}
      onChange={(event) => onChange(event.target.value as T)}
      disabled={disabled}
      displayEmpty={placeholder !== undefined}
      renderValue={(selected) => {
        const chosen = options.find((o) => o.value === selected)
        if (!chosen) return <Typography component="span" variant="body2" sx={{ color: 'text.secondary' }}>{placeholder}</Typography>
        return chosen.icon ? (
          <Stack direction="row" component="span" sx={{ alignItems: 'center', gap: 1 }}>
            <Stack component="span" aria-hidden sx={{ color: 'primary.light' }}>{chosen.icon}</Stack>
            {chosen.label}
          </Stack>
        ) : (
          chosen.label
        )
      }}
    >
      {options.map((o) => (
        <MenuItem key={o.value} value={o.value}>
          {o.icon && <ListItemIcon sx={{ color: 'primary.light' }}>{o.icon}</ListItemIcon>}
          {o.label}
        </MenuItem>
      ))}
    </Select>
  )
}
