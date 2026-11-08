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

/** Whether a tag of the workspace already has this exact name (case and accents folded). */
export function hasExactMatch(tags: readonly WorkspaceTag[], query: string): boolean {
  const wanted = fold(query.trim())
  return wanted !== '' && tags.some((tag) => fold(tag.name) === wanted)
}
