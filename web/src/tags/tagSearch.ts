import { fold } from '../editor/pageSearch'
import type { WorkspaceTag } from './types'

/** The most tags the add-tag field suggests at once. */
export const MAX_TAG_SUGGESTIONS = 8

/**
 * The workspace's tags whose name contains what was typed, case and accents ignored: names that start with it first, then
 * the others, at most `limit`. A tag the page already has is left out; with nothing typed, the first ones.
 */
export function findTags(
  tags: readonly WorkspaceTag[],
  query: string,
  exclude: ReadonlySet<string>,
  limit: number = MAX_TAG_SUGGESTIONS,
): WorkspaceTag[] {
  const wanted = fold(query.trim())
  const starts: WorkspaceTag[] = []
  const contains: WorkspaceTag[] = []
  for (const tag of tags) {
    if (exclude.has(tag.id)) continue
    const folded = fold(tag.name)
    if (wanted === '' || folded.startsWith(wanted)) starts.push(tag)
    else if (folded.includes(wanted)) contains.push(tag)
    if (starts.length >= limit) break
  }
  return [...starts, ...contains].slice(0, limit)
}

/**
 * A name as the server keeps it (`TagNames.Clean`): no control characters, tabs and line breaks as spaces, trimmed, the
 * inner spaces as they are.
 */
export function cleanTagName(name: string): string {
  return name
    .replace(/[\t\n]/g, ' ')
    .replace(/[\u0000-\u0008\u000b-\u001f\u007f-\u009f]/g, '')
    .trim()
}

/**
 * Whether a tag of the workspace already has this name by the server's rule: the same name in lower case. Accents count,
 * so "cafe" can be made next to "café", as the server allows; finding tags ignores them, matching does not.
 */
export function hasExactMatch(tags: readonly WorkspaceTag[], name: string): boolean {
  const wanted = cleanTagName(name).toLowerCase()
  return wanted !== '' && tags.some((tag) => tag.name.toLowerCase() === wanted)
}
