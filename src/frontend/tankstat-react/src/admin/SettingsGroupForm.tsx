import { useMutation } from '@apollo/client/react'
import Accordion from '@mui/material/Accordion'
import AccordionDetails from '@mui/material/AccordionDetails'
import AccordionSummary from '@mui/material/AccordionSummary'
import Alert from '@mui/material/Alert'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Checkbox from '@mui/material/Checkbox'
import FormControlLabel from '@mui/material/FormControlLabel'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import type { TFunction } from 'i18next'
import { ChevronDown, Info, TriangleAlert } from 'lucide-react'
import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode, type Ref } from 'react'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../components/ConfirmDialog.tsx'
import { DefinitionList } from '../components/DefinitionList.tsx'
import { LabelBadges, type LabelBadge } from '../components/LabelBadges.tsx'
import { LabeledSwitch } from '../components/LabeledSwitch.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { Field } from '../forms/Field.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import { PasswordInput } from '../forms/PasswordInput.tsx'
import { Form } from '../forms/Form.tsx'
import { SystemSettingsDocument, UpdateSystemSettingsDocument, type SystemSettingFieldsFragment, type SettingsGroup } from '../gql/generated.ts'
import { sampleDate, sampleTime, useFormat } from '../i18n/format.ts'
import { DATE_FORMATS, TIME_FORMATS } from '../i18n/formatStore.ts'
import { validLocale } from '../i18n/locale.ts'
import { ErrorMessage } from '../messages.tsx'
import { useToast } from '../toast/toastContext.ts'
import { ProblemList } from './ProblemList.tsx'
import { changesOf, daysOf, daysValue, isOn, WEEK, type GroupDraft } from './settingsDraft.ts'
import { SettingHelp } from './SettingHelp.tsx'
import { problemsByKey, problemsOf } from './settingsText.ts'
import { sharedReason, unusedReason, type Unused, type ValueOf } from './settingsRelevance.ts'
import { layoutOf, type SectionId } from './settingsSections.ts'
import { useSettingLabel, useUnusedText } from './useUnusedText.ts'

type Setting = SystemSettingFieldsFragment

const MODE_KEY = 'Auth:Mode'

/**
 * One group of system settings as a form: the settings the environment holds are shown read-only with their variable, the others are
 * edited and saved together. The values live with the page (`draft`, `onDraft`), so they outlast a switch of tabs, and a setting another
 * one leaves unused (`valueOf`, the values of every group as the page holds them) is shown but cannot be changed, with the reason. A change
 * of the sign-in mode asks once more and says the way back. The setup page's wizard shows each group with it too (the props after
 * `onSaved`), so a setting looks and is saved the same in both.
 */
