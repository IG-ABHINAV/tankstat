/**
 * Which system settings are used with the values the page holds now: a setting another one leaves unused (the OIDC settings while sign-in
 * is off, the mail server's port while there is no mail server) is shown but cannot be changed, with the reason. The values are what the
 * form says, typed but not saved yet, so a choice greys out what it leaves unused at once. A controlling value the page cannot know (one
 * cleared to the value below the settings file) leaves everything usable: nothing is greyed out on a guess.
 */

/** Why a setting is not used: another setting's value is not one of those it is used with, or another setting is empty. */
export type Unused = { kind: 'value'; setting: string; value: string; usedWith: readonly string[] } | { kind: 'empty'; setting: string }

/** The value of a setting as the page holds it; undefined when it cannot be known. */
export type ValueOf = (key: string) => string | undefined

const MODE = 'Auth:Mode'
const PROVIDER = 'Recognition:Provider'
const SMTP_HOST = 'Smtp:Host'
/** The one switch of telemetry: without it nothing is exported (a signal's own endpoint, environment only, never turns it on). */
const OTLP_ENDPOINT = 'OTEL_EXPORTER_OTLP_ENDPOINT'
// (Nothing set anywhere, these run as the built-in defaults the server sends: no sign-in, no photo provider, no mail server.)

interface Rule {
  /** The settings it is about: a key, or every key below a prefix ending with a colon. */
  keys: readonly string[]
  /** The setting that decides. */
  on: string
  /** Used only while the deciding setting is one of these (in any case); without it, used only while the deciding setting is not empty. */
  usedWith?: readonly string[]
}

/** In order: the first rule that leaves a setting unused gives the reason (the sign-in mode before the mail server). */
const RULES: readonly Rule[] = [
  { keys: ['Auth:PublicUrl', 'Auth:Standalone:', 'Smtp:'], on: MODE, usedWith: ['Standalone'] },
  { keys: ['Auth:AccessTokenMinutes', 'Auth:RefreshTokenDays', 'Auth:RefreshRotationGraceSeconds'], on: MODE, usedWith: ['Standalone', 'Oidc'] },
  { keys: ['Auth:AdminEmails'], on: MODE, usedWith: ['Oidc', 'ProxyHeader'] },
  { keys: ['Auth:Oidc:'], on: MODE, usedWith: ['Oidc'] },
  { keys: ['Auth:ProxyHeader:'], on: MODE, usedWith: ['ProxyHeader'] },
  { keys: ['Smtp:Port', 'Smtp:Security', 'Smtp:Username', 'Smtp:Password'], on: SMTP_HOST },
  { keys: ['Recognition:'], on: PROVIDER, usedWith: ['OpenAiCompatible'] },
  { keys: ['OTEL_EXPORTER_OTLP_PROTOCOL', 'OTEL_EXPORTER_OTLP_HEADERS', 'OTEL_SERVICE_NAME', 'Telemetry:TraceDatabase'], on: OTLP_ENDPOINT },
]

/** Whether `key` is among `keys`: a key, or every key below a prefix ending with a colon (the rules here, the page's sections). */
export const keyIn = (keys: readonly string[], key: string) => keys.some((k) => (k.endsWith(':') ? key.startsWith(k) : key === k))

const covers = (rule: Rule, key: string) => key !== rule.on && keyIn(rule.keys, key)

/** Why a setting is not used with the values the page holds; null when it is used (or when that cannot be told). */
export function unusedReason(key: string, valueOf: ValueOf): Unused | null {
  for (const rule of RULES) {
    if (!covers(rule, key)) continue
    const value = valueOf(rule.on)
    if (value === undefined) continue
    if (rule.usedWith) {
      const used = rule.usedWith.some((v) => v.toLowerCase() === value.trim().toLowerCase())
      if (!used) return { kind: 'value', setting: rule.on, value: value.trim(), usedWith: rule.usedWith }
    } else if (value.trim() === '') return { kind: 'empty', setting: rule.on }
  }
  return null
}

/** A setting as the server describes it: the value it runs with (the built-in default when nothing sets it), and whether the settings file holds it. */
export interface ServerValue {
  value: string | null
  inFile: boolean
}

/**
 * A setting's value as the page holds it: what the form says, else what it runs with. A value the settings file holds and the form cleared
 * ("Default (not set)") falls to the one below the file, which the page does not know; a setting the server did not list is not known
 * either.
 */
export function valueOfDraft(server: Readonly<Record<string, ServerValue>>, draft: Readonly<Record<string, string>>): ValueOf {
  return (key) => {
    const known = server[key]
    if (!known) return undefined
    const typed = draft[key]
    if (typed !== undefined && typed !== '') return typed
    if (typed === '' && known.inFile) return undefined // cleared: whatever is below the file
    return known.value ?? ''
  }
}

/** The one reason all of a group's settings share, when every one of them is unused for it; null otherwise. */
export function sharedReason(reasons: readonly (Unused | null)[]): Unused | null {
  const first = reasons[0]
  if (!first) return null
  return reasons.every((r) => r !== null && JSON.stringify(r) === JSON.stringify(first)) ? first : null
}
