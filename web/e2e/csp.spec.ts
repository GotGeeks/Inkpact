import { EXPECTS_CSP_VIOLATIONS, expect, test } from './fixtures'

test('the document carries the strict policy and the other security headers', async ({ page }) => {
  const response = await page.goto('/')
  const headers = response!.headers()

  const policy = headers['content-security-policy']!
  expect(policy).toContain("default-src 'self'")
  expect(policy).toContain("script-src 'self'")
  expect(policy).toContain("style-src 'self'")
  expect(policy).toContain("frame-ancestors 'none'")
  expect(policy).toContain("object-src 'none'")
  expect(policy).not.toContain('unsafe-inline')
  expect(policy).not.toContain('unsafe-eval')
  expect(policy).not.toContain('upgrade-insecure-requests')

  expect(headers['x-content-type-options']).toBe('nosniff')
  expect(headers['referrer-policy']).toBe('no-referrer')
  expect(headers['x-frame-options']).toBe('DENY')
  expect(headers['permissions-policy']).toContain('camera=()')
  expect(headers['server']).toBeUndefined()
})

// Without this, a harness that stopped seeing violations would stay green forever. Eval is not part of it:
// Playwright's own evaluate is not subject to the page's eval restriction, so the header test covers that
// (no unsafe-eval in the policy).
test('the suite sees inline script and inline style being blocked', async ({
  page,
  cspViolations,
}, testInfo) => {
  testInfo.annotations.push({ type: EXPECTS_CSP_VIOLATIONS })
  await page.goto('/')

  await page.evaluate(() => {
    const script = document.createElement('script')
    script.textContent = 'window.__inlineScriptRan = true'
    document.head.appendChild(script)

    document.body.setAttribute('style', 'color: red')
  })

  await expect
    .poll(() => cspViolations.map((v) => v.directive))
    .toEqual(expect.arrayContaining(['script-src-elem', 'style-src-attr']))
  expect(
    await page.evaluate(() => (window as unknown as Record<string, unknown>).__inlineScriptRan),
  ).toBeUndefined()
})

test('the app cannot be framed, not even by itself', async ({ page }) => {
  await page.goto('/')

  await page.evaluate(() => {
    const frame = document.createElement('iframe')
    frame.src = '/'
    document.body.appendChild(frame)
  })
  await page.waitForTimeout(750)

  // A blocked frame holds an error or empty document. Asked from outside the page, so that the page
  // itself never has to read a document it is not allowed to.
  const frames = page.frames().filter((frame) => frame !== page.mainFrame())
  expect(frames).toHaveLength(1)
  expect(await frames[0]!.locator('h1').count()).toBe(0)
})
