import { describe, expect, it } from 'vitest'
import { isAllowedLink, pageIdOfLink } from './links'

const ID = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f00'
const ORIGIN = 'https://notes.example'

describe('isAllowedLink', () => {
  it.each([
    'https://example.com',
    'http://example.com/a?b#c',
    'mailto:someone@example.com',
    `/p/${ID}`,
  ])('allows %s', (href) => {
    expect(isAllowedLink(href)).toBe(true)
  })

  it.each([
    null,
    undefined,
    '',
    'javascript:alert(1)',
    'java\tscript:alert(1)',
    'data:text/html,x',
    '/relative',
    '/p/../x',
    `//p/${ID}`,
    `/p/${ID}?x=1`,
    `/p/${ID}#x`,
    `/p/${ID}/`,
    `/p/${ID}\n`,
    `/p/${ID.toUpperCase()}`,
    `/p/${ID}/extra`,
    '/p/',
    `p/${ID}`,
    `/q/${ID}`,
  ])('refuses %s', (href) => {
    expect(isAllowedLink(href)).toBe(false)
  })
})

describe('pageIdOfLink', () => {
  it('reads the id of a page path', () => {
    expect(pageIdOfLink(`/p/${ID}`, ORIGIN)).toBe(ID)
  })

  it('reads the id of the full address of a page on this origin, as a copied link has it', () => {
    expect(pageIdOfLink(`${ORIGIN}/p/${ID}`, ORIGIN)).toBe(ID)
  })

  it.each([
    `https://evil.example/p/${ID}`,
    `${ORIGIN}/p/${ID}?x=1`,
    `${ORIGIN}/p/${ID}#x`,
    `${ORIGIN}/other/${ID}`,
    `//p/${ID}`,
    'https://example.com',
    'mailto:someone@example.com',
    '',
    null,
    undefined,
  ])('finds no page in %s', (href) => {
    expect(pageIdOfLink(href, ORIGIN)).toBeNull()
  })
})
