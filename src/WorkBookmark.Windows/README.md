# Windows adapter boundary

All methods that touch Shell COM, Excel COM, or files run only inside the per-request STA worker. The only UI-safe entry point is ForegroundSnapshot.Capture(), which uses local Win32 calls without COM or filesystem access. Execute returns DTOs; it never writes the database. Capture checks the target process token in the worker and rejects elevated or unreadable elevation state. Excel resume treats an elevated/inaccessible instance as incomplete enumeration and never opens another copy on that basis. No RunAs, token adjustment or permission change is used.

## Capture identity

Explorer capture enumerates every matching ShellWindows item and obtains IShellBrowser through IServiceProvider.QueryService(SID_STopLevelBrowser). QueryActiveShellView supplies the active view; its HWND must be visible, belong to the original foreground window, and equal the visible SHELLDLL_DefView HWND captured at hotkey dispatch. Folder/selection automation comes from that exact IShellView.GetItemObject(SVGIO_BACKGROUND, IID_IDispatch). Multiple distinct candidates are rejected. Folder filesystem identity, directory existence and the full selection are read twice, with foreground/focus/process/visible-view checks in between.

SHELLDLL_DefView, CabinetWClass and ExploreWClass are isolated Windows Shell class dependencies, not promises that all future Windows 11 builds expose the same structure. Zero or multiple visible views fail closed. Actual active-tab support must be established with P0-A on the tested build. Source code and successful compilation do not establish it.

Excel capture obtains native Window objects through EXCEL7 and AccessibleObjectFromWindow(OBJID_NATIVEOM). It verifies Window.Application, Application.ActiveWindow/ActiveWorkbook, ActiveCell.Parent, Worksheet.Parent and membership in Workbook.Windows. In the tested Excel build, the native-accessibility Window and collection Window had different IUnknown identities; Window equality therefore requires the exact live HWND plus the same Application identity. Workbook and Worksheet COM identities are checked directly. Reflection calls use Excel’s en-US LCID 1033 automation contract; invariant LCID 127 produced TYPE_E_INVDATAREAD on the tested Korean installation. Two reads must agree, including full path, sheet, address and unsaved state. Chart sheets and no-path books are rejected. A merged cell uses the top-left cell of MergeArea consistently for capture and resume. No cell value/formula is read.

## Resume and uncertainty

Excel resume enumerates top-level XLMAIN windows, independently running Excel processes in the current session, and every connected Application.Workbooks collection. Failed connections, protected-view windows, changed inventories and unresolved aliases make enumeration incomplete. A hidden XLMAIN with no EXCEL7 child is covered only by a successfully connected Application from the same PID with complete workbook/protected-view enumeration; it is not treated as a separate missing instance. An exact path in multiple distinct Workbook COM identities is ambiguous; multiple windows of the same Workbook are not duplicate copies. Case differences and matching filesystem file IDs are detected as possible aliases and reported unconfirmed rather than opened again.

Only a complete negative enumeration can trigger one Shell open. Observation then repeats within the original deadline without repeating the open. Temporary incomplete inventories after that single open are observed until the deadline, because Excel can expose its top-level window before its native object model and workbook are ready. A matching native Window is reconnected before navigation. Hidden sheets/rows/columns, selection restrictions, changed merged-cell anchors and invalid stored coordinates are rejected without modification. Range navigation is confirmed by rereading the exact workbook/sheet/cell. Foreground success is a separate result.

The temporary resume input hooks retain only a boolean for fresh keyboard presses, mouse buttons and wheel activity. They do not retain key codes, text or mouse positions; key/button releases and mouse movement are ignored. New input stops later navigation even inside the same Excel window. Unrelated or unknown foreground windows also stop later actions. Permitted HWNDs are checked again against their PID and process creation stamp, and file resume installs the guard before existence checks that could wait on a network. The app must establish the input baseline after dispatching the initiating Enter/click and hiding its list, and should monitor input during worker startup. Hooks and the parent process deadline cannot undo an already transmitted COM/Shell action.

Only Window.Activate, Worksheet.Activate and Application.Goto with a validated Range are issued to Excel. No Save, SaveAs, Close, Quit, workbook opening API, cell writes, Saved setter, macro calls or global setting changes are implemented. Existing Excel events, add-ins and AutoSave can still run because opening or moving a cell can trigger the user's existing behavior.

## Official API references

- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishellbrowser
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellbrowser-queryactiveshellview
- https://learn.microsoft.com/en-us/windows/win32/api/oleacc/nf-oleacc-accessibleobjectfromwindow
- https://learn.microsoft.com/en-us/office/vba/api/excel.window.activecell
- https://learn.microsoft.com/en-us/office/vba/api/excel.application.goto

Actual P0/E/X/D acceptance results belong in the repository validation reports, not in this implementation note.