export function SettingsGroupForm({
  group,
  draft,
  valueOf,
  onDraft,
  onSaved,
  code,
  required = [],
  confirmModeChange = true,
  submitLabel,
  saveUnchanged = false,
  actions,
  announceSaved = true,
}: {
  group: { name: SettingsGroup; appliesAtOnce: boolean; settings: readonly Setting[] }
  draft: GroupDraft
  valueOf: ValueOf
  onDraft: (draft: GroupDraft) => void
  /** After a save: the form starts afresh from the server's answer, even one that looks as before. */
  onSaved: () => void
  /** The one-time code the setup page's wizard saves with while the setup waits (no administrator exists yet). */
  code?: string
  /** Choices that must be made: offered without "Default (not set)", and the form is not sent until each holds one. */
  required?: readonly string[]
  /** Ask once more before the sign-in mode changes, saying the way back; a first setup has no mode to go back to. */
  confirmModeChange?: boolean
  /** The submit button's words; the settings page's Save by default. */
  submitLabel?: string
  /** Nothing changed: the submit goes on (`onSaved`) instead of doing nothing, as a wizard's step does. */
  saveUnchanged?: boolean
  /** More buttons beside the submit one (a wizard's Back and Skip). */
  actions?: ReactNode
  /** A toast after a save, saying whether it waits for a restart (the settings page); a wizard moves on instead. */
  announceSaved?: boolean
}) {
  const { t } = useTranslation()
  const { weekday } = useFormat()
  const choiceLabel = useChoiceLabel()
  const { toast } = useToast()
  const unusedText = useUnusedText()
  const settingLabel = useSettingLabel()
  const heading = useId()
  const saveButton = useRef<HTMLButtonElement>(null)
  const [update, { loading: saving }] = useMutation(UpdateSystemSettingsDocument, {
    // The answer is the whole page as the server now has it, so it takes the query's place: no second request, whose failure would show a
    // save that was written as refused (and invite another, which writes the file and its .bak once more).
    update: (cache, { data }) => {
      if (data) cache.writeQuery({ query: SystemSettingsDocument, variables: code === undefined ? undefined : { code }, data: { systemSettings: data.updateSystemSettings } })
    },
  })
  const [error, setError] = useState<unknown>()
  // The required choices the last submit found empty, each said at its control, the first one given the focus.
  const [missing, setMissing] = useState<ReadonlySet<string>>(new Set())
  const focusers = useRef(new Map<string, { focus: () => void }>())
  const [confirmMode, setConfirmMode] = useState(false)
  const problems = problemsOf(error)

  // Saving turns the button off, so the focus it had (or that the mode's confirmation gives back to it as it closes) is lost. After a
  // refusal, once the confirmation is gone, it returns there, unless it went somewhere else meanwhile.
  const [confirmGone, setConfirmGone] = useState(true)
  useEffect(() => {
    if (confirmGone && !saving && error !== undefined && (!document.activeElement || document.activeElement === document.body)) saveButton.current?.focus()
  }, [confirmGone, saving, error])

  const label = (s: Setting) => settingLabel(s.key)
  const editable = group.settings.filter((s) => s.editable)
  // A refused save's problems at the controls they name (a switch or the days have no place for one: said under the form, as is what names
  // a setting the environment holds, which cannot be changed here).
  const { byKey: problemAt, rest: otherProblems } = problemsByKey(
    problems,
    editable.filter((s) => s.kind !== 'BOOLEAN' && s.kind !== 'DAYS').map((s) => s.key),
  )
  const form = useRef<HTMLFormElement>(null)
  useEffect(() => {
    const first = editable.find((s) => problemAt.has(s.key))
    if (!first) return
    if (focusers.current.has(first.key)) focusers.current.get(first.key)!.focus()
    else form.current?.querySelector<HTMLElement>(`[name="${CSS.escape(first.key)}"]`)?.focus()
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once per refusal
  }, [error])
  const changes = changesOf(group.settings, draft)
  const layout = layoutOf(group.name, group.settings)
  // Which sections are open: as the person left them, else open while used and closed while not (so a choice that leaves one unused folds it).
  const [opened, setOpened] = useState<Partial<Record<SectionId, boolean>>>({})
  const modeChange = changes.find((c) => c.key === MODE_KEY)
  // What the confirmation calls the next mode. Cleared, it is the one below the file (the shipped appsettings.json's None, unless the
  // operator's says otherwise); None, chosen or not, is said for what it is: no sign-in, and so no administrator to change it back here.
  const nextMode = modeChange?.value == null ? t('systemSettings.modeDefault') : modeChange.value.toLowerCase() === 'none' ? t('systemSettings.modeNone') : modeChange.value

  // Why each setting is not used now, said once at the widest place it holds for: above the group, in a section's header, or at the setting.
  const unused = new Map<string, Unused | null>(group.settings.map((s) => [s.key, unusedReason(s.key, valueOf)]))
  const wholeGroup = sharedReason([...unused.values()])
  const sectionReason = new Map(layout.sections.map((s) => [s.id, sharedReason(s.settings.map((setting) => unused.get(setting.key) ?? null))]))
  const reasonOf = (s: Setting, section?: SectionId) => {
    const reason = unused.get(s.key)
    if (!reason || wholeGroup || (section && sectionReason.get(section))) return null
    return unusedText(reason)
  }

  /** A run of settings: the ones the environment holds as values with their variable, the others as controls. */
  const block = (settings: readonly Setting[], section?: SectionId) => {
    const fixed = settings.filter((s) => !s.editable)
    return (
      <Stack sx={{ gap: 1.5 }}>
        {fixed.length > 0 && (
          <DefinitionList
            items={fixed.map((s) => ({
              label: label(s),
              value: (
                <Stack direction="row" sx={{ gap: 0.5, alignItems: 'flex-start' }}>
                  <Box sx={{ flex: 1, minWidth: 0 }}>
                    {shown(s, t, weekday, choiceLabel)}
                    <Typography component="span" variant="caption" sx={{ display: 'block', color: 'text.secondary' }}>
                      {[t('systemSettings.source.ENVIRONMENT'), reasonOf(s, section)].filter(Boolean).join(' ')}
                    </Typography>
                  </Box>
                  <SettingHelp settingKey={s.key} label={label(s)} />
                </Stack>
              ),
            }))}
          />
        )}
        {settings
          .filter((s) => s.editable)
          .map((s) => (
            <SettingControl
              key={s.key}
              setting={s}
              groupAppliesAtOnce={group.appliesAtOnce}
              label={label(s)}
              value={draft.values[s.key] ?? ''}
              cleared={draft.cleared.has(s.key)}
              unused={unused.get(s.key) != null}
              reason={reasonOf(s, section)}
              required={required.includes(s.key)}
              missing={missing.has(s.key)}
              problem={problemAt.get(s.key)}
              selectRef={(handle) => {
                if (handle) focusers.current.set(s.key, handle)
                else focusers.current.delete(s.key)
              }}
              onChange={(value) => set(s.key, value)}
              onClear={(removed) => clear(s.key, removed)}
            />
          ))}
        {/* No sign-in hands everything to whoever reaches the server: said as soon as it is chosen, and while it runs so. Part of the page,
            not news (a note, not an alert, which would interrupt a screen reader each time the tab is drawn): the mode's confirmation
            says it again at the moment of choosing. */}
        {settings.some((s) => s.key === 'Auth:Mode') && valueOf('Auth:Mode')?.toLowerCase() === 'none' && (
          <Alert severity="warning" role="note" icon={<TriangleAlert size={16} aria-hidden />}>
            {t('systemSettings.noSignInWarning')}
          </Alert>
        )}
        {/* Secrets in backups that are not encrypted: readable by anyone who has a file (#140). Said while it is so, as above. */}
        {settings.some((s) => s.key === 'Backups:Secrets') && valueOf('Backups:Secrets')?.toLowerCase() === 'true' && !valueOf('Backups:Encryption:Recipient')?.trim() && (
          <Alert severity="warning" role="note" icon={<TriangleAlert size={16} aria-hidden />}>
            {t('systemSettings.secretsPlainWarning')}
          </Alert>
        )}
      </Stack>
    )
  }

  const body = (
    <Stack sx={{ gap: 1.5 }}>
      {layout.lead.length > 0 && block(layout.lead)}
      {layout.sections.length > 0 && (
        <div>
          {layout.sections.map((section) => {
            const reason = sectionReason.get(section.id) ?? null
            const pending = changesOf(section.settings, draft).length > 0
            const status = [reason && (wholeGroup ? t('systemSettings.unused.short') : unusedText(reason)), pending && t('systemSettings.sectionUnsaved')].filter(
              (part): part is string => typeof part === 'string',
            )
            return (
              <Accordion
                key={section.id}
                expanded={opened[section.id] ?? !reason}
                onChange={(_, open) => setOpened((o) => ({ ...o, [section.id]: open }))}
                disableGutters
                variant="outlined"
                slotProps={{ heading: { component: 'h3' } }}
              >
                <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />} id={`${heading}-${section.id}-header`} aria-controls={`${heading}-${section.id}-panel`}>
                  <Stack sx={{ minWidth: 0 }}>
                    <Typography component="span" variant="body1" sx={{ fontWeight: 700, color: reason ? 'text.secondary' : undefined }}>
                      {t(`systemSettings.sections.${section.id}`)}
                    </Typography>
                    {/* A space between the title and the status, so the header reads as two phrases, not one word. */}{' '}
                    {status.length > 0 && (
                      <Typography component="span" variant="caption" sx={{ color: 'text.secondary' }}>
                        {status.join(' · ')}
                      </Typography>
                    )}
                  </Stack>
                </AccordionSummary>
                {/* The panel's region takes its id from the header's aria-controls (MUI does that): an id here too would be a second one. */}
                <AccordionDetails>{block(section.settings, section.id)}</AccordionDetails>
              </Accordion>
            )
          })}
        </div>
      )}
    </Stack>
  )

  async function save() {
    setError(undefined)
    try {
      const answer = await update({ variables: code === undefined ? { changes } : { changes, code } })
      onSaved()
      // The server says what waits: a group that applies at once runs with what was saved, and a value saved back to what runs waits for nothing.
      // Only what this save changed counts: a setting of the tab saved before may wait for a restart beside one that applies at once (the
      // password minimum in the Sign-in tab).
      const sent = new Set(changes.map((c) => c.key))
      const saved = answer.data?.updateSystemSettings.groups.find((g) => g.name === group.name)
      if (announceSaved)
        toast(t(saved?.settings.some((s) => sent.has(s.key) && s.restartPending) ? 'systemSettings.saved' : 'systemSettings.savedInEffect'))
    } catch (e) {
      setError(e)
    }
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const empty = editable.filter((s) => required.includes(s.key) && !(draft.values[s.key] ?? '').trim()).map((s) => s.key)
    if (empty.length > 0) {
      setMissing(new Set(empty))
      focusers.current.get(empty[0]!)?.focus()
      return
    }
    if (changes.length === 0) {
      if (saveUnchanged) onSaved()
      return
    }
    if (modeChange && confirmModeChange) setConfirmMode(true)
    else void save()
  }

  const set = (key: string, value: string) => {
    if (missing.has(key)) setMissing(new Set([...missing].filter((k) => k !== key)))
    onDraft({ ...draft, values: { ...draft.values, [key]: value } })
  }
  const clear = (key: string, removed: boolean) => {
    const next = new Set(draft.cleared)
    if (removed) next.add(key)
    else next.delete(key)
    onDraft({ ...draft, cleared: next })
  }

  return (
    <Stack component="section" aria-labelledby={heading} sx={{ gap: 1.5 }}>
      <div>
        <Typography component="h2" variant="h5" id={heading} sx={{ mb: 0.5 }}>
          {t(`systemSettings.groups.${group.name}`)}
        </Typography>
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t(`systemSettings.groupHelp.${group.name}`)}
        </Typography>
        <Typography variant="body2" sx={{ color: 'text.secondary', mt: 0.5 }}>
          {t(group.appliesAtOnce ? 'systemSettings.appliesAtOnce' : 'systemSettings.appliesAfterRestart')}
        </Typography>
      </div>
      {wholeGroup && (
        <Alert severity="info" role="note" icon={<Info size={16} aria-hidden />}>
          {t('systemSettings.unused.group', { reason: unusedText(wholeGroup) })}
        </Alert>
      )}
      {editable.length === 0 && body}
      {editable.length > 0 && (
        <Form ref={form} onSubmit={submit} aria-label={t('systemSettings.groupForm', { group: t(`systemSettings.groups.${group.name}`) })}>
          <Stack sx={{ gap: 1.5 }}>
            {body}
            {error !== undefined &&
              (problems.length === 0 ? (
                <ErrorMessage error={error} />
              ) : (
                otherProblems.length > 0 && (
                  <ErrorMessage error={error}>
                    {t('systemSettings.problems')}
                    <ProblemList problems={otherProblems} />
                  </ErrorMessage>
                )
              ))}
            <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap', alignItems: 'center' }}>
              {actions}
              <Button ref={saveButton} type="submit" variant="contained" size="large" loading={saving} disabled={changes.length === 0 && !saveUnchanged}>
                {submitLabel ?? t('systemSettings.save')}
              </Button>
            </Stack>
          </Stack>
        </Form>
      )}
      <ConfirmDialog
        open={confirmMode}
        onOpenChange={setConfirmMode}
        title={t('systemSettings.modeChangeTitle')}
        description={t('systemSettings.modeChangeDescription', { mode: nextMode })}
        confirmLabel={t('systemSettings.confirmModeChange')}
        onConfirm={() => {
          setConfirmGone(false)
          void save()
        }}
        onClosed={() => setConfirmGone(true)}
      />
    </Stack>
  )
}

