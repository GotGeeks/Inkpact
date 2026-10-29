import { ApiError, api, uploadFileWithProgress } from '../api'
import { cachedLimit } from '../limits'

export type ImportState = 'queued' | 'running' | 'completed' | 'failed'

/** What the import made, per kind, and what it left alone. */
export interface ImportCounts {
  notebooks: number
  notes: number
  /** Of the notes, the ones that are to-dos. */
  todos: number
  tags: number
  images: number
  files: number
  alreadyImported: number
  /** Notes an earlier import made that changed only in Joplin, and were brought up to date. */
  updated?: number
  /** Notes nobody changed here, converted again by a newer version of the import. */
  reconverted?: number
  /** Notes that changed only here, and were left as they are. */
  keptInInkpact?: number
  /** Notes that changed in Joplin and here: Joplin's version became a copy beside the page. */
  conflicts?: number
  alreadyInTrash: number
  trashed: number
  skipped: number
  failed: number
  /** Lines left out of the report because it is full. */
  unlisted: number
}

/** Why an item did not come across as it was: a code to translate, and what exactly (a tag name, a file name). */
export interface ImportReason {
  code: string
  detail?: string | null
  count: number
}

export type ReportStatus =
  'imperfect' | 'conflict' | 'skipped' | 'failed' | 'updated' | 'conflict_copy'

export interface ReportEntry {
  kind: 'notebook' | 'note' | 'tag'
  externalId: string
  title: string
  status: ReportStatus
  reasons: ImportReason[]
  /** The page that was made, when one was. */
  pageId?: string
  /** For a conflict copy: the page that was kept as it was, beside which the copy is. */
  originalPageId?: string
}

export interface ImportRun {
  id: string
  workspaceId: string
  fileName: string
  state: ImportState
  /** Why a failed import stopped. */
  errorCode: string | null
  totalItems: number
  processedItems: number
  createdAt: string
  startedAt: string | null
  finishedAt: string | null
  counts: ImportCounts
  /** Null until the import has ended. */
  report: ReportEntry[] | null
}

export function hasEnded(run: Pick<ImportRun, 'state'>): boolean {
  return run.state === 'completed' || run.state === 'failed'
}

const limit = cachedLimit('/api/imports/limits')

/** The largest export the server takes, asked once. Unknown (null) when it could not be asked; then the server decides. */
export const importMaxFileSize = limit.get

/** For the tests: ask the server again. */
export const forgetImportLimit = limit.forget

/** The workspace has an import waiting or running already. `importId` is set when it is the person's own. */
export class ImportInProgressError extends ApiError {
  constructor(readonly importId: string | null) {
    super(409, 'import_in_progress')
    this.name = 'ImportInProgressError'
  }
}

/** Throws ImportInProgressError when the workspace has an open import. */
async function ensureNoOpenImport(workspaceId: string) {
  const open = await api<{ open: boolean; id: string | null }>(
    'GET',
    `/api/workspaces/${encodeURIComponent(workspaceId)}/imports/open`,
  )
  if (open.open) throw new ImportInProgressError(open.id ?? null)
}

/**
 * Sends a JEX file to the server, which imports it in the background. The server refuses a file that is empty, too large
 * or one import too many before it reads the body, and a browser still sending a large body often sees that answer as a
 * reset connection. So all three are checked here before anything is sent, and a dropped connection is checked again.
 */
export async function startJoplinImport(
  workspaceId: string,
  file: File,
  onProgress: (sent: number, total: number) => void,
  signal?: AbortSignal,
): Promise<{ id: string }> {
  if (file.size === 0) throw new ApiError(400, 'import_empty')
  const max = await importMaxFileSize()
  if (max !== null && file.size > max) throw new ApiError(413, 'import_too_large')
  await ensureNoOpenImport(workspaceId)

  const path = `/api/workspaces/${encodeURIComponent(workspaceId)}/imports/joplin?name=${encodeURIComponent(file.name)}`
  try {
    return await uploadFileWithProgress<{ id: string }>(path, file, onProgress, signal)
  } catch (error) {
    // Another import may have started in between; then that is the reason, not the network.
    if (error instanceof ApiError && error.code === 'network_error') {
      await ensureNoOpenImport(workspaceId).catch((cause: unknown) => {
        if (cause instanceof ImportInProgressError) throw cause
      })
    }
    throw error
  }
}

export function getImport(id: string): Promise<ImportRun> {
  return api<ImportRun>('GET', `/api/imports/${encodeURIComponent(id)}`)
}
