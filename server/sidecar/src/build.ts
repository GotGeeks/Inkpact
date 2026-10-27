import * as Y from 'yjs'

/** The field TipTap's collaboration extension keeps the document in. */
export const FIELD = 'default'

/** The client id of the one update that makes a page's first paragraph. Part of the stored format: see web/src/editor/firstParagraph.ts. */
export const FIRST_PARAGRAPH_CLIENT_ID = 1

/**
 * The nodes the editor's schema has and the attributes each may carry, in the names TipTap gives them. A test in `web/`
 * compares these lists with the schema of the real editor, so an extension added there cannot drift away from here.
 * Anything else is refused: a page with a node the clients do not know is a page they refuse to open.
 */
export const NODE_ATTRIBUTES: Readonly<Record<string, readonly string[]>> = {
  paragraph: [],
  heading: ['level'],
  blockquote: [],
  bulletList: [],
  orderedList: ['start', 'type'],
  listItem: [],
  codeBlock: ['language'],
  hardBreak: [],
  horizontalRule: [],
  taskList: [],
  taskItem: ['checked'],
  table: [],
  tableRow: [],
  tableHeader: ['colspan', 'rowspan', 'colwidth', 'align'],
  tableCell: ['colspan', 'rowspan', 'colwidth', 'align'],
  image: ['attachmentId', 'alt'],
  attachment: ['attachmentId', 'fileName', 'size'],
}

export const MARK_ATTRIBUTES: Readonly<Record<string, readonly string[]>> = {
  bold: [],
  italic: [],
  strike: [],
  underline: [],
  code: [],
  link: ['href', 'target', 'rel', 'class', 'title'],
}

/** Nesting deeper than any document made by hand; a limit so a hostile input cannot exhaust the stack. */
const MAX_DEPTH = 64

export class InvalidContentError extends Error {}

type Attribute = string | number | boolean | null | number[]
type Attributes = Record<string, Attribute>

