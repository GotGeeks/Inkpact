/** A piece of a title or a snippet, and whether it is what the search matched. The client renders text, never markup. */
export interface SearchSegment {
  text: string
  match: boolean
}

/** A page that matched. An empty title is a page with none; an empty snippet, a page that matched by its title or a tag. */
export interface SearchHit {
  id: string
  title: SearchSegment[]
  snippet: SearchSegment[]
}

export interface SearchResponse {
  results: SearchHit[]
  /** More pages matched than are in the list: a longer search narrows it. */
  more: boolean
}
