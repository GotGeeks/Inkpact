import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { ApiError, api } from '../api'
import { ancestorIds } from '../tree/treeModel'
import type {
  PageChange,
  PageDetail,
  PageNode,
  Position,
  TreeResponse,
  WorkspaceSummary,
} from '../tree/types'
import { useNotificationsStore } from './notifications'

const expandedKey = (workspaceId: string) => `inkpact.tree.expanded.${workspaceId}`

function readExpanded(workspaceId: string): Set<string> {
  try {
    const stored = JSON.parse(localStorage.getItem(expandedKey(workspaceId)) ?? '[]') as unknown
    return new Set(
      Array.isArray(stored) ? stored.filter((id): id is string => typeof id === 'string') : [],
    )
  } catch {
    // Storage can be blocked or hold something else: start collapsed.
    return new Set()
  }
}

/**
 * The page tree of the workspace the person is in. Every change goes to the server and the tree is read again
 * afterwards, so what is on screen is always what the server decided, also when two people (or two tabs) change
 * things at once. Errors are posted as notices, never thrown into the components.
 */
export const useTreeStore = defineStore('tree', () => {
  const workspace = ref<WorkspaceSummary | null>(null)
  const pages = ref<PageNode[]>([])
  const trash = ref<PageNode[]>([])
  const treeVersion = ref(0)
  const loaded = ref(false)
  const expanded = ref<Set<string>>(new Set())
  const notifications = useNotificationsStore()

  const canEdit = computed(
    () => workspace.value?.role === 'owner' || workspace.value?.role === 'editor',
  )
  const byId = computed(() => new Map(pages.value.map((page) => [page.id, page])))

  /** Finds the workspace and reads its tree. Safe to call again; only the first call does anything. */
  async function init() {
    if (loaded.value) return
    const workspaces = await api<WorkspaceSummary[]>('GET', '/api/workspaces')
    // Several workspaces arrive with sharing, in phase 2. Until then a person has one.
    workspace.value = workspaces[0] ?? null
    if (!workspace.value) return
    expanded.value = readExpanded(workspace.value.id)
    await load()
    loaded.value = true
  }

  async function load() {
    if (!workspace.value) return
    const id = workspace.value.id
    const [tree, trashed] = await Promise.all([
      api<TreeResponse>('GET', `/api/workspaces/${id}/pages`),
      api<PageNode[]>('GET', `/api/workspaces/${id}/trash`),
    ])
    pages.value = tree.pages
    treeVersion.value = tree.treeVersion
    trash.value = trashed
  }

  function persistExpanded() {
    if (!workspace.value) return
    try {
      localStorage.setItem(expandedKey(workspace.value.id), JSON.stringify([...expanded.value]))
    } catch {
      // Not remembered, which is fine.
    }
  }

  function setExpanded(id: string, open: boolean) {
    const next = new Set(expanded.value)
    if (open) next.add(id)
    else next.delete(id)
    expanded.value = next
    persistExpanded()
  }

  /** Opens every branch above a page, so that it is on screen. */
  function reveal(id: string) {
    const above = ancestorIds(pages.value, id).filter((parent) => !expanded.value.has(parent))
    if (above.length === 0) return
    expanded.value = new Set([...expanded.value, ...above])
    persistExpanded()
  }

  /** Runs a change. Returns what the server answered, or null after telling the person why it did not work. */
  async function change(call: () => Promise<PageChange>): Promise<PageChange | null> {
    try {
      const result = await call()
      await load()
      return result
    } catch (error) {
      const code = error instanceof ApiError ? error.code : 'unknown'
      notifications.post({
        type: 'error',
        code,
        retryAfterSeconds: error instanceof ApiError ? error.retryAfterSeconds : undefined,
      })
      // Whatever went wrong, the screen may no longer match the server: read it again.
      await load().catch(() => undefined)
      return null
    }
  }

  function positionBody(position: Position) {
    return {
      parentId: position.parentId,
      afterId: position.afterId ?? null,
      beforeId: position.beforeId ?? null,
    }
  }

  async function create(title: string, position: Position = { parentId: null }) {
    const workspaceId = workspace.value?.id
    if (!workspaceId) return null
    const result = await change(() =>
      api<PageChange>('POST', `/api/workspaces/${workspaceId}/pages`, {
        title,
        ...positionBody(position),
      }),
    )
    if (result && position.parentId) setExpanded(position.parentId, true)
    return result
  }

  async function rename(id: string, title: string) {
    return change(() =>
      api<PageChange>('PATCH', `/api/pages/${id}`, {
        title,
        expectedVersion: byId.value.get(id)?.version,
      }),
    )
  }

  async function move(id: string, position: Position) {
    const result = await change(() =>
      api<PageChange>('POST', `/api/pages/${id}/move`, {
        ...positionBody(position),
        expectedVersion: byId.value.get(id)?.version,
      }),
    )
    if (result && position.parentId) setExpanded(position.parentId, true)
    return result
  }

  /** Puts a page in the trash and tells the person, with a way to take it back. */
  async function trashPage(id: string) {
    const title = byId.value.get(id)?.title ?? ''
    const result = await change(() =>
      api<PageChange>('POST', `/api/pages/${id}/trash`, {
        expectedVersion: byId.value.get(id)?.version,
      }),
    )
    if (result) notifications.post({ type: 'trashed', pageId: id, title })
    return result
  }

  async function restore(id: string) {
    const title =
      trash.value.find((page) => page.id === id)?.title ?? byId.value.get(id)?.title ?? ''
    const result = await change(() => api<PageChange>('POST', `/api/pages/${id}/restore`, {}))
    if (result)
      notifications.post({
        type: 'restored',
        pageId: id,
        title,
        toTopLevel: result.restoredTo === 'root',
      })
    return result
  }

  /** One page and whether it is in the trash. Null when it does not exist or is not the person's to see. */
  async function fetchPage(id: string): Promise<PageDetail | null> {
    try {
      return await api<PageDetail>('GET', `/api/pages/${id}`)
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null
      throw error
    }
  }

  /** Forgets everything, when the person leaves, so the next one starts clean. */
  function reset() {
    workspace.value = null
    pages.value = []
    trash.value = []
    treeVersion.value = 0
    loaded.value = false
    expanded.value = new Set()
  }

  return {
    workspace,
    pages,
    trash,
    treeVersion,
    loaded,
    expanded,
    canEdit,
    byId,
    init,
    load,
    reset,
    setExpanded,
    reveal,
    create,
    rename,
    move,
    trashPage,
    restore,
    fetchPage,
  }
})
