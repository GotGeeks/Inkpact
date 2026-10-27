import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { setCsrfToken } from '../api'
import { jsonResponse } from '../testing/helpers'
import { forgetAttachmentLimit, uploadAttachment } from './attachmentApi'

beforeEach(() => {
  forgetAttachmentLimit()
  setCsrfToken('token')
})
afterEach(() => vi.unstubAllGlobals())

/** Answers the limits with `limit` (or fails them), and an upload with an attachment. */
function server(limit: number | 'fails') {
  const fetchMock = vi.fn(async (path: string) => {
    if (path === '/api/attachments/limits') {
      return limit === 'fails'
        ? jsonResponse(500, { code: 'server_error' })
        : jsonResponse(200, { maxFileSize: limit })
    }
    return jsonResponse(201, { id: 'a', fileName: 'x', contentType: 'x', size: 1, inline: false })
  })
  vi.stubGlobal('fetch', fetchMock)
  const uploads = () => fetchMock.mock.calls.filter(([path]) => path.includes('/attachments?'))
  const limitAsks = () =>
    fetchMock.mock.calls.filter(([path]) => path === '/api/attachments/limits')
  return { uploads, limitAsks }
}

const sized = (bytes: number, name = 'f.bin') => new File([new Uint8Array(bytes)], name)

describe('uploadAttachment', () => {
  it('sends the file to the page, with its name in the address', async () => {
    const { uploads } = server(1000)

    await uploadAttachment('page-1', sized(10, 'Vakantie & foto?.png'))

    expect(uploads()).toHaveLength(1)
    expect(uploads()[0]![0]).toBe('/api/pages/page-1/attachments?name=Vakantie%20%26%20foto%3F.png')
  })

  it('refuses a file over the limit without sending any of it', async () => {
    const { uploads } = server(1000)

    await expect(uploadAttachment('p', sized(1001))).rejects.toMatchObject({
      status: 413,
      code: 'attachment_too_large',
    })
    expect(uploads()).toHaveLength(0)
    await expect(uploadAttachment('p', sized(1000))).resolves.toMatchObject({ id: 'a' })
  })

  it('refuses an empty file without asking anything', async () => {
    const { uploads, limitAsks } = server(1000)

    await expect(uploadAttachment('p', sized(0))).rejects.toMatchObject({
      code: 'attachment_empty',
    })
    expect(uploads()).toHaveLength(0)
    expect(limitAsks()).toHaveLength(0)
  })

  it('asks for the limit once', async () => {
    const { limitAsks } = server(1000)

    await uploadAttachment('p', sized(1))
    await uploadAttachment('p', sized(2))

    expect(limitAsks()).toHaveLength(1)
  })

  it('leaves the decision to the server when the limit cannot be asked, and asks again next time', async () => {
    const { uploads, limitAsks } = server('fails')

    await uploadAttachment('p', sized(5000))
    await uploadAttachment('p', sized(5000))

    expect(uploads()).toHaveLength(2)
    expect(limitAsks()).toHaveLength(2)
  })
})
