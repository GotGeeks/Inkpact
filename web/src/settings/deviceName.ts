/**
 * What a signed-in device is, read from its browser's user agent (run B.9): enough to tell one's own devices apart
 * ("Firefox on Windows"), not a fingerprint. Names of browsers and systems are names, the same in every language; a part
 * that is not known is null, and the screen says so in the person's language.
 *
 * The order matters: Edge, Opera and Samsung Internet also say "Chrome", Chrome also says "Safari", and iOS also says
 * "Mac OS X".
 */
export interface Device {
  browser: string | null
  system: string | null
}

const browsers: [RegExp, string][] = [
  [/\bEdg(?:e|A|iOS)?\//, 'Edge'],
  [/\bOPR\/|\bOpera\b/, 'Opera'],
  [/\bSamsungBrowser\//, 'Samsung Internet'],
  [/\bFirefox\/|\bFxiOS\//, 'Firefox'],
  [/\bChrome\/|\bCriOS\//, 'Chrome'],
  [/\bVersion\/[\d.]+.*\bSafari\//, 'Safari'],
]

const systems: [RegExp, string][] = [
  [/\biPad\b/, 'iPadOS'],
  [/\biPhone\b|\biPod\b/, 'iOS'],
  [/\bAndroid\b/, 'Android'],
  [/\bCrOS\b/, 'ChromeOS'],
  [/\bWindows\b/, 'Windows'],
  [/\bMacintosh\b|\bMac OS X\b/, 'macOS'],
  [/\bLinux\b/, 'Linux'],
]

export function deviceOf(userAgent: string | null | undefined): Device {
  const agent = userAgent ?? ''
  return {
    browser: browsers.find(([pattern]) => pattern.test(agent))?.[1] ?? null,
    system: systems.find(([pattern]) => pattern.test(agent))?.[1] ?? null,
  }
}
