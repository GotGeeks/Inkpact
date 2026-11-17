# Changelog

## [0.3.1](https://github.com/GotGeeks/Inkpact/compare/v0.3.0...v0.3.1) (2026-11-17)


### Fixes

* **security:** fresh Ubuntu packages in every day's image, patched glob and minimatch, an origin check in the worker ([#177](https://github.com/GotGeeks/Inkpact/issues/177)) ([a43c0be](https://github.com/GotGeeks/Inkpact/commit/a43c0bef3778e23c7f5c061c654263e18ab487f5))

## [0.3.0](https://github.com/GotGeeks/Inkpact/compare/v0.2.0...v0.3.0) (2026-11-15)


### Features

* **editor:** say when a change was refused for the page size, and let it be discarded (B.10) ([#164](https://github.com/GotGeeks/Inkpact/issues/164)) ([05c56e0](https://github.com/GotGeeks/Inkpact/commit/05c56e01e77f2c0640cb4bbc872e404f2219cf69))
* **sync:** refuse live edits that would make a page too large to open (B.10) ([#163](https://github.com/GotGeeks/Inkpact/issues/163)) ([cf96206](https://github.com/GotGeeks/Inkpact/commit/cf96206444a0c1145f47f0afdade2a941320a962))

## [0.2.0](https://github.com/GotGeeks/Inkpact/compare/v0.1.1...v0.2.0) (2026-11-15)


### Features

* **account:** change the display name, and see and sign out signed-in devices (B.9) ([#159](https://github.com/GotGeeks/Inkpact/issues/159)) ([04324e9](https://github.com/GotGeeks/Inkpact/commit/04324e9f979462460fa0232abab46c990c25d2e7))
* **account:** change your own password, signing out every other device (B.9) ([#161](https://github.com/GotGeeks/Inkpact/issues/161)) ([8b7c051](https://github.com/GotGeeks/Inkpact/commit/8b7c051d31f65b865d5bd254bfd621b4e9f1d588))

## [0.1.1](https://github.com/GotGeeks/Inkpact/compare/v0.1.0...v0.1.1) (2026-11-13)


### Fixes

* **deploy:** run the latest release, correct the image's labels, and ask for cosign 3 (B.8) ([#157](https://github.com/GotGeeks/Inkpact/issues/157)) ([09af11e](https://github.com/GotGeeks/Inkpact/commit/09af11eb4c414aef00215254e969b6b0403edbbd))

## 0.1.0 (2026-11-13)


### Features

* **deploy:** the Unraid template, and a clear stop on a data folder the app cannot write (B.8) ([236a52e](https://github.com/GotGeeks/Inkpact/commit/236a52eac7abc94c1149437d638ec98b8c5caf31))
* **deploy:** the Unraid template, and a clear stop on an unwritable data folder (B.8, PR 3) ([01b39eb](https://github.com/GotGeeks/Inkpact/commit/01b39eb81c0bb34a01038a49329b5160bc2037b3))
* **versions:** the app knows its version and tells the administrator of a newer one (B.8) ([3f9dafa](https://github.com/GotGeeks/Inkpact/commit/3f9dafa873bc6eb632715f695402764e77663ff6))
* **versions:** the B.8 card, and the app's version and update check (B.8, PR 1) ([06e2872](https://github.com/GotGeeks/Inkpact/commit/06e2872cbc66bc01bffa2263965920137f4fd7be))


### Fixes

* **ci:** the review's findings 4, 5, 8 and 9 on [#153](https://github.com/GotGeeks/Inkpact/issues/153) (B.8) ([fb813f4](https://github.com/GotGeeks/Inkpact/commit/fb813f49c555877d338760872afbc5a041fa66f9))
* **deploy:** the review's findings 3 and 4 on [#154](https://github.com/GotGeeks/Inkpact/issues/154) (B.8) ([c3a7786](https://github.com/GotGeeks/Inkpact/commit/c3a7786362eeaf0b84b5e33fd2250c917e19340f))
* **versions:** a broken connection or an unexpected failure no longer stops the app (B.8) ([de71f29](https://github.com/GotGeeks/Inkpact/commit/de71f297987e7cdecb07398fd324abcbd4ddf1d6))
* **versions:** the review's findings 2, 6, 7 and 10 on [#152](https://github.com/GotGeeks/Inkpact/issues/152) (B.8) ([b77a45c](https://github.com/GotGeeks/Inkpact/commit/b77a45c45d8664de4c06163789022845dc948f8d))
