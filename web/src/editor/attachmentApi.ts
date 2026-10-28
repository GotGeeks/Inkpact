import { ApiError, uploadFile } from '../api'
import { cachedLimit } from '../limits'
import type { UploadedAttachment } from './attachmentUpload'

const limit = cachedLimit('/api/attachments/limits')
const maxFileSize = limit.get

/** For the tests: ask the server again. */
export const forgetAttachmentLimit = limit.forget

/**
 * Uploads a file to a page. A file over the limit is refused here, before any of it is sent: the server refuses it
 * too, but a large body it does not read often reaches the browser as a reset connection instead of its answer.
 */
export async function uploadAttachment(pageId: string, file: File): Promise<UploadedAttachment> {
  if (file.size === 0) throw new ApiError(400, 'attachment_empty')
  const max = await maxFileSize()
  if (max !== null && file.size > max) throw new ApiError(413, 'attachment_too_large')
  const path = `/api/pages/${encodeURIComponent(pageId)}/attachments?name=${encodeURIComponent(file.name)}`
  return uploadFile<UploadedAttachment>(path, file)
}
