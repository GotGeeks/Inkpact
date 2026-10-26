/**
 * How the document looks, as Tailwind classes on the editor's root element. TipTap's own stylesheet is switched off
 * (it is injected as a `<style>` element, which the strict policy blocks), so everything here comes from the theme
 * tokens in DESIGN.md. ProseMirror's few required rules are in style.css.
 */

const text = [
  'relative min-h-48 break-words whitespace-break-spaces text-body-lg text-on-surface outline-none',
  '[font-variant-ligatures:none]',
  '[&_p]:my-space-md [&_pre]:whitespace-pre-wrap [&_li]:relative',
  '[&_hr]:my-space-xl [&_hr]:border-border-strong',
  '[&_strong]:font-semibold',
]

const headings = [
  '[&_h1]:mt-space-xl [&_h1]:mb-space-sm [&_h1]:text-headline-lg',
  '[&_h2]:mt-space-xl [&_h2]:mb-space-sm [&_h2]:text-headline-sm',
  '[&_h3]:mt-space-lg [&_h3]:mb-space-sm [&_h3]:text-headline-sm',
  // The design styles three levels; a Joplin import has up to six.
  '[&_h4]:mt-space-lg [&_h4]:mb-space-sm [&_h4]:font-semibold',
  '[&_h5]:mt-space-lg [&_h5]:mb-space-sm [&_h5]:font-semibold',
  '[&_h6]:mt-space-lg [&_h6]:mb-space-sm [&_h6]:font-semibold',
]

const lists = [
  '[&_ul]:list-disc [&_ul]:ps-space-xl [&_ol]:list-decimal [&_ol]:ps-space-xl',
  '[&_li>p]:my-space-xs',
  // Checklists: a box of 18 px and, once ticked, the text crossed out and faded.
  '[&_ul[data-type=taskList]]:list-none [&_ul[data-type=taskList]]:ps-0',
  '[&_li[data-checked]]:flex [&_li[data-checked]]:items-start [&_li[data-checked]]:gap-space-md',
  '[&_li[data-checked]>label]:mt-space-xs [&_li[data-checked]>label]:select-none',
  '[&_li[data-checked]>div]:flex-1',
  '[&_input[type=checkbox]]:size-4.5 [&_input[type=checkbox]]:accent-primary-container',
  '[&_li[data-checked=true]>div]:line-through [&_li[data-checked=true]>div]:opacity-40',
]

const blocks = [
  '[&_pre]:overflow-x-auto [&_pre]:rounded [&_pre]:border [&_pre]:border-border-subtle [&_pre]:bg-surface-subtle [&_pre]:p-space-md',
  '[&_pre]:font-mono [&_pre]:text-code-inline',
  '[&_:not(pre)>code]:rounded-sm [&_:not(pre)>code]:bg-surface-subtle [&_:not(pre)>code]:px-space-xs [&_:not(pre)>code]:font-mono [&_:not(pre)>code]:text-code-inline',
  '[&_blockquote]:border-s-2 [&_blockquote]:border-border-strong [&_blockquote]:ps-space-lg [&_blockquote]:text-on-surface-variant',
  '[&_a]:text-secondary [&_a]:underline',
  '[&_table]:my-space-md [&_table]:w-full [&_table]:table-fixed [&_table]:border-collapse',
  '[&_td]:border [&_td]:border-border-subtle [&_td]:p-space-sm [&_td]:align-top',
  '[&_th]:border [&_th]:border-border-subtle [&_th]:bg-surface-subtle [&_th]:p-space-sm [&_th]:text-start [&_th]:align-top',
  '[&_.ProseMirror-selectednode]:outline-2 [&_.ProseMirror-selectednode]:outline-primary',
]

const placeholder = [
  '[&_p.is-editor-empty:first-child]:before:pointer-events-none [&_p.is-editor-empty:first-child]:before:float-left',
  '[&_p.is-editor-empty:first-child]:before:h-0 [&_p.is-editor-empty:first-child]:before:text-on-surface-variant',
  '[&_p.is-editor-empty:first-child]:before:content-[attr(data-placeholder)]',
]

export const editorClasses = [...text, ...headings, ...lists, ...blocks, ...placeholder].join(' ')
