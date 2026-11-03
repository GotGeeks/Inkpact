import { vi, type Mock } from 'vitest'
import { jsonResponse } from './helpers'
import type { SearchResponse } from '../search/types'
import type { PageChange, PageNode, WorkspaceSummary } from '../tree/types'

export const workspace: WorkspaceSummary = { id: 'w1', name: 'Private', role: 'owner' }

/** Builds pages from an outline, two spaces of indent per level, in tree order. Ids are the titles. */
export function outline(lines: string[]): PageNode[] {
  const stack: string[] = []
  return lines.map((line, i) => {
    const level = (line.length - line.trimStart().length) / 2
    const title = line.trim()
    stack.length = level
    const parentId = level === 0 ? null : stack[level - 1]!
    stack[level] = title
    return {
      id: title,
      parentId,
      title,
      sortKey: `k${i}`,
      version: 1,
      updatedAt: '2026-10-26T10:00:00Z',
    }
  })
}

export interface FakeTree {
  fetch: Mock
  /** The texts that were searched for, in order. */
  searches: () => string[]
  /** The writes the client made, in order: method, path and body. */
  writes: () => { method: string; path: string; body: Record<string, unknown> }[]
  setPages: (pages: PageNode[]) => void
  setTrash: (pages: PageNode[]) => void
  /** Makes the next write fail with this status and code. */
  failNext: (status: number, code: string) => void
}

/**
 * A stand-in for the server's tree API. Reading gives what was set; a write is recorded and answered with a change
 * for the page it was about, without changing what reading gives (a test sets the pages it wants to see next).
 */
export function fakeTree(
  initial: {
    pages?: PageNode[]
    trash?: PageNode[]
    role?: WorkspaceSummary['role']
    restoredTo?: 'original' | 'root'
    /** Pages the server says are in the trash only because a page above them is. */
    hidden?: string[]
    /** Answers a search (a Response for an error); a promise lets a test hold an answer back. Without it, a search is an unexpected read. */
    search?: (
      query: string,
      signal: AbortSignal | undefined,
    ) => SearchResponse | Response | Promise<SearchResponse | Response>
  } = {},
): FakeTree {
  let pages = initial.pages ?? []
  let trash = initial.trash ?? []
  let failure: { status: number; code: string } | null = null
  const writes: { method: string; path: string; body: Record<string, unknown> }[] = []
  const searches: string[] = []

  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const path = String(input)

    if (method === 'GET') {
      if (path === '/api/auth/state') {
        return jsonResponse(200, {
          setupRequired: false,
          user: { id: 'u1', email: 'jos@example.com', displayName: 'Jos', isAdmin: true },
          csrfToken: 'token',
        })
      }
      if (path === '/api/workspaces')
        return jsonResponse(200, [{ ...workspace, role: initial.role ?? 'owner' }])
      if (path === `/api/workspaces/${workspace.id}/pages`)
        return jsonResponse(200, { treeVersion: 1, pages })
      if (path === `/api/workspaces/${workspace.id}/trash`) return jsonResponse(200, trash)
      const searchPath = `/api/workspaces/${workspace.id}/search?q=`
      if (path.startsWith(searchPath) && initial.search) {
        const query = decodeURIComponent(path.slice(searchPath.length))
        searches.push(query)
        const answer = await initial.search(query, init?.signal ?? undefined)
        if (init?.signal?.aborted) throw new DOMException('Aborted', 'AbortError')
        return answer instanceof Response ? answer : jsonResponse(200, answer)
      }
      const single = /^\/api\/pages\/(.+)$/.exec(path)
      if (single) {
        const page = [...pages, ...trash].find((p) => p.id === single[1])
        return page
          ? jsonResponse(200, {
              page,
              inTrash:
                trash.some((p) => p.id === page.id) || (initial.hidden ?? []).includes(page.id),
              workspaceId: workspace.id,
            })
          : jsonResponse(404, { code: 'not_found' })
      }
      throw new Error(`Unexpected read: ${path}`)
    }

    const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {}
    writes.push({ method, path, body })
    if (failure) {
      const { status, code } = failure
      failure = null
      return jsonResponse(status, { code })
    }

    const target = /^\/api\/pages\/([^/]+)/.exec(path)?.[1]
    const page = pages.find((p) => p.id === target) ??
      trash.find((p) => p.id === target) ?? {
        // A page the client gave an id to has that id.
        id: typeof body.id === 'string' ? body.id : 'new',
        parentId: null,
        title: String(body.title ?? ''),
        sortKey: 'a0',
        version: 1,
        updatedAt: '',
      }
    const change: PageChange = {
      page: { ...page, version: page.version + 1 },
      conflict: false,
      ...(path.endsWith('/restore') ? { restoredTo: initial.restoredTo ?? 'original' } : {}),
    }
    return jsonResponse(path.endsWith('/pages') ? 201 : 200, change)
  })

  vi.stubGlobal('fetch', fetchMock)
  return {
    fetch: fetchMock,
    searches: () => searches,
    writes: () => writes,
    setPages: (next) => {
      pages = next
    },
    setTrash: (next) => {
      trash = next
    },
    failNext: (status, code) => {
      failure = { status, code }
    },
  }
}
