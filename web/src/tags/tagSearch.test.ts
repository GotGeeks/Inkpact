import { describe, expect, it } from 'vitest'
import { findTags, hasExactMatch } from './tagSearch'
import type { WorkspaceTag } from './types'

const tags: WorkspaceTag[] = [
  { id: 't1', name: 'Work', pageCount: 3 },
  { id: 't2', name: 'Home', pageCount: 1 },
  { id: 't3', name: 'Workout', pageCount: 2 },
  { id: 't4', name: 'café', pageCount: 1 },
]

describe('findTags', () => {
  it('finds names that contain what was typed, case and accents ignored', () => {
    expect(findTags(tags, 'wo', new Set()).map((t) => t.name)).toEqual(['Work', 'Workout'])
    expect(findTags(tags, 'CAFE', new Set()).map((t) => t.name)).toEqual(['café'])
  })

  it('puts names that start with the query before those that only contain it', () => {
    expect(findTags(tags, 'work', new Set()).map((t) => t.name)).toEqual(['Work', 'Workout'])
  })

  it('leaves out excluded ids', () => {
    expect(findTags(tags, '', new Set(['t1'])).map((t) => t.id)).not.toContain('t1')
  })

  it('gives the first tags with nothing typed, up to the limit', () => {
    expect(findTags(tags, '', new Set(), 2)).toHaveLength(2)
  })
})

describe('hasExactMatch', () => {
  it('is true only for a name that matches exactly, case and accents folded', () => {
    expect(hasExactMatch(tags, 'work')).toBe(true)
    expect(hasExactMatch(tags, 'CAFE')).toBe(true)
    expect(hasExactMatch(tags, 'wo')).toBe(false)
    expect(hasExactMatch(tags, '')).toBe(false)
  })
})