/** The server's spelling of an enum value ("TwentyFourHour") as the schema writes it ("TWENTY_FOUR_HOUR"). */
const schemaName = (choice: string) => choice.replace(/([a-z])([A-Z])/g, '$1_$2').toUpperCase()

/** The settings whose choices have words of their own (`systemSettings.choices.<set>.<choice>`), by the set they take them from. */
const CHOICE_SETS: Record<string, string> = {
  'Auth:Mode': 'authMode',
  'Smtp:Security': 'smtpSecurity',
  'Recognition:Provider': 'recognitionProvider',
  'Recognition:OpenAiCompatible:ResponseFormat': 'responseFormat',
  'Backups:Environment': 'backupEnvironment',
  OTEL_EXPORTER_OTLP_PROTOCOL: 'otlpProtocol',
}

/** Choice sets whose words say it all (user decision): offered without the value after them; the help names the values. */
const WORDS_ALONE = new Set(['backupEnvironment'])

/**
 * A choice as the page offers it, in words with the value as the server spells it after them ("Local accounts (Standalone)"),
 * since that is what the documentation and the environment use: a time or date format by an example of it alone (a name
 * such as DayMonthYearDots says little), a unit by its name alone, a set of `WORDS_ALONE` by its words alone. A log level, which says itself, and a choice without words of its own (a spelling
 * still accepted) as it is.
 */
