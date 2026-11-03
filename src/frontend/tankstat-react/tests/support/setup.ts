import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { MotionGlobalConfig } from 'motion/react'
import { afterEach } from 'vitest'
import { resetSignedOutHere } from '../../src/auth/signedOutHere.ts'
import { resetFormats } from '../../src/i18n/formatStore.ts'
import { i18n, initI18n } from '../../src/i18n/index.ts'
import { connectivity } from '../../src/offline/connectivity.ts'
import { deviceData } from '../../src/offline/deviceData.ts'
import { appUpdate } from '../../src/pwa/appUpdate.ts'
import { installPrompt } from '../../src/pwa/installPrompt.ts'
import { offlineReady } from '../../src/pwa/offlineReady.ts'

await initI18n('en')

// Pages are lazy: the first test of a file waits for App and its page to load, which with coverage on and the CPU shared with other
// test files (CI) can take well over the 1 s default of findBy*/waitFor; 3 s still missed now and then there. Stays well below the
// test timeout (15 s, vite.config.ts), so a miss still names the element. Only a failing test waits this long.
configure({ asyncUtilTimeout: 5000 })

// Motion would keep a leaving item in the DOM while it animates out: tests check the outcome, not the animation.
MotionGlobalConfig.skipAnimations = true

afterEach(async () => {
  cleanup()
  window.localStorage.clear()
  window.sessionStorage.clear()
  resetFormats() // the browser's copy is gone: as the language writes times and dates
  document.documentElement.classList.remove('surface-transparent', 'surface-opaque') // glossy again: the surface style lives on <html>
  installPrompt.reset()
  appUpdate.reset()
  offlineReady.reset()
  resetSignedOutHere() // no sign-out being told, none waiting to be told again (before connectivity changes: nothing left to wake)
  connectivity.reset()
  deviceData.reset() // a fresh, empty device: what one test kept is never another's
  await i18n.changeLanguage('en')
})

// jsdom lacks a few browser APIs that MUI and MUI X (pickers, grid, charts, popovers) rely on.
Element.prototype.hasPointerCapture ??= () => false
Element.prototype.setPointerCapture ??= () => {}
Element.prototype.releasePointerCapture ??= () => {}
Element.prototype.scrollIntoView ??= () => {}
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
}
