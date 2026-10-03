# Clips

A quiet Windows desktop capture notebook. Create a notebook, start a session, then intentionally save selected text or a rectangular screenshot while reading in another application. Review captures in order, attach notes, and return later. The application runs locally without accounts, servers, AI, sync, or telemetry.

## Run the built application

The latest build is **`artifacts/Clips-win-x64-panel/Clips.App.exe`**, including the floating session panel and earlier capture/note fixes. Quit the previous instance from its system-tray menu before launching this version. All builds use the same existing local data directory; no data migration or reset is required.

The generated **`artifacts/Clips-win-x64/Clips.App.exe`** is a self-contained, single-file Windows x64 application. Double-click it; no separate .NET runtime is required. Build artifacts are ignored by Git. This executable is unsigned and is not an installer.

Opening Clips always shows the main window, including when a session is restored. Opening it again brings the running instance forward. On first launch, create a notebook with a name, colour, and optional icon. Creating a notebook does not start capture. Press **Start session** to hide the main window and show a small floating panel. **Recent captures** opens the existing capture tray for review and notes; **Open notebook** brings back the main window. Drag the panel’s notebook/status header to reposition it. It stays above other apps without taking focus when shown, remains available while paused, and temporarily hides during screenshot capture. Ending the session removes the panel and reopens the notebook. Use the Windows notification-area icon to pause/resume, end the session, or quit. Closing the main window or capture tray hides it. Quit is an explicit tray action.

## Prerequisites and development

- Windows 10 **1903 or later**, or Windows 11. This build was compiled and its WPF views rendered on the current Windows machine; older Windows versions still require manual compatibility QA.
- .NET **8 SDK** (the repository accepts any .NET 8 feature band at or above 8.0.100).
- Visual Studio 2022 17.8+ with **.NET desktop development**, or the .NET CLI.
- Internet access only for the initial SDK/NuGet restore and publishing runtime packages. The installed application makes no network requests.

Open `Clips.sln` in Visual Studio and set `Clips.App` as the startup project, or run:

```powershell
dotnet restore Clips.sln --configfile NuGet.Config
dotnet build Clips.sln -c Release --no-restore
dotnet test Clips.sln -c Release --no-build --no-restore
dotnet run --project src/Clips.App
```

The workspace also contains a locally downloaded SDK in `.dotnet/` (ignored by Git). To use it instead of a global SDK:

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.build-home"
$env:NUGET_PACKAGES = "$PWD/.nuget/packages"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& ./.dotnet/dotnet.exe build Clips.sln -c Release --no-restore
```

Publish a portable single executable:

```powershell
dotnet publish src/Clips.App/Clips.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/Clips-win-x64 --configfile NuGet.Config
```

Alternatively use the `Windows-x64` Visual Studio publish profile. WPF trimming is disabled. The single-file runtime extracts native dependencies to its standard local temporary extraction directory. Do not publish the smoke tool with the product.

## Architecture

- **Clips.Core**: immutable domain records, validation, normalization, interfaces, notebook commands, and capture orchestration. No WPF or database dependencies.
- **Clips.Infrastructure**: raw parameterized SQLite, embedded versioned SQL migrations, atomic PNG storage, orphan reconciliation, bounded rolling diagnostic logs, UI Automation selected-text reading, foreground context, `RegisterHotKey`, and one-shot GDI desktop snapshots. GDI is used as the Windows-native screen capture implementation for broad Windows 10 compatibility.
- **Clips.App**: WPF views, CommunityToolkit.Mvvm view models and commands, Microsoft DI composition root, system tray, non-activating notifications, compact review tray, settings/themes, and rectangular snipping overlay.
- **Clips.Tests**: xUnit tests with isolated real SQLite databases, injected transactional failures, and controlled selection-reader fakes.
- **tools/Clips.Smoke**: a developer-only WPF runtime construction/render check using fabricated content and temporary storage. It does not read selections or take desktop screenshots.

SQLite operations use a serialized connection and execute on a worker thread, because Microsoft.Data.Sqlite’s I/O is synchronous internally. Selection retrieval runs on an isolated STA thread with a two-second timeout; a hung provider cannot accumulate additional reader threads. PNG encoding, image loading, filesystem operations, and snapshot acquisition run off the UI thread. Capture and storage ordering is serialized; persistence revalidates the session inside the transaction.

One open session (Active **or Paused**) is enforced in SQLite with a partial unique index. Switching notebooks requires explicit confirmation and atomically ends the old session. Captures append to the notebook’s integer order; move-up/down and deletion compact order deterministically in a transaction. Paused and ended sessions cannot accept captures. A restored open session keeps its state on restart; the main window stays hidden.

Notes use **explicit Save note** controls in both views. The unsaved state is visible; drafts survive refreshes and notebook navigation in memory. Quit warns about unsaved drafts. Save note is required to preserve edits across restart.

## Local storage and privacy

```text
%LOCALAPPDATA%\Clips\
  clips.db
  captures\{capture-id}.png
  logs\clips.log
