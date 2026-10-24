import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Collaboration from '@tiptap/extension-collaboration'
import * as Y from 'yjs'
import { connectRelay } from './sync'
import './style.css'

const pageId = new URLSearchParams(location.search).get('page') ?? 'demo'
const doc = new Y.Doc()

new Editor({
  element: document.querySelector<HTMLElement>('#editor')!,
  extensions: [
    // Yjs brings its own undo manager, so the ProseMirror one must be off.
    StarterKit.configure({ undoRedo: false }),
    Collaboration.configure({ document: doc }),
  ],
})

const pageLabel = document.querySelector<HTMLElement>('#page')!
const statusLabel = document.querySelector<HTMLElement>('#status')!
pageLabel.textContent = `page: ${pageId}`

const wsUrl = new URL(`/ws?page=${encodeURIComponent(pageId)}`, location.href)
wsUrl.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'

connectRelay(doc, wsUrl.href, (status) => {
  statusLabel.textContent = status
  statusLabel.dataset.state = status
})
