import { describe, expect, it } from 'vitest'
import { formatAgo } from './relativeTime'

const now = new Date('2026-11-09T12:00:00Z')
const ago = (seconds: number) => new Date(now.getTime() - seconds * 1000).toISOString()

describe('formatAgo', () => {
  it('says minutes, hours and days in English', () => {
    expect(formatAgo(ago(5 * 60), 'en', now)).toBe('5 minutes ago')
    expect(formatAgo(ago(3 * 3600), 'en', now)).toBe('3 hours ago')
    expect(formatAgo(ago(24 * 3600), 'en', now)).toBe('yesterday')
    expect(formatAgo(ago(3 * 24 * 3600), 'en', now)).toBe('3 days ago')
  })

  it('says the same in Dutch', () => {
    expect(formatAgo(ago(5 * 60), 'nl', now)).toBe('5 minuten geleden')
    expect(formatAgo(ago(24 * 3600), 'nl', now)).toBe('gisteren')
  })

  it('treats under a minute, and a clock slightly ahead, as this minute', () => {
    expect(formatAgo(ago(20), 'en', now)).toBe('this minute')
    expect(formatAgo(ago(-30), 'en', now)).toBe('this minute')
  })
})