interface JsonNode {
  type: string
  attrs?: Attributes
  content?: JsonNode[]
  text?: string
  marks?: { type: string; attrs?: Attributes }[]
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

function checkAttributes(
  what: string,
  value: unknown,
  allowed: readonly string[],
): Attributes | undefined {
  if (value === undefined) return undefined
  if (!isRecord(value)) throw new InvalidContentError(`${what}: attrs is not an object`)
  for (const [name, attribute] of Object.entries(value)) {
    if (!allowed.includes(name)) throw new InvalidContentError(`${what}: unknown attribute ${name}`)
    const plain = attribute === null || ['string', 'number', 'boolean'].includes(typeof attribute)
    const numbers =
      Array.isArray(attribute) && attribute.every((entry) => typeof entry === 'number')
    if (!plain && !numbers)
      throw new InvalidContentError(`${what}: attribute ${name} has no plain value`)
  }
  return value as Attributes
}

/** Marks as Yjs formatting attributes: the mark's name, holding its attributes (or `{}`), as y-prosemirror writes them. */
function formatting(node: JsonNode): Record<string, Attributes> {
  const result: Record<string, Attributes> = {}
  if (node.marks === undefined) return result
  if (!Array.isArray(node.marks)) throw new InvalidContentError('text: marks is not a list')
  for (const mark of node.marks) {
    if (!isRecord(mark) || typeof mark.type !== 'string' || !(mark.type in MARK_ATTRIBUTES))
      throw new InvalidContentError(`unknown mark ${String(isRecord(mark) ? mark.type : mark)}`)
    const attributes = checkAttributes(`mark ${mark.type}`, mark.attrs, MARK_ATTRIBUTES[mark.type]!)
    const kept: Attributes = {}
    // y-prosemirror leaves out attributes that are null: reading them back gives the schema's default.
    for (const [name, value] of Object.entries(attributes ?? {}))
      if (value !== null) kept[name] = value
    result[mark.type] = kept
  }
  return result
}

function element(node: unknown, depth: number): Y.XmlElement {
  if (depth > MAX_DEPTH) throw new InvalidContentError('content is nested too deeply')
  if (
    !isRecord(node) ||
    typeof node.type !== 'string' ||
    !Object.hasOwn(NODE_ATTRIBUTES, node.type)
  )
    throw new InvalidContentError(`unknown node ${String(isRecord(node) ? node.type : node)}`)

  const json = node as unknown as JsonNode
  const result = new Y.XmlElement(json.type)
  const attributes = checkAttributes(`node ${json.type}`, json.attrs, NODE_ATTRIBUTES[json.type]!)
  for (const [name, value] of Object.entries(attributes ?? {})) {
    if (value !== null) result.setAttribute(name, value as never)
  }

  if (json.content === undefined) return result
  if (!Array.isArray(json.content))
    throw new InvalidContentError(`node ${json.type}: content is not a list`)

  const children: (Y.XmlElement | Y.XmlText)[] = []
  // Adjacent text nodes share one Y.XmlText, whatever their marks, as y-prosemirror does.
  let run: { insert: string; attributes: Record<string, Attributes> }[] = []
  const endRun = () => {
    if (run.length === 0) return
    const text = new Y.XmlText()
    text.applyDelta(run)
    children.push(text)
    run = []
  }
  for (const child of json.content) {
    if (isRecord(child) && child.type === 'text') {
      if (typeof child.text !== 'string' || child.text.length === 0)
        throw new InvalidContentError('text: needs a text that is not empty')
      if (child.content !== undefined || child.attrs !== undefined)
        throw new InvalidContentError('text: has no content or attributes')
      run.push({ insert: child.text, attributes: formatting(child as unknown as JsonNode) })
    } else {
      endRun()
      children.push(element(child, depth + 1))
    }
  }
  endRun()
  result.insert(0, children)
  return result
}

/**
 * The Yjs update for a page whose content is `content`, a ProseMirror document as JSON (`{type: 'doc', content: [...]}`).
 *
 * Every page's document starts from the same fixed update, which makes the first paragraph; clients apply it before they
 * subscribe, so none makes a paragraph of its own. This one does too, then deletes that paragraph and writes the content
 * after it. A client that applies the first-paragraph update again changes nothing: the deletion is in the delete set.
 * An empty document keeps the first paragraph as it is.
 *
 * A document that does not end in a paragraph gets an empty one, which is what the editor would add when it opens the page
 * (TipTap's trailing node). If it were left to the editor, every client that opened the page would add its own.
 */
export function buildDocument(content: unknown): Uint8Array {
  if (!isRecord(content) || content.type !== 'doc')
    throw new InvalidContentError('the content is not a doc')
  const blocks = content.content ?? []
  if (!Array.isArray(blocks)) throw new InvalidContentError('doc: content is not a list')

  const doc = new Y.Doc()
  try {
    const first = new Y.Doc()
    first.clientID = FIRST_PARAGRAPH_CLIENT_ID
    const paragraph = new Y.XmlElement('paragraph')
    paragraph.insert(0, [new Y.XmlText()])
    first.getXmlFragment(FIELD).insert(0, [paragraph])
    const firstUpdate = Y.encodeStateAsUpdate(first)
    first.destroy()

    // Built before anything is applied, so a refusal leaves nothing half done.
    const elements = blocks.map((block) => element(block, 1))
    const last = blocks.at(-1)
    if (elements.length > 0 && !(isRecord(last) && last.type === 'paragraph'))
      elements.push(new Y.XmlElement('paragraph'))
    if (elements.length === 0) return firstUpdate

    Y.applyUpdate(doc, firstUpdate)
    const fragment = doc.getXmlFragment(FIELD)
    doc.transact(() => {
      fragment.delete(0, fragment.length)
      fragment.insert(0, elements)
    })
    return Y.encodeStateAsUpdate(doc)
  } finally {
    doc.destroy()
  }
}
