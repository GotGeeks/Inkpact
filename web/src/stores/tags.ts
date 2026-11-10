import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, api } from '../api'
import type { TagPagesResponse, TagRef, WorkspaceTag } from '../tags/types'
import { useTreeStore } from './tree'

/**
 * The workspace's tags (run B.2): read when the add-tag field or the "Tags" view first needs them, and again after any
 * change, so a tag made or renamed in one place is offered everywhere else without a reload.
 */
export const useTagsStore = defineStore('tags', () => {
  const items = ref<WorkspaceTag[]>([])
  const loaded = ref(false)
  const loadError = ref<string | null>(null)
  let loadGeneration = 0

  /** The workspace's id, asking the tree store to find it first when nothing has, so a view opened on its own still works. */
  async function workspaceId(): Promise<string | undefined> {
    const tree = useTreeStore()
    if (!tree.workspace) await tree.init()
    return tree.workspace?.id
  }

  async function load() {
    const mine = ++loadGeneration
    loadError.value = null
    try {
      const id = await workspaceId()
      if (!id) throw new Error('No workspace is available')
      const answer = await api<WorkspaceTag[]>('GET', `/api/workspaces/${id}/tags`)
      if (mine === loadGeneration && useTreeStore().workspace?.id === id) {
        items.value = answer
        loaded.value = true
        loadError.value = null
      }
    } catch (error) {
      if (mine === loadGeneration) {
        loadError.value = error instanceof ApiError ? error.code : 'unknown'
      }
      throw error
    }
  }

  /** Reads the list if it has not been read yet; safe to call every time the add field opens. */
  async function ensureLoaded() {
    if (!loaded.value) await load()
  }

  async function pagesOf(tagId: string): Promise<TagPagesResponse> {
    const id = await workspaceId()
    if (!id) throw new Error('no workspace')
    return api<TagPagesResponse>('GET', `/api/workspaces/${id}/tags/${tagId}/pages`)
  }

  /** Renames a tag and refreshes the list, so its new name and spelling show everywhere at once. */
  async function rename(tagId: string, name: string): Promise<TagRef> {
    const id = await workspaceId()
    if (!id) throw new Error('no workspace')
    const result = await api<TagRef>('PATCH', `/api/workspaces/${id}/tags/${tagId}`, {
      name,
    })
    await load().catch(() => undefined)
    return result
  }

  function reset() {
    loadGeneration++
    items.value = []
    loaded.value = false
    loadError.value = null
  }

  return { items, loaded, load, loadError, ensureLoaded, pagesOf, rename, reset }
})
