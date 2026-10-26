import { Editor } from '@tiptap/core'
import * as Y from 'yjs'
import { afterEach, describe, expect, it } from 'vitest'
import { createExtensions } from './extensions'
import { sanitizePastedHtml, withoutActiveContent } from './sanitize'

const editors: Editor[] = []

function editorWith(content?: string): Editor {
  const editor = new Editor({
    element: document.createElement('div'),
    extensions: createExtensions({ doc: new Y.Doc(), placeholder: '', checkboxLabel: () => '' }),
    editorProps: { transformPastedHTML: sanitizePastedHtml },
    content,
    injectCSS: false,
  })
  editors.push(editor)
  return editor
}

afterEach(() => {
  for (const editor of editors.splice(0)) editor.destroy()
})

describe('sanitizePastedHtml', () => {
  it.each([
    ['a script', '<p>a</p><script>window.pwned = 1</script>', 'script'],
    ['an iframe', '<iframe src="https://evil.example"></iframe><p>a</p>', 'iframe'],
    ['an object', '<object data="x.swf"></object><p>a</p>', 'object'],
    ['an embed', '<embed src="x.swf"><p>a</p>', 'embed'],
    ['an svg', '<svg onload="window.pwned = 1"><circle r="1"/></svg><p>a</p>', 'svg'],
    ['an image', '<img src="https://evil.example/x.png"><p>a</p>', 'img'],
    ['a style element', '<style>body { display: none }</style><p>a</p>', 'style'],
    ['a form', '<form action="https://evil.example"><button>x</button></form><p>a</p>', 'form'],
    ['a base element', '<base href="https://evil.example/"><p>a</p>', 'base'],
    [
      'a meta refresh',
      '<meta http-equiv="refresh" content="0;url=https://evil.example"><p>a</p>',
      'meta',
    ],
  ])('removes %s', (_name, html, selector) => {
    const result = document.createElement('div')
    result.innerHTML = sanitizePastedHtml(html)
    expect(result.querySelector(selector)).toBeNull()
    expect(result.textContent).toContain('a')
  })

  it.each([
    '<p onclick="x()">a</p>',
    '<a href="https://example.com" onmouseover="x()">a</a>',
    '<img src=x onerror=x()>',
    '<p/onclick=x()>a</p>',
    '<p ONCLICK = "x()">a</p>',
  ])('removes event handlers: %s', (html) => {
    expect(sanitizePastedHtml(html).toLowerCase()).not.toMatch(/\bon[a-z]+\s*=/)
  })

  it.each([
    'javascript:alert(1)',
    'JaVaScRiPt:alert(1)',
    ' javascript:alert(1)',
    'java\tscript:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'vbscript:msgbox(1)',
    '//evil.example/x',
    '/relative/path',
  ])('removes the address of a link to %s', (href) => {
    const result = document.createElement('div')
    result.innerHTML = sanitizePastedHtml(`<a href="${href}">link</a>`)
    expect(result.querySelector('a')?.getAttribute('href') ?? null).toBeNull()
    expect(result.textContent).toBe('link')
  })

  it.each(['https://example.com/a?b=c#d', 'http://example.com', 'mailto:someone@example.com'])(
    'keeps a link to %s',
    (href) => {
      expect(sanitizePastedHtml(`<a href="${href}">link</a>`)).toContain(`href="${href}"`)
    },
  )

  it('keeps every tag and attribute the editor can hold', () => {
    const html = [
      '<h1>one</h1><h2>two</h2><h3>three</h3><h4>four</h4><h5>five</h5><h6>six</h6>',
      '<p><strong>a</strong><b>b</b><em>c</em><i>d</i><u>e</u><s>f</s><del>g</del><strike>h</strike><code>i</code><br></p>',
      '<ul><li>x</li></ul><ol start="3"><li>y</li></ol><blockquote><p>q</p></blockquote><pre><code>code</code></pre><hr>',
      '<table><thead><tr><th colspan="2">h</th></tr></thead><tbody><tr><td rowspan="2">c</td><td>d</td></tr></tbody></table>',
      '<ul data-type="taskList"><li data-checked="true"><label><input type="checkbox" checked><span></span></label><div><p>t</p></div></li></ul>',
    ].join('')
    const result = document.createElement('div')
    result.innerHTML = sanitizePastedHtml(html)

    for (const selector of [
      'h1',
      'h2',
      'h3',
      'h4',
      'h5',
      'h6',
      'strong',
      'b',
      'em',
      'i',
      'u',
      's',
      'del',
      'strike',
      'code',
      'br',
      'ul',
      'ol[start="3"]',
      'blockquote',
      'pre',
      'hr',
      'table',
      'thead',
      'tbody',
      'th[colspan="2"]',
      'td[rowspan="2"]',
      'ul[data-type="taskList"]',
      'li[data-checked="true"]',
      'input[type="checkbox"][checked]',
    ]) {
      expect(result.querySelector(selector), selector).not.toBeNull()
    }
  })

  it('drops an input that is not a checkbox', () => {
    expect(
      sanitizePastedHtml('<p>a</p><input type="text" value="x"><input type="password">'),
    ).not.toContain('input')
  })

  it('drops attributes it has no use for, including a style on a block', () => {
    const result = document.createElement('div')
    result.innerHTML = sanitizePastedHtml(
      '<p id="x" class="y" style="color: red" data-foo="1" title="t" aria-label="l">a</p>',
    )
    const paragraph = result.querySelector('p')!
    expect(paragraph.getAttributeNames()).toEqual([])
  })

  it('keeps a style on the elements the marks read it from', () => {
    expect(sanitizePastedHtml('<span style="font-weight: 700">a</span>')).toContain(
      'style="font-weight: 700"',
    )
  })

  it('survives a round trip of what the editor writes itself, tables and checklists included', () => {
    const source = editorWith(
      [
        '<h2>Title</h2><p>text with <strong>bold</strong> and <a href="https://example.com">a link</a></p>',
        '<ul data-type="taskList"><li data-checked="true"><p>done</p></li><li data-checked="false"><p>open</p></li></ul>',
        '<table><tbody><tr><th>h</th><th>i</th></tr><tr><td>a</td><td>b</td></tr></tbody></table>',
        '<pre><code>let x = 1</code></pre>',
      ].join(''),
    )
    const written = source.getHTML()

    const copy = editorWith(sanitizePastedHtml(written))

    expect(copy.getJSON()).toEqual(source.getJSON())
  })

  it('does not turn a whole paste bold because of the wrapper Google Docs puts around it', () => {
    const editor = editorWith()
    editor.view.pasteHTML(
      '<meta charset="utf-8"><b style="font-weight:normal;" id="docs-internal-guid-1"><p>plain</p><p><span style="font-weight:700;">bold</span></p></b>',
    )

    const html = editor.getHTML()
    expect(html).toContain('<p>plain</p>')
    expect(html).toContain('<strong>bold</strong>')
  })

  it('keeps the text of a paste from Word and none of its markup', () => {
    const editor = editorWith()
    editor.view.pasteHTML(
      [
        '<html xmlns:o="urn:schemas-microsoft-com:office:office"><head><style>p.MsoNormal { margin: 0 }</style></head><body>',
        "<!--StartFragment--><p class=MsoNormal style='margin:0cm'><span style='font-size:11.0pt'>Hello</span><o:p></o:p></p><!--EndFragment-->",
        '</body></html>',
      ].join(''),
    )

    expect(editor.getText()).toContain('Hello')
    expect(editor.getHTML()).not.toMatch(/style|class|MsoNormal/)
  })

  it('leaves only allowed content in the document after a hostile paste', () => {
    const editor = editorWith()
    editor.view.pasteHTML(
      '<p>kept</p><img src=x onerror="window.pwned=1"><script>window.pwned=1</script><a href="javascript:window.pwned=1">bad</a>',
    )

    const html = editor.getHTML()
    expect(html).toContain('kept')
    expect(html).toContain('bad')
    expect(html).not.toMatch(/<img|<script|javascript:|onerror/i)
  })
})

describe('withoutActiveContent', () => {
  it('cuts style and script elements, and nothing around them', () => {
    expect(
      withoutActiveContent('<p>a</p><style>x { y: z }</style><p>b</p><script>1</script><p>c</p>'),
    ).toBe('<p>a</p><p>b</p><p>c</p>')
  })

  it('cuts an element that is never closed, to the end', () => {
    expect(withoutActiveContent('<p>a</p><script>alert(1)')).toBe('<p>a</p>')
  })

  it('cuts event handlers out of tags only', () => {
    expect(withoutActiveContent('<p onclick="x()" class="y">use onclick=foo here</p>')).toBe(
      '<p class="y">use onclick=foo here</p>',
    )
  })

  it('copes with handlers written in the odd ways HTML allows', () => {
    expect(withoutActiveContent("<img src=x onerror='x()'>")).toBe('<img src=x >')
    expect(withoutActiveContent('<img src=x onerror=x()>')).toBe('<img src=x >')
    expect(withoutActiveContent('<p/onclick=x()>a</p>')).toBe('<p/>a</p>')
  })
})
