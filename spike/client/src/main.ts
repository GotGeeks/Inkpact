import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Collaboration from '@tiptap/extension-collaboration'
import { SyncClient } from './syncClient'
import { renderPresence, type PresenceUser } from './presence'
import './style.css'

// ?pages=a,b opens several pages over one socket; ?page=a opens one; ?user= sets the name.
const params = new URLSearchParams(location.search)
const pageIds = (params.get('pages') ?? params.get('page') ?? 'demo')
  .split(',')
  .map((id) => id.trim())
  .filter(Boolean)

const user = currentUser(params.get('user'))
const statusLabel = document.querySelector<HTMLElement>('#status')!
document.querySelector<HTMLElement>('#user')!.textContent = `user: ${user.name}`

const wsUrl = new URL('/ws', location.href)
wsUrl.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'

const client = new SyncClient(wsUrl.href, (status) => {
  statusLabel.textContent = status
  statusLabel.dataset.state = status
})

const container = document.querySelector<HTMLElement>('#pages')!
for (const pageId of pageIds) {
  const section = document.createElement('section')
  const heading = document.createElement('h2')
  heading.textContent = pageId
  const presence = document.createElement('ul')
  presence.className = 'presence'
  const editor = document.createElement('div')
  section.append(heading, presence, editor)
  container.append(section)

  const page = client.subscribe(pageId)
  page.awareness.setLocalStateField('user', user)
  renderPresence(presence, page.awareness)

  new Editor({
    element: editor,
    extensions: [
      // Yjs brings its own undo manager, so the ProseMirror one must be off.
      StarterKit.configure({ undoRedo: false }),
      Collaboration.configure({ document: page.doc }),
    ],
  })
}

/** The same name in every tab of this browser, so presence can group them. */
function currentUser(fromQuery: string | null): PresenceUser {
  let name = fromQuery
  if (!name) {
    try {
      name = localStorage.getItem('spike-user')
      if (!name) {
        name = `user-${Math.random().toString(36).slice(2, 6)}`
        localStorage.setItem('spike-user', name)
      }
    } catch {
      name = 'anonymous'
    }
  }
  let hash = 0
  for (const char of name) hash = (hash * 31 + char.charCodeAt(0)) >>> 0
  return { name, color: `hsl(${hash % 360} 70% 45%)` }
}
