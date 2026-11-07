import type { PageNode } from '../tree/types'

/** A page a link can point to, as the menus list it. */
export interface PageChoice {
  id: string
  /** The title, empty for a page without one: the menu says "Untitled". */
  title: string
  /** The title of the page it is in, to tell two pages with one title apart; null at the top level. */
  parentTitle: string | null
}

/** The most pages a menu lists. */
export const MAX_PAGE_CHOICES = 8

/** Case and accents ignored: "é" is found by "e", and "Ä" by "a". */
export function fold(text: string): string {
  return text.normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase()
}

/**
 * The live pages whose title contains what was typed, for a menu that makes a link: titles that start with it first, then the
 * others, each in the order of the list given (the sidebar's), at most `limit`; with nothing typed, the first ones. The page
 * the link is made on is never offered: a link to itself is no use (and the index leaves it out). Titles are looked at as
 * the tree has them, so a page made a moment ago is found, and nothing needs the server.
 */
export function findPages(
  pages: readonly PageNode[],
  query: string,
  currentPageId: string | null,
  limit: number = MAX_PAGE_CHOICES,
): PageChoice[] {
  const wanted = fold(query.trim())
  const titles = new Map(pages.map((page) => [page.id, page.title]))
  const starts: PageChoice[] = []
  const contains: PageChoice[] = []
  for (const page of pages) {
    if (page.id === currentPageId) continue
    const folded = fold(page.title)
    const choice: PageChoice = {
      id: page.id,
      title: page.title,
      parentTitle: page.parentId === null ? null : (titles.get(page.parentId) ?? null),
    }
    if (wanted === '' || folded.startsWith(wanted)) starts.push(choice)
    else if (folded.includes(wanted)) contains.push(choice)
    if (starts.length >= limit) break
  }
  return [...starts, ...contains].slice(0, limit)
}

/** The ids whose title is shared with another page in the list: those are shown with the page they are in. */
export function sharedTitles(choices: readonly PageChoice[]): Set<string> {
  const counts = new Map<string, number>()
  for (const choice of choices) {
    const key = fold(choice.title)
    counts.set(key, (counts.get(key) ?? 0) + 1)
  }
  return new Set(
    choices.filter((choice) => (counts.get(fold(choice.title)) ?? 0) > 1).map((c) => c.id),
  )
}
