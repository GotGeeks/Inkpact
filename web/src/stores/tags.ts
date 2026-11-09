import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '../api'
import type { TagPagesResponse, TagRef, WorkspaceTag } from '../tags/types'
import { useTreeStore } from './tree'

/**
 * The workspace's tags (run B.2): read when the add-tag field or the "Tags" view first needs them, and again after any
 * change, so a tag made or renamed in one place is offered everywhere else without a reload.
 */
export const useTagsStore = defineStore('tags', () => {
  const items = ref<WorkspaceTag[]>([])
  const loaded = ref(false)

  /** The workspace's id, asking the tree store to find it first when nothing has, so a view opened on its own still works. */
  async function workspaceId(): Promise<string | undefined> {
    const tree = useTreeStore()
    if (!tree.workspace) await tree.init().catch(() => undefined)
    return tree.workspace?.id
  }

  async function load() {
    const id = await workspaceId()
    if (!id) return
    items.value = await api<WorkspaceTag[]>('GET', `/api/workspaces/${id}/tags`)
    loaded.value = true
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
    items.value = []
    loaded.value = false
  }

  return { items, loaded, load, ensureLoaded, pagesOf, rename, reset }
})
