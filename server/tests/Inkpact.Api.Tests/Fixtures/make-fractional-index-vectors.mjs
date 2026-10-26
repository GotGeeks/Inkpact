// Writes fractional-index-vectors.json: what the original `fractional-indexing` package answers, for the C# port
// (FractionalIndex.cs) to match byte for byte.
//
// The original is NOT a dependency of this repository. To regenerate the file, in an empty folder outside it:
//
//   pnpm init && pnpm add fractional-indexing@4.0.0
//   copy this script next to package.json, set "type": "module", and run:
//   node make-fractional-index-vectors.mjs > fractional-index-vectors.json
//
// The package is CC0 (public domain). Everything here is seeded, so the same version gives the same file.

import { generateKeyBetween, generateNKeysBetween } from 'fractional-indexing'

// mulberry32: a small seeded generator, so the file is the same every time.
function random(seed) {
  let a = seed
  return () => {
    a |= 0
    a = (a + 0x6d2b79f5) | 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

const between = []
const many = []
const errors = []

function key(a, b) {
  between.push({ a: a ?? null, b: b ?? null, expected: generateKeyBetween(a, b) })
}

// 1. The first key, and growing at the end and at the start for a long way (the integer part gets longer).
key(null, null)
let last = null
for (let i = 0; i < 400; i++) {
  last = generateKeyBetween(last, null)
  between.push({ a: i === 0 ? null : between.at(-1).expected, b: null, expected: last })
}
let first = null
for (let i = 0; i < 400; i++) {
  first = generateKeyBetween(null, first)
  between.push({ a: null, b: i === 0 ? null : between.at(-1).expected, expected: first })
}

// 2. Always just after the same key, and always just before the same key: the fraction part gets longer.
{
  const a = generateKeyBetween(null, null)
  let b = generateKeyBetween(a, null)
  for (let i = 0; i < 60; i++) {
    const m = generateKeyBetween(a, b)
    between.push({ a, b, expected: m })
    b = m
  }
  let c = generateKeyBetween(a, null)
  for (let i = 0; i < 60; i++) {
    const m = generateKeyBetween(a, c)
    between.push({ a: m === a ? null : a, b: c, expected: m })
    c = m
  }
}

// 3. Random lists: insert at random places, as people do when they drag pages around.
for (const seed of [1, 2, 3, 4, 5, 6, 7, 8]) {
  const next = random(seed)
  const keys = []
  for (let step = 0; step < 150; step++) {
    const position = Math.floor(next() * (keys.length + 1))
    const a = position === 0 ? null : keys[position - 1]
    const b = position === keys.length ? null : keys[position]
    const k = generateKeyBetween(a, b)
    between.push({ a, b, expected: k })
    keys.splice(position, 0, k)
  }
}

// 4. A bound given in the wrong order: the original swaps them.
{
  const a = generateKeyBetween(null, null)
  const b = generateKeyBetween(a, null)
  between.push({ a: b, b: a, expected: generateKeyBetween(b, a) })
}

// 5. Several keys at once, between every kind of pair.
for (const n of [0, 1, 2, 3, 4, 5, 7, 10, 25]) {
  many.push({ a: null, b: null, n, expected: generateNKeysBetween(null, null, n) })
  many.push({ a: 'a0', b: null, n, expected: generateNKeysBetween('a0', null, n) })
  many.push({ a: null, b: 'a0', n, expected: generateNKeysBetween(null, 'a0', n) })
  many.push({ a: 'a0', b: 'a1', n, expected: generateNKeysBetween('a0', 'a1', n) })
  many.push({ a: 'a0', b: 'a0V', n, expected: generateNKeysBetween('a0', 'a0V', n) })
  many.push({ a: 'Zz', b: 'a0', n, expected: generateNKeysBetween('Zz', 'a0', n) })
  many.push({ a: 'az', b: 'b00', n, expected: generateNKeysBetween('az', 'b00', n) })
}

// 6. What the original refuses.
const refused = [
  ['a0', 'a0'], // equal bounds
  ['a00', 'a1'], // a trailing zero in the fraction
  ['a', null], // too short for its own head
  [null, 'a'],
  ['!0', null], // a head that does not exist
  ['A00000000000000000000000000', null], // the smallest possible integer
  ['a0', 'a00'],
].filter(Boolean)
for (const [a, b] of refused) {
  let threw = false
  try {
    generateKeyBetween(a, b)
  } catch {
    threw = true
  }
  if (!threw) throw new Error(`expected an error for ${a} ${b}`)
  errors.push({ a, b })
}
// The largest integer cannot be incremented any more in the integer part; the original falls back to the fraction.
{
  const largest = 'z' + 'z'.repeat(26)
  between.push({ a: largest, b: null, expected: generateKeyBetween(largest, null) })
}

process.stdout.write(
  JSON.stringify(
    {
      source: 'fractional-indexing 4.0.0 (CC0), https://github.com/rocicorp/fractional-indexing',
      between,
      many,
      errors,
    },
    null,
    1,
  ) + '\n',
)
