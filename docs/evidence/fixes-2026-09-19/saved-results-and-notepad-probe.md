# Saved results and Notepad metadata probe — 2026-09-19

The user's saved checklist was read without modifying it. The saved summary records 14 successes and 4 failures. Word, PowerPoint, Chrome, and Edge each contain the same reported notification: “저장 실패. 이 화면의 작업위치는 아직 지원하지 않습니다.” The checklist also records that an Explorer file bookmark reopens the text file but cannot capture the state while working in Notepad.

The active WorkBookmark process was launched from a second extracted package location, rather than the installation directory named in the previous report. This does not establish an outdated build: both copies report FileVersion 0.1.0.0 and ProductVersion 0.1.0+a48987ab9f099d7e242e32ebbcb9a7666d039ee1. The current diagnostic log contains only two older successful Capture/CaptureCommitted entries; it supplies no error detail for the four failures.

## Notepad read-only API check

A temporary C# console probe in the ignored tools directory inspected metadata only, with a synthetic saved file open beside an untitled tab. It did not retrieve document text, invoke controls, or change the user's document.

- Installed Notepad package version: 11.2607.14.0, x64.
- The top-level class is Notepad. The active editor is a visible RichEditD2DPT under NotepadTextBox; the inactive tab has a hidden editor.
- EM_GETSEL returns the caret/selection through its full 32-bit output parameters. EM_GETMODIFY and WM_GETTEXTLENGTH respond. The synthetic test returned selection 0–0, modified false, and length 70.
- Managed UI Automation exposes the tab control (AutomationId Tabs), tab list (TabListView), and selection patterns. The editor exposes TextPattern and ValuePattern.
- Tab Name and window Name expose the basename. HelpText is empty on the window, selected tab, editor, and descendants. No absolute file path was found in these metadata properties.
- MSAA OBJID_CLIENT exposes only the window title. Editor accName, accDescription, and accHelp are null. OBJID_NATIVEOM returns E_FAIL (0x80004005) on the editor and window.

A basename alone cannot prove file identity when documents have the same name. A production adapter needs an authoritative full-path channel or explicit user file linking before persisting a Notepad location. Selection and modified-state reads must also be paired with active-window/tab revalidation.

Microsoft's [EM_GETSEL documentation](https://learn.microsoft.com/en-us/windows/win32/controls/em-getsel) confirms the full 32-bit start/end output parameters; the packed return value alone truncates beyond 65,535. The probe used the output parameters.

## Implemented support and regression results

The adapter now requests explicit source-file linking for every capture. It verifies the full Korean or English Notepad window title, equal normalized document/file text, saved state, native editor identity, and the original caret/content fingerprint after the file picker closes. It stores only the local path, normalized UTF-16 caret offset, and modified flag. The compared text is transient and is not included in IPC responses, the bookmark database, or diagnostic logs.

Current capture supports UTF-8 and BOM-detected UTF-16/UTF-32 text, bounded to 2 Mi UTF-16 characters and 8 MiB on disk. Unsaved changes, an ambiguous editor, mismatched file contents/title, and a changed picker observation are rejected.

Resume launches the explicit file through the system Notepad executable and returns NotepadOpenRequested. Automatic caret restoration is unavailable because modern Notepad does not expose an authoritative path for the selected tab; a new control with matching basename/content is insufficient proof when sessions are restored. No selection-changing message is sent by resume. The saved caret remains metadata for display and future supported restoration.

Validation completed:

- 31 binding/coordinate checks, including native full-DWORD selection 70,000–71,234 and LF/CRLF coordinate mapping.
- 13 final installed Notepad checks with the synthetic fixture: saved caret 42, fake-file rejection, stale-picker rejection, exact NotepadOpenRequested outcome, unchanged existing-tab selection, unchanged file bytes, and restoring the original selection. The first rerun was correctly rejected while another synthetic test tab was active; after opening the intended fixture, all 13 passed.
- The initial 2 reopen checks exercised a prototype heuristic. Their evidence is marked superseded: that implementation was removed after identity review and must not be treated as certification of the final behavior.

The corresponding JSON evidence files are notepad-checks.json, notepad-native-checks.json, and notepad-resume-checks.json.
