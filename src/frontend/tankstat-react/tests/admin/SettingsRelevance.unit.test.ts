import { describe, expect, it } from 'vitest'
import { changesOf, draftOf } from '../../src/admin/settingsDraft.ts'
import { sharedReason, unusedReason, valueOfDraft, type ServerValue } from '../../src/admin/settingsRelevance.ts'
import { layoutOf } from '../../src/admin/settingsSections.ts'
import type { SystemSettingFieldsFragment } from '../../src/gql/generated.ts'

const valueOf = (values: Record<string, string | undefined>) => (key: string) => values[key]

describe('which settings are used', () => {
  it('follows the sign-in mode: each mode uses its own settings, and none uses those of another', () => {
    const none = valueOf({ 'Auth:Mode': 'None' })
    for (const key of ['Auth:PublicUrl', 'Auth:Standalone:LockoutMinutes', 'Smtp:Host', 'Auth:AccessTokenMinutes', 'Auth:AdminEmails', 'Auth:Oidc:ClientId', 'Auth:ProxyHeader:TrustedProxies'])
      expect(unusedReason(key, none)).not.toBeNull()

    const standalone = valueOf({ 'Auth:Mode': 'Standalone', 'Smtp:Host': 'mail.example' })
    expect(unusedReason('Auth:PublicUrl', standalone)).toBeNull()
    expect(unusedReason('Smtp:Port', standalone)).toBeNull()
    expect(unusedReason('Auth:AccessTokenMinutes', standalone)).toBeNull()
    expect(unusedReason('Auth:Oidc:Authority', standalone)).toEqual({ kind: 'value', setting: 'Auth:Mode', value: 'Standalone', usedWith: ['Oidc'] })
    expect(unusedReason('Auth:AdminEmails', standalone)).toEqual({ kind: 'value', setting: 'Auth:Mode', value: 'Standalone', usedWith: ['Oidc', 'ProxyHeader'] })

    const oidc = valueOf({ 'Auth:Mode': 'oidc' }) // in any case, as the server takes it
    expect(unusedReason('Auth:Oidc:ClientSecret', oidc)).toBeNull()
    expect(unusedReason('Auth:AccessTokenMinutes', oidc)).toBeNull()
    expect(unusedReason('Auth:AdminEmails', oidc)).toBeNull()
    expect(unusedReason('Smtp:Host', oidc)).not.toBeNull()
  })

  it('never decides about the deciding setting itself, nor about settings no rule names', () => {
    const none = valueOf({ 'Auth:Mode': 'None', 'Recognition:Provider': 'None' })
    expect(unusedReason('Auth:Mode', none)).toBeNull()
    expect(unusedReason('Recognition:Provider', none)).toBeNull()
    for (const key of ['Defaults:Currency', 'Notifications:MaxPerHour', 'Sync:RetentionDays', 'Logging:LogLevel:Default']) expect(unusedReason(key, none)).toBeNull()
  })

  it('the sign-in mode before the mail server; an empty mail server leaves the rest of its settings unused, but not the server itself', () => {
    expect(unusedReason('Smtp:Port', valueOf({ 'Auth:Mode': 'None', 'Smtp:Host': '' }))).toMatchObject({ kind: 'value', setting: 'Auth:Mode' })
    const noHost = valueOf({ 'Auth:Mode': 'Standalone', 'Smtp:Host': ' ' })
    expect(unusedReason('Smtp:Port', noHost)).toEqual({ kind: 'empty', setting: 'Smtp:Host' })
    expect(unusedReason('Smtp:Password', noHost)).toEqual({ kind: 'empty', setting: 'Smtp:Host' })
    expect(unusedReason('Smtp:Host', noHost)).toBeNull()
    expect(unusedReason('Smtp:From', noHost)).toBeNull()
  })

  it('photo reading without a provider leaves its other settings unused', () => {
    expect(unusedReason('Recognition:OpenAiCompatible:Model', valueOf({ 'Recognition:Provider': 'None' }))).not.toBeNull()
    expect(unusedReason('Recognition:MinConfidence', valueOf({ 'Recognition:Provider': 'None' }))).not.toBeNull()
    expect(unusedReason('Recognition:MinConfidence', valueOf({ 'Recognition:Provider': 'OpenAiCompatible' }))).toBeNull()
  })

  it('telemetry without an endpoint leaves its other settings unused, the endpoint itself never', () => {
    const noEndpoint = valueOf({ OTEL_EXPORTER_OTLP_ENDPOINT: '' })
    for (const key of ['OTEL_EXPORTER_OTLP_PROTOCOL', 'OTEL_EXPORTER_OTLP_HEADERS', 'OTEL_SERVICE_NAME', 'Telemetry:TraceDatabase'])
      expect(unusedReason(key, noEndpoint)).toEqual({ kind: 'empty', setting: 'OTEL_EXPORTER_OTLP_ENDPOINT' })
    expect(unusedReason('OTEL_EXPORTER_OTLP_ENDPOINT', noEndpoint)).toBeNull()
    expect(unusedReason('Telemetry:TraceDatabase', valueOf({ OTEL_EXPORTER_OTLP_ENDPOINT: 'http://collector:4317' }))).toBeNull()
  })

  it('greys nothing out on a guess: a deciding value that cannot be known leaves everything used', () => {
    expect(unusedReason('Auth:Oidc:Authority', valueOf({}))).toBeNull()
    expect(unusedReason('Smtp:Port', valueOf({ 'Auth:Mode': 'Standalone' }))).toBeNull()
  })

  it("says a group's reason once only when every setting shares it", () => {
    const a = { kind: 'empty', setting: 'Smtp:Host' } as const
    expect(sharedReason([a, { ...a }])).toEqual(a)
    expect(sharedReason([a, null])).toBeNull()
    expect(sharedReason([a, { kind: 'empty', setting: 'Other' }])).toBeNull()
    expect(sharedReason([])).toBeNull()
  })
})

