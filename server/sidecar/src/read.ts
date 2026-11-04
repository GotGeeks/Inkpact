import * as Y from 'yjs'
import { FIELD, MARK_ATTRIBUTES, NODE_ATTRIBUTES } from './build.ts'

/**
 * Nesting past which a page is not followed (the editor makes nothing like it; `build` refuses more than 64). The walk
 * keeps its own stack, so this is not about the call stack: it bounds what one hostile page can cost.
 */
const MAX_DEPTH = 200

/** The order marks are written in, outermost first, so that the same text always reads back as the same JSON. */
const MARK_ORDER = ['link', 'underline', 'bold', 'italic', 'strike', 'code'] as const

type Attribute = string | number | boolean | number[]
type Json = { [key: string]: unknown }

export interface ReadResult {
  /** The page as ProseMirror JSON, the inverse of `buildDocument`. */
  document: Json
  /** How many nodes, marks, attributes and embeds the editor's schema does not have, left out. */
  skipped: number
}

const isAttribute = (value: unknown): value is Attribute =>
  ['string', 'number', 'boolean'].includes(typeof value) ||
  (Array.isArray(value) && value.every((entry) => typeof entry === 'number'))

/** Only the attributes the schema allows, with plain values; `skipped` counts the rest. Null and missing ones are left out. */
function attributesOf(
  raw: Record<string, unknown>,
  allowed: readonly string[],
  skip: () => void,
): Record<string, Attribute> | undefined {
  const kept: Record<string, Attribute> = {}
  for (const [name, value] of Object.entries(raw)) {
    if (value === null || value === undefined) continue
    if (allowed.includes(name) && isAttribute(value)) kept[name] = value
    else skip()
  }
  return Object.keys(kept).length > 0 ? kept : undefined
}

/** The text nodes of a Y.XmlText, with their marks. */
function textNodes(text: Y.XmlText, skip: () => void): Json[] {
  const nodes: Json[] = []
  for (const op of text.toDelta() as { insert: unknown; attributes?: Record<string, unknown> }[]) {
    if (typeof op.insert !== 'string') {
      // An embed: the schema has none.
      skip()
      continue
    }
    if (op.insert.length === 0) continue

    const marks: Json[] = []
    const named = op.attributes ?? {}
    for (const name of MARK_ORDER) {
      if (!Object.hasOwn(named, name) || named[name] === null || named[name] === undefined) continue
      const value = named[name]
      const attrs =
        typeof value === 'object' && !Array.isArray(value)
          ? attributesOf(value as Record<string, unknown>, MARK_ATTRIBUTES[name]!, skip)
          : undefined
      marks.push(attrs ? { type: name, attrs } : { type: name })
    }
    // A mark the schema does not have.
    for (const name of Object.keys(named)) if (!Object.hasOwn(MARK_ATTRIBUTES, name)) skip()

    nodes.push(
      marks.length > 0
        ? { type: 'text', text: op.insert, marks }
        : { type: 'text', text: op.insert },
    )
  }
  return nodes
}

/**
 * A page's document as ProseMirror JSON: the `default` fragment, element by element, in the names and attributes of the
 * editor's schema (`NODE_ATTRIBUTES`, `MARK_ATTRIBUTES`). It is the inverse of `buildDocument`: what that was given comes back,
 * except attributes that were null, which are left out as they are when a page is built. Whatever the schema does not have is
 * left out and counted, so a caller (and a test) can tell. A page without the fragment is an empty document.
 */
export function readDocument(doc: Y.Doc): ReadResult {
  let skipped = 0
  const skip = () => {
    skipped++
  }
  const root: Json = { type: 'doc' }
  if (!doc.share.has(FIELD)) return { document: root, skipped }

  // An explicit stack, as text.ts has: a deeply nested page must not be able to exhaust the call stack.
  interface Frame {
    node: Json
    children: (Y.XmlElement | Y.XmlText | Y.XmlHook | unknown)[]
    next: number
    content: Json[]
  }
  const stack: Frame[] = [
    { node: root, children: doc.getXmlFragment(FIELD).toArray(), next: 0, content: [] },
  ]
  while (stack.length > 0) {
    const frame = stack[stack.length - 1]!
    if (frame.next === frame.children.length) {
      stack.pop()
      if (frame.content.length > 0) frame.node.content = frame.content
      continue
    }

    const child = frame.children[frame.next++]
    if (child instanceof Y.XmlText) {
      frame.content.push(...textNodes(child, skip))
    } else if (child instanceof Y.XmlElement) {
      if (!Object.hasOwn(NODE_ATTRIBUTES, child.nodeName) || stack.length > MAX_DEPTH) {
        skip()
        continue
      }
      const node: Json = { type: child.nodeName }
      const attrs = attributesOf(
        child.getAttributes() as Record<string, unknown>,
        NODE_ATTRIBUTES[child.nodeName]!,
        skip,
      )
      if (attrs) node.attrs = attrs
      frame.content.push(node)
      stack.push({ node, children: child.toArray(), next: 0, content: [] })
    } else {
      skip()
    }
  }
  return { document: root, skipped }
}
