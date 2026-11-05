import { CombinedGraphQLErrors } from '@apollo/client/errors'
import { renderHook } from '@testing-library/react'
import { expect, it } from 'vitest'
import { useErrorText, useKeyText, useReasonText } from '../../../src/i18n/errors.ts'
import { OfflineError } from '../../../src/offline/errors.ts'
import { ApiError } from '../../../src/pictures/ApiError.ts'
import { UnreadableImageError } from '../../../src/pictures/resizeImage.ts'

const errorText = () => renderHook(() => useErrorText()).result.current
const graphQLErrors = (...errors: { message: string; extensions?: Record<string, unknown> }[]) => new CombinedGraphQLErrors({ errors })

it('words a known error key with its arguments, and knows nothing of an unknown key or one that is not text', () => {
  const keyText = renderHook(() => useKeyText()).result.current

  expect(keyText('odometer.belowPrevious', { previous: '12,000 km', date: 'Sep 30, 2026' })).toBe('The odometer cannot be lower than 12,000 km, the reading on Sep 30, 2026.')
  expect(keyText('vehicle.nameRequired', undefined)).toBe('The vehicle name is required.')
  expect(keyText('no.suchKey', {})).toBeUndefined()
  expect(keyText(42, {})).toBeUndefined()
  expect(keyText(undefined, {})).toBeUndefined()
})

it('words the saved widgets\' and the dashboard\'s keys of #273: an older app\'s save refused, a widget out of shape, the shared limit', () => {
  const keyText = renderHook(() => useKeyText()).result.current

  expect(keyText('dashboard.updateApp', {})).toBe('This version of the app can no longer save dashboards. Reload the page to update it.')
  expect(keyText('chart.invalid', {})).toBe('This chart is not valid.')
  expect(keyText('chart.limit', { max: 30 })).toBe('You can have at most 30 saved widgets for a vehicle.')
})

it('words the reason a change was not applied, falls back to its key, and says nothing without one', () => {
  const reasonText = renderHook(() => useReasonText()).result.current

  expect(reasonText({ key: 'odometer.belowPrevious', args: [{ name: 'previous', value: '12,000 km' }, { name: 'date', value: 'Sep 30, 2026' }] })).toBe(
    'The odometer cannot be lower than 12,000 km, the reading on Sep 30, 2026.',
  )
  expect(reasonText({ key: 'brand.newReason', args: [] })).toBe('brand.newReason')
  expect(reasonText(null)).toBe('')
  expect(reasonText(undefined)).toBe('')
})

it('tells calmly why the device could not answer while the server is out of reach', () => {
  const text = errorText()

  expect(text(new OfflineError('notLoaded'))).toBe('This is not on this device yet: the app keeps what you open while online, so open it once you are back online.')
  expect(text(new OfflineError('onlineOnly'))).toBe('This needs a connection to the server. It works again once you are back online.')
  expect(text(new OfflineError())).toBe('The server cannot be reached right now. This works again once you are back online.')
})

it('words a picture endpoint error by its key, else shows the server message', () => {
  const text = errorText()

  expect(text(new ApiError('vehicle.notFound', {}, 'Vehicle not found', 404))).toBe('This vehicle does not exist.')
  expect(text(new ApiError(undefined, {}, 'Payload too large', 413))).toBe('Payload too large')
  expect(text(new UnreadableImageError())).toBe('This file could not be read as a picture.')
})

it('words the first GraphQL error by its key, else its message, else says the server could not be reached', () => {
  const text = errorText()

  expect(text(graphQLErrors({ message: 'Vehicle name is required', extensions: { key: 'vehicle.nameRequired' } }))).toBe('The vehicle name is required.')
  expect(text(graphQLErrors({ message: 'Something odd', extensions: { key: 'no.suchKey' } }))).toBe('Something odd')
  expect(text(graphQLErrors({ message: 'No extensions at all' }))).toBe('No extensions at all')
  expect(text(graphQLErrors())).toBe('Cannot reach the server. Check your connection and try again.')
  expect(text(new TypeError('Failed to fetch'))).toBe('Cannot reach the server. Check your connection and try again.')
})
