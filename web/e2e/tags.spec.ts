import { expect, test } from './fixtures'
import { closeDrawer, openSidebar, rowFor } from './helpers'
import { importJex, openImportPage } from './importHelpers'
import { generateJex } from './jex'

const unique = (project: string) => `${project}-${Date.now().toString(36)}`

/**
 * Tags, from the import, on a page and from the "Tags" view (run B.2). The standard export's "Pictures" note already
 * carries a tag, so that imported tags show without typing anything first.
 */
test('an imported tag shows on its page and opens the Tags view, where its pages are listed', async ({
  page,
}, testInfo) => {
  test.setTimeout(180_000)
  const jex = generateJex(unique(testInfo.project.name))
  await openImportPage(page)
  await importJex(page, jex)

  await openSidebar(page)
  await rowFor(page, jex.titles.notebook)
    .getByRole('button', { name: `Expand ${jex.titles.notebook}` })
    .click()
  await rowFor(page, jex.titles.pictures).click()
  await closeDrawer(page)
  await expect(page.getByTestId('page-title')).toHaveValue(jex.titles.pictures)

  const pill = page.getByRole('link', { name: jex.titles.tag })
  await expect(pill).toBeVisible()
  await pill.click()

  await expect(page.getByLabel('Tag name')).toHaveValue(jex.titles.tag)
  await expect(page.getByTestId('tag-pages-list')).toContainText(jex.titles.pictures)
})

test('a tag can be added and removed on a page, and a new one is offered to other pages at once', async ({
  page,
}, testInfo) => {
  test.setTimeout(180_000)
  const jex = generateJex(unique(testInfo.project.name))
  const tagName = `new-${unique(testInfo.project.name)}`
  await openImportPage(page)
  await importJex(page, jex)

  await openSidebar(page)
  await rowFor(page, jex.titles.notebook)
    .getByRole('button', { name: `Expand ${jex.titles.notebook}` })
    .click()
  await rowFor(page, jex.titles.pictures).click()
  await closeDrawer(page)
  await expect(page.getByTestId('page-title')).toHaveValue(jex.titles.pictures)

  await page.getByTestId('add-tag-button').click()
  await page.getByTestId('add-tag-field').fill(tagName)
  await page.getByRole('option', { name: `Create tag “${tagName}”` }).click()
  await expect(page.getByRole('link', { name: tagName })).toBeVisible()

  // Offered on another page right away, from the workspace's tag list, with no reload.
  await openSidebar(page)
  await rowFor(page, jex.titles.notebook).click()
  await closeDrawer(page)
  await page.getByTestId('add-tag-button').click()
  await page.getByTestId('add-tag-field').fill(tagName.slice(0, -2))
  await expect(page.getByRole('option', { name: tagName, exact: true })).toBeVisible()
  await page.keyboard.press('Escape')

  // Removed again from the Pictures note.
  await openSidebar(page)
  await rowFor(page, jex.titles.pictures).click()
  await closeDrawer(page)
  await page.getByRole('button', { name: `Remove tag ${tagName}` }).click()
  await expect(page.getByRole('link', { name: tagName })).toBeHidden()
})

test('a tag can be renamed from its own page, and the rename shows on the pages that have it', async ({
  page,
}, testInfo) => {
  test.setTimeout(180_000)
  const jex = generateJex(unique(testInfo.project.name))
  await openImportPage(page)
  await importJex(page, jex)

  await openSidebar(page)
  await rowFor(page, jex.titles.notebook)
    .getByRole('button', { name: `Expand ${jex.titles.notebook}` })
    .click()
  await rowFor(page, jex.titles.pictures).click()
  await closeDrawer(page)
  await page.getByRole('link', { name: jex.titles.tag }).click()

  const renamed = `${jex.titles.tag}-renamed`
  const field = page.getByLabel('Tag name')
  await field.fill(renamed)
  await page.keyboard.press('Tab')
  await expect(field).toHaveValue(renamed)

  await openSidebar(page)
  await rowFor(page, jex.titles.pictures).click()
  await closeDrawer(page)
  await expect(page.getByRole('link', { name: renamed })).toBeVisible()
})
