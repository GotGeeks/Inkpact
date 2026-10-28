import { Buffer } from 'node:buffer'
import { createHash, randomBytes } from 'node:crypto'

/** A PNG of one pixel. */
export const PIXEL_PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
  'base64',
)
export const MANUAL_PDF = Buffer.from('%PDF-1.4\n% a test file\n')

/** One file of a tar: a 512-byte header with its checksum, then the data padded to a whole block. */
function tarEntry(name: string, data: Buffer): Buffer {
  const header = Buffer.alloc(512)
  const put = (value: string, offset: number, length: number) =>
    header.write(value, offset, length, 'ascii')
  put(name, 0, 100)
  put('0000644\0', 100, 8)
  put('0000000\0', 108, 8)
  put('0000000\0', 116, 8)
  put(`${data.length.toString(8).padStart(11, '0')}\0`, 124, 12)
  put('00000000000\0', 136, 12)
  header.fill(0x20, 148, 156) // the checksum is counted with its own field as spaces
  put('0', 156, 1)
  put('ustar\0', 257, 6)
  put('00', 263, 2)
  const checksum = header.reduce((sum, byte) => sum + byte, 0)
  put(`${checksum.toString(8).padStart(6, '0')}\0 `, 148, 8)
  return Buffer.concat([header, data, Buffer.alloc((512 - (data.length % 512)) % 512)])
}

/** Node has no tar writer; this is all a JEX needs: the files, and two empty blocks at the end. */
function tar(files: { name: string; data: Buffer }[]): Buffer {
  return Buffer.concat([...files.map((f) => tarEntry(f.name, f.data)), Buffer.alloc(1024)])
}

export interface GeneratedJex {
  file: Buffer
  /** The titles of what is in it, which are made unique so that the tests of several devices can share one workspace. */
  titles: {
    notebook: string
    subNotebook: string
    pictures: string
    todo: string
    clipping: string
    secret: string
    tag: string
  }
}

/**
 * A small export in the shape Joplin writes it, with fresh ids and titles every time (the import's skip rule is by id,
 * so a second export with the same ids would be "already imported"; `ids` makes that a choice). It holds two notebooks
 * (one inside the other), a note with a picture and a PDF, a to-do with a due date, an HTML note with an inline SVG (which
 * does not come across), an encrypted note (which is skipped), and a tag.
 */
export function generateJex(label: string, ids = randomBytes(8).toString('hex')): GeneratedJex {
  const id = (name: string) =>
    createHash('sha256').update(`${ids}:${name}`).digest('hex').slice(0, 32)
  const titles = {
    notebook: `${label} Notebook`,
    subNotebook: `${label} Sub-notebook`,
    pictures: `${label} Pictures`,
    todo: `${label} Pay rent`,
    clipping: `${label} Clipping`,
    secret: `${label} Secret`,
    tag: `${label}-tag`,
  }

  const files: { name: string; data: Buffer }[] = []
  const item = (name: string, text: string) =>
    files.push({ name: `${id(name)}.md`, data: Buffer.from(text, 'utf8') })
  const fields = (pairs: [string, string][], type: number) =>
    [...pairs, ['type_', String(type)]].map(([key, value]) => `${key}: ${value}`).join('\n')
  const noteFields = (name: string, parent: string, extra: [string, string][] = []) =>
    [
      ['id', id(name)],
      ['parent_id', id(parent)],
      ['user_created_time', '2026-01-02T03:04:05.000Z'],
      ['user_updated_time', '2026-02-03T04:05:06.000Z'],
      ['is_conflict', '0'],
      ['is_todo', '0'],
      ['todo_due', '0'],
      ['todo_completed', '0'],
      ['markup_language', '1'],
      ['encryption_applied', '0'],
      ['deleted_time', '0'],
      ...extra,
    ] as [string, string][]
  const override = (pairs: [string, string][]) => [...new Map(pairs).entries()]

  item(
    'notebook',
    `${titles.notebook}\n\n${fields(
      [
        ['id', id('notebook')],
        ['parent_id', ''],
      ],
      2,
    )}`,
  )
  item(
    'sub',
    `${titles.subNotebook}\n\n${fields(
      [
        ['id', id('sub')],
        ['parent_id', id('notebook')],
      ],
      2,
    )}`,
  )

  const body = `Rent and **bold** words.\n\n![a pixel](:/${id('pixel')})\n\n[manual.pdf](:/${id('manual')})`
  item(
    'pictures',
    `${titles.pictures}\n\n${body}\n\n${fields(noteFields('pictures', 'notebook'), 1)}`,
  )

  const due = String(Date.UTC(2027, 0, 15, 9, 0, 0))
  item(
    'todo',
    `${titles.todo}\n\n- [ ] Pay the rent\n\n${fields(
      override(
        noteFields('todo', 'sub', [
          ['is_todo', '1'],
          ['todo_due', due],
        ]),
      ),
      1,
    )}`,
  )
  item(
    'clipping',
    `${titles.clipping}\n\n<p>Hello <b>world</b></p><svg width="10"><circle r="4"/></svg>\n\n${fields(override(noteFields('clipping', 'sub', [['markup_language', '2']])), 1)}`,
  )
  item(
    'secret',
    `${titles.secret}\n\nJED01000...\n\n${fields(override(noteFields('secret', 'sub', [['encryption_applied', '1']])), 1)}`,
  )

  item('tag', `${titles.tag}\n\n${fields([['id', id('tag')]], 5)}`)
  item(
    'link',
    `\n\n${fields(
      [
        ['id', id('link')],
        ['note_id', id('pictures')],
        ['tag_id', id('tag')],
      ],
      6,
    )}`,
  )

  item(
    'pixel',
    `pixel.png\n\n${fields(
      [
        ['id', id('pixel')],
        ['mime', 'image/png'],
        ['filename', ''],
        ['file_extension', 'png'],
        ['size', String(PIXEL_PNG.length)],
      ],
      4,
    )}`,
  )
  files.push({ name: `resources/${id('pixel')}.png`, data: PIXEL_PNG })
  item(
    'manual',
    `manual.pdf\n\n${fields(
      [
        ['id', id('manual')],
        ['mime', 'application/pdf'],
        ['filename', ''],
        ['file_extension', 'pdf'],
        ['size', String(MANUAL_PDF.length)],
      ],
      4,
    )}`,
  )
  files.push({ name: `resources/${id('manual')}.pdf`, data: MANUAL_PDF })

  return { file: tar(files), titles }
}