function useChoiceLabel() {
  const { t, i18n } = useTranslation()
  // The examples in the tag the pages write with (useFormat's), so each shows what the format gives.
  const language = validLocale(i18n.language, i18n.resolvedLanguage)
  return (key: string, choice: string): string => {
    // A format this page does not know (a newer server's, while the tab waits for its update) as the server spells it: it has no example.
    if (key === 'Defaults:TimeFormat') {
      const format = TIME_FORMATS.find((f) => f === schemaName(choice))
      return format ? t('formats.option', { name: t(`formats.times.${format}`), example: sampleTime(language, format) }) : choice
    }
    if (key === 'Defaults:DateFormat') {
      const format = DATE_FORMATS.find((f) => f === schemaName(choice))
      return format ? sampleDate(language, format) : choice
    }
    const set = key === 'Defaults:DistanceUnit' ? 'distance' : key === 'Defaults:VolumeUnit' ? 'volume' : CHOICE_SETS[key]
    const wordsKey = set === 'distance' || set === 'volume' ? `units.${set}.${schemaName(choice)}` : `systemSettings.choices.${set}.${choice}`
    if (!set || !i18n.exists(wordsKey)) return choice
    const words = String(t(wordsKey as never))
    return set === 'distance' || set === 'volume' || WORDS_ALONE.has(set) || words === choice ? words : t('systemSettings.choiceWithValue', { words, value: choice })
  }
}

