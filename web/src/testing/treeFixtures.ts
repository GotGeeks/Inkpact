import { vi, type Mock } from 'vitest'
import { jsonResponse } from './helpers'
import type { SearchResponse } from '../search/types'
import type { VersionInfo } from '../stores/version'
import type { TagPagesResponse, TagRef, WorkspaceTag } from '../tags/types'
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

/** What the server answers for the pages that link to a page. */
export interface BacklinksAnswer {
  items: { id: string; title: string; parentTitle: string | null }[]
  more: boolean
}

export interface FakeTree {
  fetch: Mock
  /** The texts that were searched for, in order. */
  searches: () => string[]
  /** The writes the client made, in order: method, path and body. */
  writes: () => { method: string; path: string; body: Record<string, unknown> }[]
  setPages: (pages: PageNode[]) => void
  setTrash: (pages: PageNode[]) => void
  /** What the server says links to a page, from now on. */
  setBacklinks: (pageId: string, answer: BacklinksAnswer | Response) => void
  /** The pages whose backlinks were asked for, in order. */
  backlinkReads: () => string[]
  /** What the server says the person starred or opened, from now on. */
  setFavorites: (ids: string[]) => void
  setRecent: (items: { pageId: string; openedAt: string }[]) => void
  /** The favorite ids the server holds now. */
  favorites: () => string[]
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
    /** Answers the check before an export; a 204 without it. */
    exportCheck?: () => Response
    /** The pages that link to a page, by its id; none without it. */
    backlinks?: Record<string, BacklinksAnswer>
    /** A page's tags, by its id; none without it. */
    tags?: Record<string, TagRef[]>
    /** The ids of the pages the person starred, in the order they were; none without it. */
    favorites?: string[]
    /** The pages the person opened, newest first; none without it. */
    recent?: { pageId: string; openedAt: string }[]
    /** Whether the person signed in is the administrator; they are without it. */
    isAdmin?: boolean
    /** What `/api/version` answers; version 0.1.0 without an update without it. */
    version?: VersionInfo
  } = {},
): FakeTree {
  let pages = initial.pages ?? []
  let trash = initial.trash ?? []
  let favorites = [...(initial.favorites ?? [])]
  let recent = [...(initial.recent ?? [])]
  let failure: { status: number; code: string } | null = null
  const writes: { method: string; path: string; body: Record<string, unknown> }[] = []
  const searches: string[] = []
  const backlinks = new Map<string, BacklinksAnswer | Response>(
    Object.entries(initial.backlinks ?? {}),
  )
  const backlinkReads: string[] = []

  // Tags: who has which (by id), and every tag's current name, so a rename or a reused name agrees everywhere.
  const tagsByPage = new Map<string, Set<string>>(
    Object.entries(initial.tags ?? {}).map(([pageId, tags]) => [
      pageId,
      new Set(tags.map((tag) => tag.id)),
    ]),
  )
  const tagNames = new Map<string, string>(
    Object.values(initial.tags ?? {})
      .flat()
      .map((tag) => [tag.id, tag.name]),
  )
  let nextTagId = 1

  function tagsOf(pageId: string): TagRef[] {
    return [...(tagsByPage.get(pageId) ?? [])]
      .map((id) => ({ id, name: tagNames.get(id) ?? '' }))
      .sort((a, b) => a.name.localeCompare(b.name))
  }

  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const path = String(input)

    if (method === 'GET') {
      if (path === '/api/auth/state') {
        return jsonResponse(200, {
          setupRequired: false,
          user: {
            id: 'u1',
            email: 'jos@example.com',
            displayName: 'Jos',
            isAdmin: initial.isAdmin ?? true,
          },
          csrfToken: 'token',
        })
      }
      if (path === '/api/workspaces')
        return jsonResponse(200, [{ ...workspace, role: initial.role ?? 'owner' }])
      if (path === '/api/version')
        return jsonResponse(
          200,
          initial.version ?? { version: '0.1.0', development: false, update: null },
        )
      if (path === `/api/workspaces/${workspace.id}/pages`)
        return jsonResponse(200, { treeVersion: 1, pages })
      if (path === `/api/workspaces/${workspace.id}/trash`) return jsonResponse(200, trash)
      if (path === `/api/workspaces/${workspace.id}/favorites`) return jsonResponse(200, favorites)
      if (path === `/api/workspaces/${workspace.id}/recent`) return jsonResponse(200, recent)
      const searchPath = `/api/workspaces/${workspace.id}/search?q=`
      if (path.startsWith(searchPath) && initial.search) {
        const query = decodeURIComponent(path.slice(searchPath.length))
        searches.push(query)
        const answer = await initial.search(query, init?.signal ?? undefined)
        if (init?.signal?.aborted) throw new DOMException('Aborted', 'AbortError')
        return answer instanceof Response ? answer : jsonResponse(200, answer)
      }
      const linked = /^\/api\/pages\/([^/]+)\/backlinks$/.exec(path)
      if (linked) {
        backlinkReads.push(decodeURIComponent(linked[1]!))
        const answer = backlinks.get(decodeURIComponent(linked[1]!)) ?? { items: [], more: false }
        return answer instanceof Response ? answer.clone() : jsonResponse(200, answer)
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
              tags: tagsOf(page.id),
            })
          : jsonResponse(404, { code: 'not_found' })
      }
      if (path === `/api/workspaces/${workspace.id}/export/check`) {
        return initial.exportCheck?.() ?? new Response(null, { status: 204 })
      }
      if (path === `/api/workspaces/${workspace.id}/tags`) {
        const counts = new Map<string, number>()
        for (const page of pages) {
          for (const id of tagsByPage.get(page.id) ?? []) counts.set(id, (counts.get(id) ?? 0) + 1)
        }
        const list: WorkspaceTag[] = [...counts]
          .map(([id, pageCount]) => ({ id, name: tagNames.get(id) ?? '', pageCount }))
          .sort((a, b) => a.name.localeCompare(b.name))
        return jsonResponse(200, list)
      }
      const tagPages = /^\/api\/workspaces\/[^/]+\/tags\/([^/]+)\/pages$/.exec(path)
      if (tagPages) {
        const tagId = decodeURIComponent(tagPages[1]!)
        if (!tagNames.has(tagId)) return jsonResponse(404, { code: 'not_found' })
        const items = pages
          .filter((page) => (tagsByPage.get(page.id) ?? new Set()).has(tagId))
          .map((page) => ({
            id: page.id,
            title: page.title,
            parentTitle: page.parentId
              ? (pages.find((p) => p.id === page.parentId)?.title ?? null)
              : null,
          }))
          .sort((a, b) => a.title.localeCompare(b.title))
        const answer: TagPagesResponse = { tagName: tagNames.get(tagId) ?? '', items, more: false }
        return jsonResponse(200, answer)
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

    const favoriteOf = /^\/api\/pages\/([^/]+)\/favorite$/.exec(path)
    if (favoriteOf) {
      const pageId = decodeURIComponent(favoriteOf[1]!)
      favorites = favorites.filter((id) => id !== pageId)
      if (method === 'PUT') favorites.push(pageId)
      return new Response(null, { status: 204 })
    }
    const visited = /^\/api\/pages\/([^/]+)\/visit$/.exec(path)
    if (visited) {
      const pageId = decodeURIComponent(visited[1]!)
      recent = [
        { pageId, openedAt: new Date().toISOString() },
        ...recent.filter((r) => r.pageId !== pageId),
      ]
      return new Response(null, { status: 204 })
    }

    const pageTags = /^\/api\/pages\/([^/]+)\/tags$/.exec(path)
    if (pageTags) {
      const pageId = decodeURIComponent(pageTags[1]!)
      const set = tagsByPage.get(pageId) ?? new Set<string>()
      for (const id of (body.remove as string[] | undefined) ?? []) set.delete(id)
      for (const name of (body.add as string[] | undefined) ?? []) {
        const existing = [...tagNames].find(([, n]) => n.toLowerCase() === name.toLowerCase())
        const id = existing?.[0] ?? `t${nextTagId++}`
        if (!existing) tagNames.set(id, name)
        set.add(id)
      }
      tagsByPage.set(pageId, set)
      return jsonResponse(200, tagsOf(pageId))
    }

    const renameTag = /^\/api\/workspaces\/[^/]+\/tags\/([^/]+)$/.exec(path)
    if (renameTag && method === 'PATCH') {
      const tagId = decodeURIComponent(renameTag[1]!)
      if (!tagNames.has(tagId)) return jsonResponse(404, { code: 'not_found' })
      tagNames.set(tagId, String(body.name ?? ''))
      return jsonResponse(200, { id: tagId, name: tagNames.get(tagId) })
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
    setBacklinks: (pageId, answer) => {
      backlinks.set(pageId, answer)
    },
    backlinkReads: () => backlinkReads,
    setFavorites: (ids) => {
      favorites = [...ids]
    },
    setRecent: (items) => {
      recent = [...items]
    },
    favorites: () => favorites,
    failNext: (status, code) => {
      failure = { status, code }
    },
  }
}
