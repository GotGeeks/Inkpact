/** A tag as it appears on a page, or as a suggestion while adding one. */
export interface TagRef {
  id: string
  name: string
}

/** A workspace's tag, with how many live pages have it. */
export interface WorkspaceTag extends TagRef {
  pageCount: number
}

/** A page that has a tag: its id and title, and its parent's title so pages with one title can be told apart. */
export interface TagPage {
  id: string
  title: string
  parentTitle: string | null
}

export interface TagPagesResponse {
  tagName: string
  items: TagPage[]
  more: boolean
}