/** A read-only value as words: a secret only as set or not, a list on one line, days by their names, nothing as a dash. */
function shown(s: Setting, t: TFunction, weekday: (day: string) => string, choiceLabel: (key: string, choice: string) => string): string {
  if (s.isSecret) return s.isSet ? t('systemSettings.secretSetShort') : t('systemSettings.secretNotSetShort')
  if (!s.value) return t('common.none')
  if (s.kind === 'CHOICE') {
    const choice = s.choices.find((c) => c.toLowerCase() === s.value!.toLowerCase())
    return choice ? choiceLabel(s.key, choice) : s.value
  }
  if (s.kind === 'DAYS') return daysOf(s.value).map((day) => weekday(day)).join(', ') || t('common.none')
  return s.kind === 'LIST' ? s.value.split('\n').join(', ') : s.value
}

/**
 * One setting's control. `unused`: another setting leaves it unused now, so it is shown but cannot be changed; `reason` says why in its
 * hint (a disabled control takes no focus, so the reason is written out next to it, not left to a tooltip). Null when the group says it once.
 */
function SettingControl({
  setting: s,
  groupAppliesAtOnce,
  label,
  value,
  cleared,
  unused,
  reason,
  required,
  missing,
  problem,
  selectRef,
  onChange,
  onClear,
}: {
  setting: Setting
  /** What the group's note says; a setting that applies otherwise (the password minimum in the Sign-in group) says so in a badge. */
  groupAppliesAtOnce: boolean
  label: string
  value: string
  cleared: boolean
  unused: boolean
  reason: string | null
  required: boolean
  missing: boolean
  problem: string | undefined
  selectRef: Ref<{ focus: () => void }>
  onChange: (value: string) => void
  onClear: (clear: boolean) => void
}) {
  return (
    // The question mark beside the control, level with its label: what the setting does and its variable, kept out of the way.
    <Stack direction="row" sx={{ gap: 0.5, alignItems: 'flex-start' }}>
      <Box sx={{ flex: 1, minWidth: 0 }}>
        <Control
          setting={s}
          groupAppliesAtOnce={groupAppliesAtOnce}
          label={label}
          value={value}
          cleared={cleared}
          unused={unused}
          reason={reason}
          required={required}
          missing={missing}
          problem={problem}
          selectRef={selectRef}
          onChange={onChange}
          onClear={onClear}
        />
      </Box>
      <SettingHelp settingKey={s.key} label={label} />
    </Stack>
  )
}

