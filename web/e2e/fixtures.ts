import { test as base, expect, type Page } from '@playwright/test'

export interface CspViolation {
  page: string
  directive: string
  blockedUri: string
  source: string
  sample: string
}

/** Annotation for the one test that provokes violations on purpose, to prove the suite sees them. */
export const EXPECTS_CSP_VIOLATIONS = 'expects-csp-violations'

/**
 * Records every Content-Security-Policy violation the browser reports, and every uncaught page error.
 * The listener is added before any page script runs, and talks to the test through a function the page
 * cannot be blocked from calling, so the policy cannot hide its own violations.
 */
async function collect(page: Page, violations: CspViolation[], errors: string[]) {
  await page.exposeFunction('__reportCspViolation', (v: Omit<CspViolation, 'page'>) => {
    violations.push({ ...v, page: new URL(page.url()).pathname })
  })
  await page.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (e) => {
      const report = window as unknown as {
        __reportCspViolation?: (v: Record<string, string>) => void
      }
      report.__reportCspViolation?.({
        directive: e.effectiveDirective,
        blockedUri: e.blockedURI,
        source: `${e.sourceFile ?? ''}:${e.lineNumber ?? ''}:${e.columnNumber ?? ''}`,
        sample: e.sample ?? '',
      })
    })
  })
  page.on('pageerror', (error) => errors.push(error.message))
}

export const test = base.extend<{ cspViolations: CspViolation[]; pageErrors: string[] }>({
  cspViolations: [
    async ({ page, pageErrors }, use, testInfo) => {
      const violations: CspViolation[] = []
      await collect(page, violations, pageErrors)
      await use(violations)

      // A test that already failed is only reported: the screenshot Playwright takes of it hides the caret with a
      // <style> it adds to the page, which WebKit blocks and reports as a violation of the app's policy. The test is
      // red either way, and a violation of the app's own shows on the retry or on the next run.
      if (testInfo.status !== testInfo.expectedStatus) {
        if (violations.length > 0)
          await testInfo.attach('csp-violations', {
            body: JSON.stringify(violations, null, 2),
            contentType: 'application/json',
          })
        return
      }

      // Checked here, so no spec has to remember to assert it.
      if (!testInfo.annotations.some((a) => a.type === EXPECTS_CSP_VIOLATIONS)) {
        expect(violations, 'CSP violations').toEqual([])
      }
    },
    { auto: true },
  ],
  pageErrors: [
    async ({}, use) => {
      const errors: string[] = []
      await use(errors)
      expect(errors, 'uncaught page errors').toEqual([])
    },
    { auto: true },
  ],
})

export { expect }
