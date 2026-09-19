# Browser native host integration checks

Build this project and WorkBookmark.BrowserHost, then run:

    WorkBookmark.Browser.Tests.exe FULL_PATH_TO_WorkBookmark.BrowserHost.exe

The suite runs the real host apphost executable as a fresh child for every case. It sends native-messaging frames on standard input and checks one clean framed response on standard output. The positive cases use a same-user, same-session, CurrentUserOnly named-pipe mock; no browser or user document is opened. A single 20-second suite deadline bounds the run.

The real WorkBookmark application must be stopped first. The test only observes pipe existence without connecting to it and refuses to run if the production pipe exists. It never asks the app to capture anything or closes a user process. Its mock is removed after each case. A fresh application launch during a test can make the test fail; do not run these checks alongside a real app session.

Covered boundaries:

- Exact URL case, percent escapes, query and fragment and Unicode title survive the native host hop.
- Invalid URI scheme, credentials, protocol/action, title controls, request ID and URL bounds are rejected before an app connection.
- Browser payload unknown fields and Core cross-kind location fields are rejected.
- A mismatched response ID or disagreement between the success flag and commit code cannot report success.
- Truncated/oversized frames, invalid JSON and oversized app replies cannot report a commit.
- With no app pipe, the production host returns AppNotRunning after its 2-second connection timeout.
- Standard output contains exactly one native frame and no diagnostic metadata.

These tests do **not** establish a database commit: a mock response intentionally stands in for the application's persistence callback. They also do not establish extension installation, browser button behavior, active-tab permission handling, App UI-thread dispatch, or GUI notification behavior.

Remaining app-level integration cases (synthetic repository only):

1. Construct the app context with an isolated temporary repository and drive CaptureBrowserAsync on its UI dispatcher. Query SQLite after CaptureCommitted; compare URL/title and verify no capture was acknowledged before commit.
2. Inject a repository write failure and verify no success response or success notification.
3. Capture the same URL twice after adding a note; confirm note preservation, one identity and increasing capture sequence. Query/fragment variants must remain distinct.
4. Hold an existing operation busy; confirm AppBusy and no extra repository write.
5. Cancel before UI dispatch and before persistence starts; confirm no write. If cancellation races a completed commit, confirm the unknown outcome is honest and no automatic retry occurs.
6. Dispose/exit the application with a connected native host; confirm bounded host failure without a false commit and no leaked listening pipe.

Read-only review found that the current CaptureBrowserAsync sends CaptureCommitted only after awaiting repository UpsertCapture. This is source review evidence; it is separate from running the GUI/database cases above.