function Control({
  setting: s,
  groupAppliesAtOnce,
  label,
  value,
  cleared,
  unused,
  reason,
  required,
  missing,
  problem,
  selectRef,
  onChange,
  onClear,
}: {
  setting: Setting
  /** What the group's note says; a setting that applies otherwise (the password minimum in the Sign-in group) says so in a badge. */
  groupAppliesAtOnce: boolean
  label: string
  value: string
  cleared: boolean
  unused: boolean
  reason: string | null
  /** A choice that must be made: no "Default (not set)", an empty one waits for the person (`missing`: the submit found it empty). */
  required: boolean
  missing: boolean
  /** What the server refused about the value at the last save, said at the control. */
  problem: string | undefined
  selectRef: Ref<{ focus: () => void }>
  onChange: (value: string) => void
  onClear: (clear: boolean) => void
}) {
  const { t } = useTranslation()
  const choiceLabel = useChoiceLabel()
  // Where the value comes from, and a restart it waits for, beside the label; the hint keeps only what needs a sentence.
  const badges: LabelBadge[] = [
    { text: t(`systemSettings.sourceShort.${s.source}`), spoken: t(`systemSettings.source.${s.source}`) },
    ...(s.restartPending ? [{ text: t('systemSettings.restartShort'), spoken: t('systemSettings.pendingRestart'), tone: 'warning' as const }] : []),
    ...(s.appliesAtOnce && !groupAppliesAtOnce ? [{ text: t('systemSettings.atOnceShort'), spoken: t('systemSettings.atOnce') }] : []),
  ]
  const hint =
    [reason, s.kind === 'LIST' && t('systemSettings.listHint')].filter((part): part is string => typeof part === 'string' && part.length > 0).join(' · ') ||
    undefined

  switch (s.kind) {
    case 'BOOLEAN':
      return <LabeledSwitch label={label} hint={hint} badges={badges} checked={isOn(value)} disabled={unused} onChange={(checked) => onChange(checked ? 'true' : 'false')} />
    case 'CHOICE': {
      // The server takes a choice in any case, and spellings it no longer offers ("Ssl"): such a value shows as the choice it means, or as
      // it is written, never as an empty field.
      const choice = s.choices.find((c) => c.toLowerCase() === value.toLowerCase()) ?? value
      const options = [...(required ? [] : [{ value: '', label: t('systemSettings.useDefault') }]), ...s.choices.map((c) => ({ value: c, label: choiceLabel(s.key, c) }))]
      if (choice !== '' && !s.choices.includes(choice)) options.push({ value: choice, label: choice })
      return (
        <LabeledSelect
          label={label}
          hint={hint}
          badges={badges}
          value={choice}
          placeholder={t(required ? 'systemSettings.chooseOne' : 'systemSettings.useDefault')}
          disabled={unused}
          onChange={onChange}
          options={options}
          error={missing ? t('systemSettings.choiceRequired') : problem}
          selectRef={selectRef}
        />
      )
    }
    case 'DAYS':
      return <DaysControl label={label} hint={hint} badges={badges} value={value} disabled={unused} onChange={onChange} />
    case 'SECRET':
      return (
        <Stack sx={{ gap: 0.5 }}>
          <Field name={s.key} label={label} badges={badges} refusal={problem} hint={[s.isSet ? t('systemSettings.secretSet') : t('systemSettings.secretNotSet'), hint].filter(Boolean).join(' · ')}>
            <PasswordInput
              autoComplete="new-password"
              showLabel={t('forms.password.showSecret')}
              value={value}
              disabled={cleared || unused}
              onChange={(e) => onChange(e.target.value)}
            />
          </Field>
          {s.isSet && (
            <FormControlLabel control={<Checkbox checked={cleared} disabled={unused} onChange={(e) => onClear(e.target.checked)} />} label={t('systemSettings.clearSecret')} />
          )}
        </Stack>
      )
    case 'INTEGER':
    case 'DECIMAL': {
      const integer = s.kind === 'INTEGER'
      return (
        <Field
          name={s.key}
          label={label}
          badges={badges}
          hint={hint}
          refusal={problem}
          invalid={{ message: t(integer ? 'systemSettings.wholeNumber' : 'forms.numberInvalid'), test: (v) => v.trim() !== '' && !(integer ? /^-?\d+$/ : /^-?\d+([.,]\d+)?$/).test(v.trim()) }}
        >
          <FieldInput inputMode={integer ? 'numeric' : 'decimal'} value={value} disabled={unused} onChange={(e) => onChange(e.target.value)} />
        </Field>
      )
    }
    case 'MULTILINE':
    case 'LIST':
      return (
        <Field name={s.key} label={label} badges={badges} hint={hint} refusal={problem}>
          <FieldInput multiline minRows={3} value={value} disabled={unused} onChange={(e) => onChange(e.target.value)} />
        </Field>
      )
    default:
      return (
        <Field name={s.key} label={label} badges={badges} hint={hint} refusal={problem}>
          <FieldInput value={value} disabled={unused} onChange={(e) => onChange(e.target.value)} />
        </Field>
      )
  }
}