describe('the values the page holds', () => {
  const server: Record<string, ServerValue> = {
    'Auth:Mode': { value: 'Standalone', inFile: true },
    'Recognition:Provider': { value: 'None', inFile: false }, // the built-in default
    'Smtp:Host': { value: null, inFile: false },
  }

  it('is what the form says, else what runs; a value the file holds and the form cleared is not known', () => {
    expect(valueOfDraft(server, {})('Auth:Mode')).toBe('Standalone')
    expect(valueOfDraft(server, { 'Auth:Mode': 'Oidc' })('Auth:Mode')).toBe('Oidc')
    expect(valueOfDraft(server, { 'Auth:Mode': '' })('Auth:Mode')).toBeUndefined() // falls below the file
    expect(valueOfDraft(server, { 'Recognition:Provider': '' })('Recognition:Provider')).toBe('None') // nothing in the file to clear
    expect(valueOfDraft(server, {})('Smtp:Host')).toBe('')
    expect(valueOfDraft(server, {})('Unknown:Key')).toBeUndefined()
  })
})

const setting = (over: Partial<SystemSettingFieldsFragment> & { key: string }): SystemSettingFieldsFragment => ({
  kind: 'TEXT',
  choices: [],
  value: null,
  isSet: false,
  isSecret: false,
  source: 'DEFAULT',
  editable: true,
  restartPending: false,
  defaultValue: null,
  appliesAtOnce: false,
  ...over,
})

describe('a form of built-in defaults', () => {
  const settings = [
    setting({ key: 'Auth:AccessTokenMinutes', kind: 'INTEGER', defaultValue: '15' }),
    setting({ key: 'Auth:Standalone:AllowAdminSetPassword', kind: 'BOOLEAN', defaultValue: 'false' }),
    setting({ key: 'Defaults:Currency', value: 'HUF', isSet: true, source: 'FILE', defaultValue: 'EUR' }),
  ]

  it('starts at the value each setting runs with, and saves nothing while it stays there', () => {
    const draft = draftOf(settings)
    expect(draft.values).toEqual({ 'Auth:AccessTokenMinutes': '15', 'Auth:Standalone:AllowAdminSetPassword': 'false', 'Defaults:Currency': 'HUF' })
    expect(changesOf(settings, draft)).toEqual([])
  })

  it('emptying a setting nothing sets is no change; emptying one the file holds clears it', () => {
    const draft = draftOf(settings)
    expect(changesOf(settings, { ...draft, values: { ...draft.values, 'Auth:AccessTokenMinutes': '' } })).toEqual([])
    expect(changesOf(settings, { ...draft, values: { ...draft.values, 'Defaults:Currency': '' } })).toEqual([{ key: 'Defaults:Currency', value: null, clear: true }])
    expect(changesOf(settings, { ...draft, values: { ...draft.values, 'Auth:AccessTokenMinutes': '30' } })).toEqual([{ key: 'Auth:AccessTokenMinutes', value: '30', clear: false }])
  })
})

describe('the sections of a tab', () => {
  it('keeps the deciding settings above, and every setting no section names with them', () => {
    const keys = ['Auth:Mode', 'Auth:PublicUrl', 'Auth:Oidc:ClientId', 'Auth:AccessTokenMinutes', 'Auth:SomethingNew'].map((key) => ({ key }))
    const layout = layoutOf('AUTH', keys)
    expect(layout.lead.map((s) => s.key)).toEqual(['Auth:Mode', 'Auth:SomethingNew'])
    expect(layout.sections.map((s) => [s.id, s.settings.map((x) => x.key)])).toEqual([
      ['sessions', ['Auth:AccessTokenMinutes']],
      ['standalone', ['Auth:PublicUrl']],
      ['oidc', ['Auth:Oidc:ClientId']],
    ])
  })

  it('a group of one kind has no sections', () => {
    expect(layoutOf('DEFAULTS', [{ key: 'Defaults:Currency' }])).toEqual({ lead: [{ key: 'Defaults:Currency' }], sections: [] })
  })
})
