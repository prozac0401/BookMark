# Native Windows adapter diagnostics

This standalone STA console diagnostic is test tooling, not shipped app behavior. It uses the real native object model and product capture adapter, never a mock. The HWND argument must identify a synthetic Excel fixture that you intend to bring to the foreground. The default mode reads metadata only. It does not create/open/save/close a workbook, write cells, or run macros.

Build with the repository SDK:

    .tools\dotnet\dotnet.exe build tools\WindowsChecks\WindowsChecks.csproj

Run with a decimal Excel top-level HWND from the regular probe inventory:

    .tools\dotnet\dotnet.exe tools\WindowsChecks\bin\Debug\net10.0-windows\WindowsChecks.dll 134010

It prints foreground snapshots, native EXCEL7 connection status, Window HWND/IUnknown equality, parent-chain checks and the captured DTO. Only run against synthetic fixtures when keeping or sharing output: a successful DTO includes the document path and sheet name. The handle above is an example from one session, not a reusable identity.

Optional final argument `resume` runs the real resume adapter for the just-captured fixture at `$F$42` and prints phase-only guard/enumeration diagnostics. This mode changes selection and may trigger existing Excel events; it is for synthetic fixtures only. It uses the ordinary single-open resume policy if the captured file closes before reconnection. It never writes cell contents or issues save/close/macro commands.

The initial real run established two issues that compilation could not detect: Excel's accessibility Window wrapper had a distinct COM identity from its collection Window, and LCID 127 failed on ActiveSheet. The corrected capture reads the synthetic saved workbook at 확정자!$D$127 (exact HWND + owning Application checks; LCID 1033). A subsequent enumeration diagnostic identified a hidden XLMAIN control window with no EXCEL7 child. Only when the same PID is fully connected/enumerated can that control window be covered by its Application inventory. The corrected resume then confirmed `$F$42`.

## P0-A active Explorer tabs: bounded method

Create only synthetic fixture folders under testdata/ExplorerP0/A and /B, each with two text files and a child folder. Open two new Explorer windows for those fixture folders. In each new window, manually add a second fixture tab; also include two tabs of A with different selected fixture files. Keep unrelated user folders out of these windows.

There is no documented public Shell API used here to create or activate Windows 11 Explorer tabs. Do not substitute an undocumented WM_COMMAND identifier or claim a multi-window loop proves tab support. For each of 50 steps, choose a fixture tab/window and single selection from a prepared expected-path list, then run the product isolated capture probe after the view is stable. Record the expected path before capture, returned DTO/result, top-level HWND and ActiveViewHwnd. Include two same-folder tabs with different selections. Use no-selection, file, selected-folder and multiple-selection cases. A deliberate transition is accepted only as ContextChanged/AmbiguousTarget or an exact expected snapshot; a different tab's path is always a failure.

Native IShellView.SelectItem or the verified view's ShellFolderView.SelectItem may be used to set synthetic selections after identifying its actual fixture directory. They must never navigate/select in an arbitrary user view or choose the first ShellWindows item sharing a HWND. Do not close user Explorer/Excel windows as cleanup. Fixture windows can be left for the tester to close normally.

Do not report P0-A as passed from the one-window diagnostic or from an always-failing adapter. The acceptance gate requires stable success and zero wrong-tab captures across the prescribed cases.


## Fifty separate-process Excel captures

The bounded synthetic-only runner first rejects every unrelated/unsaved/protected-view workbook, then launches only missing testdata/generated/A or B copies of 같은이름.xlsx with Excel's /x command-line switch. It never uses Workbooks.Open, changes cells, saves/closes books or terminates Excel. Existing fixture windows are reused; ambiguous existing copies cause a failure instead of another launch. Both fixture windows remain open for normal manual closing afterward.

    WindowsChecks.exe series APP_EXE EXCEL_EXE D:\Github_REPOS\BookMark\testdata\generated OUTPUT_JSON 50 $F$42 $F$42

Use shell-safe literal arguments for cell addresses; in PowerShell surround them with single quotes. The two cell arguments are the expected existing A/B positions, not navigation commands. Defaults are the file-generated A=$D$127 and B=$F$42. The example uses A=$F$42 because the preceding P0-C resume deliberately placed A there. The runner refuses to begin if actual initial sheet/cell/Saved state differs from the explicit oracle.

It alternates the verified fixture HWNDs from distinct Excel PIDs, takes the native pre-UI snapshot and sends each capture through the product WorkerClient to the actual app --worker. Each capture has a 3-second deadline. JSON evidence is updated after each attempt with expected/actual identities, result codes, elapsed time, p95, wrong-target/failure counts, launch PIDs and both files' before/after SHA-256. A wrong-target result or the first denied foreground activation stops the series immediately. A denied test-driver activation is a blocked run, with zero product-worker capture attempts for that step. Fifty successful captures establish this separate-process case only; same-process multi-window and the full P0-B gate still need their own evidence.

The first attempted 50-step run produced 50 ForegroundDenied driver results before any worker capture (0 product calls); both fixture hashes were unchanged. Its evidence is retained as Blocked, and the driver was then tightened to stop on the first denial. It is not a passing P0-B result.