```

The exact directory is shown in Settings with an **Open data folder** action. SQLite may also create `clips.db-wal` and `clips.db-shm` while running. The working name and storage-directory name are isolated in `Clips.Core/Product`; change the display name separately from the storage name to preserve existing data.

- The application contains no network client, login, analytics, cloud storage, clipboard access, keyboard hooks, or selection polling.
- Text is read only following an explicit capture hotkey. It confines selection-provider discovery to the foreground window and rejects password elements, the application itself, and an editable process-name exclusion set initially containing `1Password`, `KeePass`, `KeePassXC`, and `Bitwarden`. This set is configurable in the Windows service’s `ExcludedProcessNames` property; there is no exclusion-list editor in this MVP.
- Only accessible **selected text** is requested. Discovery checks the focused element and its ancestors, then breadth-first searches at most 256 visible foreground-window elements to a depth of 16. Window membership uses accessibility ancestry rather than matching process IDs, so hosted/multiprocess document providers are permitted. Password and offscreen branches are skipped. It reads selection ranges only, never the full document range. Foreground changes discard the result; provider errors, timeouts, and oversized selections have distinct messages. No browser history, URL extraction, full documents, or automatic content collection. Source URL/document/page fields remain null.
- Snipping takes one in-memory primary-display snapshot after the explicit shortcut. Only the chosen region is persisted. Escape and regions smaller than 8×8 physical pixels save nothing. No full-screen image is written to disk and no recording session exists.
- Diagnostics contain constant event identifiers, UTC times, and numeric error codes. Captures, notes, notebook names, window titles, and exception messages are never logged. Logs roll at 1 MB and keep three previous files.
- Content is stored **unencrypted locally**, subject to the Windows user profile’s permissions. The app does not claim protection against another process running as the same user. Use Windows device encryption where needed.

PNG storage writes and flushes a temporary file, atomically renames it, then commits the capture record. If the database insert fails, the final PNG is removed. Startup removes orphan `.png.tmp` files and unreferenced capture PNGs left by interrupted operations. Database deletion happens before image deletion; failed file deletion can leave an unreferenced file that startup reconciliation removes. Loaded thumbnails release their file handles, including when Undo removes a visible image.

## Shortcuts and settings

- **Ctrl+Alt+S**: capture accessible selected text.
- **Ctrl+Alt+A**: snip an image region on the primary display.
- **Ctrl+Alt+N**: open the compact capture tray.
- **Escape**: cancel a snip or notebook dialog; hide a focused notification.

Text/image shortcuts are registered only while Active and are unregistered on pause, end, and shutdown. Without an active session they do not intercept another application’s shortcut. The tray shortcut remains available. The capture service also rejects missing/paused/changed sessions, including changes during asynchronous capture.

Settings allow distinct Ctrl+Alt combinations with letters or F1–F12, validate internal conflicts, attempt global registration, and report Windows/other-app conflicts without ending the session. Choose System/Light/Dark, toggle capture confirmations, or start minimized. Errors and important conflict notifications remain visible when capture confirmations are disabled. The first empty launch always opens the main window.

## Verification performed

- Release solution build: **0 warnings, 0 errors**.
- **46 passing xUnit cases**: validation, migrations/reopening, one-open-session enforcement, transitions/restoration, normalization/paragraphs, ordering/reordering, parallel appends, notes/settings, fallbacks, no-session/paused behavior, undo, image cleanup, orphan recovery, path validation, and transactional rollback through an injected SQLite trigger.
- WPF smoke: six views constructed and rendered with **zero binding errors**; text/image persistence and image Undo after rendering checked. Generated images and result are in `artifacts/smoke/`. Native keyboard QA additionally verified screenshot-note typing/saving in the capture tray and text-note typing/saving in the notebook against an isolated temporary SQLite database. Add note scrolls its editor into view and gives it keyboard focus; merely opening the tray still uses non-activating Show().

To repeat the runtime smoke check on an interactive Windows desktop:

```powershell
dotnet run --project tools/Clips.Smoke -c Release -- artifacts/smoke
```

It briefly opens real WPF views, writes rendered PNGs, uses fabricated source content, and deletes its isolated temporary data on successful exit. This is **not** evidence that every interactive acceptance criterion passes. Real-app selected-text retrieval (including Chrome PDF), focus relative to external source apps, snipping input, mixed-DPI displays, and Windows-version compatibility require the manual checks below. The attempted Chrome desktop test was denied by the computer-use permission layer; no Chrome/PDF compatibility claim is based on it.

## Known limitations

- **Primary-monitor-only snipping.** Other monitors are untouched. Crop coordinates are converted from logical overlay coordinates to physical snapshot pixels; 100%, 125%, 150%, and 200% display scaling still need interactive verification.
- UI Automation support depends on the source application and its accessibility provider. Some PDF readers, browser content, custom renderers, secure controls, and elevated processes will not expose a usable selection. A screenshot fallback is offered, without reading or altering the clipboard. Selected text is bounded to one million characters per selection range.
- The capture tray uses an almost-opaque glass-inspired surface; native acrylic/background blur and saved tray geometry are deferred. Windows title bars and some standard controls follow their OS appearance.
- The executable is unsigned; installer/signing, automatic startup registration, and update delivery are deferred. ARM64/x86 publishing is not verified.
- There is no app-level encrypted database or secure-memory erasure. Cancelled screenshot buffers become eligible for normal garbage collection.
- The full manual desktop loop has not been certified on Windows 10/11 or on multiple DPI configurations. Complete that checklist before treating this as a release-approved product.

## Manual QA checklist

Run on Windows 10 1903+ and Windows 11, with a clean test profile and on a writable local drive.

- [ ] First launch opens the empty state. Create a notebook with a colour/icon. Cancel/Escape creates nothing. Blank/101-character names are rejected. Restart verifies notebook persistence.
- [ ] Start a session. Verify main window hides, green tray icon appears, and the confirmation does not activate the app.
- [ ] Open another notebook and attempt Start session. Keeping the current session changes nothing; explicit replacement ends the old session and starts the new one.
- [ ] In compatible Notepad, select a sentence with paragraph breaks and press Ctrl+Alt+S. Verify text, source app/title, capture order, and continued source-app focus.
- [ ] Try no selection, an unsupported app, a password control, a configured password-manager process, and an elevated app. Confirm useful fallback/error and no clipboard changes.
- [ ] Press Ctrl+Alt+A. Draw a rectangle, including near display edges. Verify the PNG and original foreground source metadata. Repeat at 100%, 125%, 150%, and 200% scaling.
- [ ] Escape a snip and draw a tiny region. Confirm neither creates a row or PNG and no temporary files remain.
- [ ] Undo text and image notifications. Verify only the corresponding capture is deleted, including the PNG while its thumbnail is visible.
- [ ] Open the capture tray with Ctrl+Alt+N and the tray menu. Confirm opening does not take focus, clicking allows interaction, latest five are chronological, and closing only hides the tray.
- [ ] Add and save notes to text/images in both views. Change notebooks and refresh. Verify unsaved drafts are labelled, saved edits survive restart, and Quit warns on unsaved notes.
- [ ] Move captures up/down, including first/last boundaries. Delete with confirmation. Restart verifies deterministic numbering/order. Click an image to expand it.
- [ ] Rename/change colour/icon; archive/restore; delete a notebook with destructive confirmation. Active/paused notebooks cannot be archived/deleted until ended.
- [ ] Pause/resume using main UI and tray menu. Verify icon/status changes and captures do not occur while paused. End with confirmation; captures remain readable and end count is correct.
- [ ] Quit/reopen during Active and during Paused. Verify state restoration, proper hotkeys, and no forced main window. Launch a second instance and confirm no second storage writer opens.
- [ ] Reserve a shortcut in another app and change shortcuts in Settings. Verify conflicts produce a resolution path while the session stays open. Internal duplicate shortcuts are rejected.
- [ ] Without any session, confirm text/image shortcuts do not intercept input and the tray shortcut still works. Toggle confirmations and start-minimized behavior.
- [ ] Exercise System/Light/Dark and Windows high contrast; navigate with Tab/Shift+Tab/Enter/Escape. Check focus visibility and readable text/actions.
- [ ] Disconnect the network and repeat the loop. Verify all data remains accessible offline. Inspect process network activity and logs; no captured content or window titles should appear in logs.
- [ ] With the app closed, create an unreferenced GUID PNG/temp PNG in a test capture folder, then relaunch. Verify cleanup preserves referenced images. Test a read-only storage folder and ensure errors do not silently discard captures.

See `docs/IMPLEMENTATION.md` for the implementation report and next feature.
