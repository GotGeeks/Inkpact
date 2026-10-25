import { describe, expect, it } from 'vitest'
import en from './locales/en.json'
import nl from './locales/nl.json'

function entries(obj: Record<string, unknown>, prefix = ''): [string, unknown][] {
  return Object.entries(obj).flatMap(([key, value]) =>
    typeof value === 'object' && value !== null
      ? entries(value as Record<string, unknown>, `${prefix}${key}.`)
      : [[`${prefix}${key}`, value] as [string, unknown]],
  )
}

describe('locales', () => {
  it('has the same keys in English and Dutch', () => {
    const keys = (messages: Record<string, unknown>) =>
      entries(messages)
        .map(([key]) => key)
        .sort()
    expect(keys(nl)).toEqual(keys(en))
  })

  it('has no empty messages', () => {
    for (const messages of [en, nl]) {
      for (const [key, value] of entries(messages)) {
        expect(value, key).not.toBe('')
      }
    }
  })
})
