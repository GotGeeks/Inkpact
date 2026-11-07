import { describe, expect, it } from 'vitest'
import type { PageNode } from '../tree/types'
import { findPages, fold, sharedTitles } from './pageSearch'

const page = (id: string, title: string, parentId: string | null = null): PageNode => ({
  id,
  parentId,
  title,
  sortKey: 'a0',
  version: 1,
  updatedAt: '',
})

const pages = [
  page('work', 'Work'),
  page('plan', 'Project plan', 'work'),
  page('planb', 'Plan B', 'work'),
  page('cafe', 'Café ideas'),
  page('notes', 'Notes'),
  page('replan', 'Replanning'),
  page('plan2', 'Project plan', 'notes'),
]

const ids = (query: string, current: string | null = null, limit?: number) =>
  findPages(pages, query, current, limit).map((choice) => choice.id)

describe('fold', () => {
  it('ignores case and accents', () => {
    expect(fold('CAFÉ Ünïcode')).toBe('cafe unicode')
  })
})

describe('findPages', () => {
  it('finds the pages whose title contains the text, titles that start with it first', () => {
    // "Plan B" starts with it; the others contain it, in the order of the list.
    expect(ids('plan')).toEqual(['planb', 'plan', 'replan', 'plan2'])
  })

  it('ignores case and accents, in what is typed and in the title', () => {
    expect(ids('CAFE')).toEqual(['cafe'])
    expect(ids('café')).toEqual(['cafe'])
  })

  it('leaves out the page the link is made on', () => {
    expect(ids('plan', 'plan')).toEqual(['planb', 'replan', 'plan2'])
  })

  it('lists the first pages, in the order given, when nothing is typed or only spaces', () => {
    expect(ids('')).toEqual(['work', 'plan', 'planb', 'cafe', 'notes', 'replan', 'plan2'])
    expect(ids('   ', null, 3)).toEqual(['work', 'plan', 'planb'])
  })

  it('lists at most the limit, 8 by default', () => {
    const many = Array.from({ length: 20 }, (_, i) => page(`p${i}`, `Page ${i}`))
    expect(findPages(many, 'page', null)).toHaveLength(8)
    expect(findPages(many, 'page', null, 3)).toHaveLength(3)
  })

  it('finds nothing for text that is in no title, and finds a page without a title by nothing typed', () => {
    expect(ids('zzz')).toEqual([])
    expect(findPages([page('x', '')], '', null).map((c) => c.id)).toEqual(['x'])
  })

  it('names the page each is in', () => {
    const found = findPages(pages, 'project', null)
    expect(found.map((choice) => [choice.id, choice.parentTitle])).toEqual([
      ['plan', 'Work'],
      ['plan2', 'Notes'],
    ])
    expect(findPages(pages, 'work', null)[0]!.parentTitle).toBeNull()
  })
})

describe('sharedTitles', () => {
  it('gives the pages whose title another in the list has too, ignoring case and accents', () => {
    const found = [
      { id: 'a', title: 'Plan', parentTitle: 'Work' },
      { id: 'b', title: 'PLAN', parentTitle: 'Home' },
      { id: 'c', title: 'Other', parentTitle: null },
    ]

    expect([...sharedTitles(found)].sort()).toEqual(['a', 'b'])
  })
})
