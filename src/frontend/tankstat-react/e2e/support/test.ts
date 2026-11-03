import { test as base, type Browser, type BrowserContext, type BrowserContextOptions } from '@playwright/test'

export { expect } from '@playwright/test'

/**
 * No service worker in the page (#236): `main.tsx` registers one only where the browser has it, so the "Tankstat can now be used offline."
 * toast never comes at a moment of its own, over the control a phone test is about to tap at the bottom of the screen. Only
 * `offline.e2e.ts` keeps the worker, whose cache serves the lazy chunks while offline; it imports `@playwright/test` itself.
 */
async function withoutWorker(context: BrowserContext): Promise<BrowserContext> {
  await context.addInitScript(() => {
    delete (Navigator.prototype as { serviceWorker?: unknown }).serviceWorker
  })
  return context
}

/** Every e2e test but the offline ones: the page's context has no service worker. */
export const test = base.extend({
  // Playwright's fixture callback is named `provide` here: `use` reads as a React hook to the linter.
  context: async ({ context }, provide) => {
    await provide(await withoutWorker(context))
  },
})

/** A second person or device (`browser.newContext()`), without a service worker as well. */
export const newContext = async (browser: Browser, options?: BrowserContextOptions) => withoutWorker(await browser.newContext(options))
