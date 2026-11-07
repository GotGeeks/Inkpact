import { expect, test } from './fixtures'
import { newPage, openClient, typeText, type Client } from './syncClients'

/**
 * The spike's browser scenarios (1, 2, 4 and 5 of docs/spike-findings.md), against the real server and the real editor:
 * two browsers of the same person edit one page; one loses its connection in different ways; they must always end with
 * the same page that has every character in it.
 */

const unique = (project: string, name: string) => `${name}-${project}-${Date.now().toString(36)}`

/** True when every character of `part` appears in `whole`, in order. */
function isSubsequence(part: string, whole: string) {
  let i = 0
  for (const char of whole) if (char === part[i]) i++
  return i === part.length
}

/**
 * Both editors have the same text, with every typed piece in it in order, and exactly one paragraph: two clients typing
 * into a new page used to make a paragraph each (run 0.3), which the first paragraph made at page creation prevents.
 */
async function expectConverged(a: Client, b: Client, pieces: string[], timeout = 15_000) {
  const total = pieces.join('').length
  await expect
    .poll(
      async () => {
        const [textA, textB] = [await a.editor.innerText(), await b.editor.innerText()]
        const paragraphs = [
          await a.editor.locator('p').count(),
          await b.editor.locator('p').count(),
        ]
        const converged =
          textA === textB &&
          textA.length === total &&
          paragraphs[0] === 1 &&
          paragraphs[1] === 1 &&
          pieces.every((piece) => isSubsequence(piece, textA))
        return converged
          ? 'converged'
          : `A=${JSON.stringify(textA)} B=${JSON.stringify(textB)} p=${paragraphs}`
      },
      { timeout, message: `both editors should converge on all of: ${pieces.join(' | ')}` },
    )
    .toBe('converged')
}

test('two browsers typing at the same time converge with nothing lost, in one paragraph', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Concurrent'))
  const [a, b] = [
    await openClient(browser, testInfo, baseURL, id),
    await openClient(browser, testInfo, baseURL, id),
  ]

  await Promise.all([
    typeText(a, 'alpha-alpha-alpha-alpha'),
    typeText(b, 'BRAVO-BRAVO-BRAVO-BRAVO'),
  ])

  const pieces = ['alpha-alpha-alpha-alpha', 'BRAVO-BRAVO-BRAVO-BRAVO']
  await expectConverged(a, b, pieces)

  // A third browser gets the same text from the server alone.
  const fresh = await openClient(browser, testInfo, baseURL, id)
  await expectConverged(a, fresh, pieces)
  for (const client of [a, b, fresh]) await client.context.close()
})

test('a browser that goes offline merges its edits when it comes back', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Offline'))
  const [a, b] = [
    await openClient(browser, testInfo, baseURL, id),
    await openClient(browser, testInfo, baseURL, id),
  ]
  await typeText(a, 'base')
  await expect(b.editor).toContainText('base')

  a.setOffline(true)
  await expect(a.status).toHaveAttribute('data-state', 'offline')
  await typeText(a, ' offline-A')
  await typeText(b, ' online-B')
  // The other side must not see the offline edit yet.
  await expect(b.editor).not.toContainText('offline-A')

  a.setOffline(false)

  await expectConverged(a, b, ['base', ' offline-A', ' online-B'])
  for (const client of [a, b]) await client.context.close()
})

test('a connection that dies silently (Wi-Fi lost on a phone) is noticed, and its edits merge after reconnecting', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Silent'))
  const [a, b] = [
    await openClient(browser, testInfo, baseURL, id),
    await openClient(browser, testInfo, baseURL, id),
  ]
  await typeText(a, 'base')
  await expect(b.editor).toContainText('base')

  // Nothing passes any more, but the browser is not told: the socket still looks open.
  a.blackhole()
  await typeText(a, ' typed-on-phone')
  await typeText(b, ' typed-on-laptop')
  await expect(b.editor).not.toContainText('typed-on-phone')

  // The client has to notice by itself, reconnect, and send what the server is missing.
  await expectConverged(a, b, ['base', ' typed-on-phone', ' typed-on-laptop'], 40_000)
  for (const client of [a, b]) await client.context.close()
})

test('the browser going offline and online drops the connection at once and reconnects without waiting', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Events'))
  const [a, b] = [
    await openClient(browser, testInfo, baseURL, id),
    await openClient(browser, testInfo, baseURL, id),
  ]
  await typeText(a, 'base')
  await expect(b.editor).toContainText('base')

  // The browser's offline mode fires the page's offline and online events; the socket is left alone.
  await a.context.setOffline(true)
  await expect(a.status).toHaveAttribute('data-state', 'offline', { timeout: 2_000 })
  await typeText(a, ' while-offline')
  await typeText(b, ' meanwhile')

  await a.context.setOffline(false)
  await expect(a.status).toHaveAttribute('data-state', 'live', { timeout: 3_000 })
  await expectConverged(a, b, ['base', ' while-offline', ' meanwhile'], 5_000)
  for (const client of [a, b]) await client.context.close()
})