/** Days of the week as checkboxes, from Monday, in a group named by the setting's label; none ticked chooses no day. */
function DaysControl({
  label,
  hint,
  badges,
  value,
  disabled,
  onChange,
}: {
  label: string
  hint?: string
  badges: readonly LabelBadge[]
  value: string
  disabled: boolean
  onChange: (value: string) => void
}) {
  const { weekday } = useFormat()
  const id = useId()
  const chosen = daysOf(value)
  const describedBy = [badges.length > 0 && `${id}-badges`, hint && `${id}-hint`].filter(Boolean).join(' ') || undefined
  return (
    // A group named by its label (not a fieldset: the badges beside the label would have to sit inside its legend, and so in its name).
    <Box role="group" aria-labelledby={`${id}-label`} aria-describedby={describedBy}>
      <Stack direction="row" sx={{ gap: 1, alignItems: 'center', flexWrap: 'wrap', mb: 0.5 }}>
        <Typography id={`${id}-label`} variant="body2" sx={{ fontWeight: 700 }}>
          {label}
        </Typography>
        {badges.length > 0 && <LabelBadges id={`${id}-badges`} badges={badges} />}
      </Stack>
      <Stack direction="row" sx={{ flexWrap: 'wrap', columnGap: 1 }}>
        {WEEK.map((day) => (
          <FormControlLabel
            key={day}
            label={weekday(day)}
            control={
              <Checkbox
                checked={chosen.includes(day)}
                disabled={disabled}
                onChange={(e) => onChange(daysValue(e.target.checked ? [...chosen, day] : chosen.filter((d) => d !== day)))}
              />
            }
          />
        ))}
      </Stack>
      {hint && (
        <Typography id={`${id}-hint`} variant="caption" sx={{ color: 'text.secondary' }}>
          {hint}
        </Typography>
      )}
    </Box>
  )
}
