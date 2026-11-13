import type { JSONContent } from '@tiptap/core'

/** The kinds of version the server makes (run B.5b). The last two come with restore. */
export type VersionKind = 'automatic' | 'rename' | 'restore' | 'before_restore'

/**
 * Where a page's history stands: `ready` (everything is in a version), `pending` (something waits to be captured),
 * `failed` (the page could not be read), `full` (the workspace's cap is reached) or `off` (history is off).
 */
export type HistoryState = 'ready' | 'pending' | 'failed' | 'full' | 'off'

export interface VersionSummary {
  id: string
  createdAt: string
  kind: VersionKind
  title: string
  /** Who made it, when that is one person (a rename, a restore); null for an automatic version. */
  createdBy: { id: string; displayName: string } | null
  bytes: number
  /** Set while no retention or cap removes it: the page as it was before a restore, kept for a day. */
  protectedUntil?: string | null
}

export interface VersionList {
  versions: VersionSummary[]
  /** The cursor for the versions after these; null when there are none. */
  next: string | null
  state: HistoryState
  failure: string | null
  /** The page's content as stored when the list was read: a restore sends it back, and is refused if the page changed. */
  pageContent: string
}

/** A version to look at: its title, and its content as the editor's document (ProseMirror JSON). */
export interface VersionDetail {
  id: string
  createdAt: string
  kind: VersionKind
  title: string
  content: JSONContent
}

/** What a restore made: the version that keeps the page as it was, the restore's own, and the page's title and version after it. */
export interface RestoreAnswer {
  beforeVersionId: string
  restoreVersionId: string
  title: string
  version: number
}
