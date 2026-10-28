import { describe, expect, it } from 'vitest'
import { formatFileSize } from './fileSize'

describe('formatFileSize', () => {
  it('formats a size in the language of the page', () => {
    expect(formatFileSize(0, 'en')).toBe('0 bytes')
    expect(formatFileSize(1, 'en')).toBe('1 byte')
    expect(formatFileSize(999, 'en')).toBe('999 bytes')
    expect(formatFileSize(1500, 'en')).toBe('1.5 kB')
    expect(formatFileSize(1500, 'nl')).toBe('1,5 kB')
    expect(formatFileSize(52_428_800, 'en')).toBe('52 MB')
    expect(formatFileSize(3_400_000_000, 'en')).toBe('3.4 GB')
  })
})
