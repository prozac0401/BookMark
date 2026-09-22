# WorkBookmark artwork verification

The original mark is a teal bookmark ribbon on an ink tile. The tray uses a separate transparent silhouette with a dark contour, so its edge remains visible against light and dark taskbars. The 16–24 px application artwork is optically simplified.

- Source: `src/WorkBookmark.App/Assets/*.svg` and `installer/Assets/*.svg`.
- Generated: application and tray ICO resources, plus WiX dialog/banner BMP resources.
- Preview: `branding-preview.png`, with icons at their actual pixel sizes on both taskbar themes.
- Rebuild: `node scripts/build-brand-assets.cjs` with Node.js 20+ and `sharp` available to Node's module resolver (`NODE_PATH` can point to an existing dependency directory). This is an artwork-maintenance command; app and MSI builds consume checked-in assets and require no Node dependency.

Verified on Windows, 22 September 2026:

- Both ICO files contain 16, 20, 24, 32, 40, 48, 64, 128 and 256 px RGBA frames.
- `System.Drawing.Icon` loads all 16–128 px frames at the requested dimensions. Independent clones still convert to bitmaps after the source icon and stream are disposed.
- Native `user32!LoadImage` loads both 256 px resources successfully. (The managed `Icon` size selector chooses the 128 px entry when asked for 256 px; the app uses 32 px and the system small-icon size.)
- The welcome bitmap is 493 × 312 px, 24-bit RGB. Every pixel at x ≥ 164 is white, preserving the WiX text region.
- The banner bitmap is 493 × 58 px, 24-bit RGB. Every pixel at x &lt; 426 is white, preserving WiX titles and descriptions.
- The preview was visually inspected at native resolution. Small app icons and tray silhouettes remain distinct at 16 px, and the installer wordmark fits within its artwork region.

`Branding.CreateApplicationIcon()` and `Branding.CreateTrayIcon()` return independent, caller-owned clones of embedded resources. No loose artwork files are needed after installation.

## 0.1.7 build and regression verification

The full solution compiled in Release with .NET SDK 10.0.401, zero warnings and zero errors. See [build and Core/Storage log](tests/build-and-core.log). This first script invocation completed the 44 Core/Storage checks, then the host refused the `dotnet run` launch of the integration executable. The remaining suites were executed with explicit native process launches; only complete passing runs are counted.

| Suite | Passed checks | Evidence |
|---|---:|---|
| Core/Storage, URL and snapshot rules | 44 | [log](tests/build-and-core.log) |
| IPC/process integration | 17 | [log](tests/integration.log) |
| Desktop components and resume notification policy | 87 | [log](tests/desktop.log) |
| Windows adapter boundary | 10 | [log](tests/adapter-checks.log) |
| Excel/session evidence verdicts | 27 | [Excel](tests/evidence-selftest.log), [session](tests/session-selftest.log) |
| Word coordinates/protection | 24 | [log](tests/word-checks.log) |
| Office URL/resume adapters | 103 | [log](tests/office-url-checks.log) |
| Browser adapter | 30 | [log](tests/browser-checks.log) |
| Notepad snapshot recovery | 28 | [log](tests/notepad-checks.log) |
| PDF transport | 28 | [log](tests/pdf-checks.log) |
| Browser native host | 27 | [log](tests/browser-native.log) |
| Browser/SQLite integration | 15 | [log](tests/browser-sqlite.log) |
| Browser extension | 23 | [log](tests/browser-extension.log) |
| Browser registration ownership | 7 | [log](tests/browser-registration.log) |

**Total: 470 automatic checks passed.** The successful desktop and browser/SQLite runs use a fresh output directory to avoid a file lock left after an earlier aborted test invocation.

The added notification checks verify that `OfficeDocumentOpened` and unconfirmed results clear progress quietly, while actual access failures, explicit position failures and login actions remain visible. Icons load from embedded resources and application disposal removes the tray icon and closes owned forms.

## Rendered component review

The test-only `--render-branding OUTPUT_DIRECTORY` command creates the actual WinForms controls using synthetic documents at 96 DPI. The initial attempt rendered empty child areas while forms were hidden; the renderer now creates and shows the test forms before drawing them, then hides and disposes them. The four final PNGs were inspected:

- [First-use notification](ui/toast-introduction.png): all four text lines and the settings action fit.
- [Long Korean path notification](ui/toast-long-korean-path.png): six text lines and the memo action fit; literal `R&D &` is retained.
- [Recent bookmarks](ui/recent-bookmarks.png): synthetic Excel, Word, web and folder entries, with readable selection, location and notes.
- [Settings](ui/settings.png): branded title icon, readable shortcuts, registration status and actions.

[Render dimensions and label measurements](ui/render-manifest.json) distinguish these component images from live desktop screenshots. The Windows Computer Use native connection was unavailable, so live installer interaction and taskbar screenshots were not captured. Higher-DPI Windows taskbar rendering, actual MSI upgrade/removal, Windows re-login and environment-specific Office/AD acceptance remain separate manual checks. Existing compatibility records are unchanged.

The final MSI package includes an attached `.validation.json` on the [0.1.7 release](https://github.com/prozac0401/BookMark/releases/tag/v0.1.7). Its checks compare embedded artwork and shortcut icon resources, installer conditions, administrative extraction and every extracted payload file against the publish directory. Administrative extraction does not install or upgrade the product.
