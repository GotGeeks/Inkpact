import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { createAppI18n, type Locale } from '../i18n'
import { useImportMessages } from './useImportMessages'

function messagesIn(locale: Locale) {
  let messages!: ReturnType<typeof useImportMessages>
  mount(
    {
      setup() {
        messages = useImportMessages()
        return () => null
      },
    },
    { global: { plugins: [createAppI18n(locale)] } },
  )
  return messages
}

// Every code the server writes into a report (NoteCodes in the server's ConversionNotes.cs).
const serverCodes = [
  'unsupported_element',
  'removed_element',
  'link_removed',
  'note_link_pending',
  'resource_missing',
  'resource_link_text_lost',
  'external_image',
  'image_not_kept',
  'footnote',
  'definition_list',
  'math',
  'highlight',
  'sub_superscript',
  'mermaid',
  'nesting_flattened',
  'mark_dropped',
  'truncated',
  'encrypted',
  'item_too_large',
  'resource_too_large',
  'resource_empty',
  'notebook_missing',
  'notebook_cycle',
  'notebook_in_trash',
  'parent_failed',
  'conversion_failed',
  'item_failed',
]

describe('useImportMessages', () => {
  it.each(['en', 'nl'] as const)(
    'has a message in %s for every code the server reports',
    (locale) => {
      const { reason } = messagesIn(locale)
      const generic = reason({ code: 'a code from a newer server', count: 1 })

      for (const code of serverCodes) {
        const text = reason({ code, detail: 'svg', count: 1 })
        expect(text, code).not.toBe(generic)
        expect(text, code).not.toContain('import.reasons')
      }
    },
  )

  it('puts the detail in the sentence', () => {
    const { reason } = messagesIn('en')

    expect(reason({ code: 'removed_element', detail: 'svg', count: 1 })).toContain('“svg”')
    expect(reason({ code: 'resource_too_large', detail: 'movie.mp4', count: 1 })).toContain(
      '“movie.mp4”',
    )
  })

  it('says so when a relative link was removed, and which address otherwise', () => {
    const { reason } = messagesIn('en')

    expect(reason({ code: 'link_removed', detail: 'relative', count: 1 })).toContain('relative')
    expect(reason({ code: 'link_removed', detail: 'javascript', count: 1 })).toContain(
      '(javascript)',
    )
  })

  it('counts a reason that came up more than once, in the number format of the language', () => {
    expect(messagesIn('en').reason({ code: 'math', count: 1200 })).toContain('(1,200 times)')
    expect(messagesIn('nl').reason({ code: 'math', count: 1200 })).toContain('(1.200 keer)')
    expect(messagesIn('en').reason({ code: 'math', count: 1 })).not.toContain('times')
  })

  it('never shows a code it does not know', () => {
    const { reason } = messagesIn('en')

    expect(reason({ code: 'something_new', detail: 'x', count: 1 })).toBe(
      'Something did not come across as it was.',
    )
  })

  it('names every status and kind, and treats an unknown one as the plain case', () => {
    const { status, kind } = messagesIn('en')

    expect(['imperfect', 'conflict', 'skipped', 'failed'].map(status)).toEqual([
      'Imported, with changes',
      'Joplin conflict',
      'Skipped',
      'Failed',
    ])
    expect(['notebook', 'note', 'tag'].map(kind)).toEqual(['Notebook', 'Note', 'Tag'])
    expect(status('new')).toBe('Imported, with changes')
    expect(kind('new')).toBe('Note')
  })
})
