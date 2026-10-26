# Yjs fixtures for the codec contract tests

Written by real Yjs, read by `Crdt/YjsCodecContractTests` through the real sidecar.

- `tiptap-small`, `tiptap-10k`, `no-format-2k`, `insert-only-2k`, `surrogate-split`, `yrs-out-of-order-repro`,
  `yjs-one-transaction-repro`: copied from the phase 0 spike (git tag `phase0-spike`, folders
  `spike/server/Spike.Crdt.Tests/Fixtures/` and `spike/server/Spike.Server.Tests/Fixtures/`; generators in
  `spike/client/scripts/`). Their `expectedText` is ProseMirror's text, which the sidecar's schema-free extract matches
  apart from empty lines and trailing spaces. `yrs-out-of-order-repro` is named after the YDotNet bug it found; for the
  sidecar it is the case of a deletion that arrives before its content.
- `non-tiptap`, and the `fingerprint` and `oneTransactionFingerprint` fields of `yjs-one-transaction-repro`: written by
  `server/sidecar/scripts/yjs-fixtures.ts`.
