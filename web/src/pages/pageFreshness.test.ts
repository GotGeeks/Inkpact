import { describe, expect, it } from 'vitest'
import type { PageDetail, PageNode } from '../tree/types'
import { shownPageIsCurrent } from './pageFreshness'

const node = (over: Partial<PageNode> = {}): PageNode => ({
  id: 'a',
  parentId: null,
  title: 'Notes',
  sortKey: 'a0',
  version: 3,
  updatedAt: '2026-11-12T10:00:00Z',
  ...over,
})
const detail = (over: Partial<PageDetail> = {}): PageDetail => ({
  page: node(),
  inTrash: false,
  workspaceId: 'w',
  tags: [],
  ...over,
})

describe('shownPageIsCurrent', () => {
  it('is current when the tree has the page at the version that was fetched', () => {
    expect(shownPageIsCurrent(detail(), node())).toBe(true)
  })

  it('is not current once the page itself changed (a rename, a move)', () => {
    expect(shownPageIsCurrent(detail(), node({ version: 4, title: 'Renamed' }))).toBe(false)
  })

  it('is not current when the tree no longer has the page (the trash, or below one)', () => {
    expect(shownPageIsCurrent(detail(), undefined)).toBe(false)
  })

  it('is never current for a page shown as in the trash, which a restore elsewhere may bring back', () => {
    expect(shownPageIsCurrent(detail({ inTrash: true }), node())).toBe(false)
  })

  it('is not current before anything was fetched, or for another page', () => {
    expect(shownPageIsCurrent(null, node())).toBe(false)
    expect(shownPageIsCurrent(detail(), node({ id: 'b' }))).toBe(false)
  })
})
