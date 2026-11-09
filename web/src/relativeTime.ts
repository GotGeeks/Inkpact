const units: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 3600],
  ['month', 30 * 24 * 3600],
  ['week', 7 * 24 * 3600],
  ['day', 24 * 3600],
  ['hour', 3600],
  ['minute', 60],
]

/** "5 minutes ago", "yesterday", in the language of the page; anything under a minute is "this minute". */
export function formatAgo(iso: string, locale: string, now: Date = new Date()): string {
  const formatter = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' })
  const seconds = Math.max(0, (now.getTime() - new Date(iso).getTime()) / 1000)
  for (const [unit, size] of units) {
    if (seconds >= size) return formatter.format(-Math.floor(seconds / size), unit)
  }
  return formatter.format(0, 'minute')
}
