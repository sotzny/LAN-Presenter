# Contributor guidance

**Language:** [Deutsch](AGENTS.md) · English

## Architecture and boundaries

- `src/BeamerPresenter.Domain` must not have any project dependencies.
- `Application` references only `Domain`; the UI and infrastructure stay outside the business logic.
- `App` is the only composition root. The WinForms application and Kestrel deliberately run in one process.
- User data belongs under `%LOCALAPPDATA%\BeamerPresenter\Presenter`, never in the installation directory or repository.

## Security

- Never log, test, commit, or put web passwords in `appsettings`.
- Passwords are stored with PBKDF2-SHA512 and an individual salt. Changes to this need a safe migration path.
- Uploads remain authenticated; always normalize filenames with `Path.GetFileName` and check allowed extensions centrally.
- `/presenter` remains accessible without cookie authentication but, like video streams under `/media/{mediaId}` and `/hubs/presenter`, only from loopback addresses. The `/media` management page (including `/media/`) requires cookie authentication and is also accessible from the LAN when LAN access is deliberately enabled. Do not add management endpoints under the local presenter resources, and always register the loopback middleware before authentication and authorization.
- By default, the Web UI binds only to `127.0.0.1`. `PresenterSettings.AllowLanAccess` may be enabled deliberately only through the desktop setting; only then does Kestrel bind to `0.0.0.0`, while all management routes still require cookie authentication.
- Razor Components require `UseAntiforgery()` after `UseAuthentication()` and `UseAuthorization()`; without this middleware, even `/login` returns HTTP 500.
- The WinForms host does not create a merged static asset manifest. MSBuild therefore copies RCL and MudBlazor assets into `wwwroot/_content/...` in the app output, and `UseStaticFiles()` serves them.
- The Kestrel web root is explicitly set to `AppContext.BaseDirectory/wwwroot`, because the working directory can differ when starting from Visual Studio or the installer.
- `Directory.Build.targets` writes `BuildTimestampUtc` and `GitCommitSha` as assembly metadata. The status window reads these values from the actual app assembly; do not hardcode them there.
- Single-instance behavior uses a per-user named mutex and a per-user named pipe. A second process must not start a host; it may only request that the existing status window be opened.
- Tray commands change state only through the central `PlaybackController`; Chrome, power, and presenter side effects are orchestrated there in later phases and do not belong directly in the form.
- The status window always opens the management UI through loopback but shows all detected usable IPv4 addresses when LAN access is deliberately enabled. Chrome and presenter status are refreshed regularly from the central controllers, not inferred by the UI itself.
- Monitor identity is stored as the Windows `DeviceName`; friendly name, bounds, and primary status are resolved again on every load. If a saved device has disappeared, activating must not silently switch to another monitor.
- Start Chrome only through `IBrowserController` with the separate `ChromeProfile`. Pass kiosk, no-first-run, crash-bubble, and autoplay arguments, as well as the internal `/presenter` URL, through `ProcessStartInfo.ArgumentList`; do not concatenate shell strings.
- `PlaybackOrchestrator` is the only place that coordinates presenter states with Chrome, SignalR, and power requests. WinForms and later web commands call only its asynchronous methods; power requests must be released in Hidden and Stopped states.
- `PowerCreateRequest`, `PowerSetRequest`, and `PowerClearRequest` are imported from `kernel32.dll` at runtime. Do not confuse the native import library `PowrProf.lib` with a runtime DLL; the real Windows smoke test must cover both importing and releasing the requests.
- Autostart is registered under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. The value always contains the fully quoted executable path plus `--autostart`; this startup mode does not automatically open the status window.
- Serilog writes structured daily logs to `%LOCALAPPDATA%\BeamerPresenter\Presenter\Logs` and keeps at most 14 files. Passwords, cookies, tokens, and request bodies must never be logged.
- SQLite cannot translate `DateTimeOffset` into server-side `ORDER BY`. The small video list is therefore loaded first and then sorted in memory by `AddedAtUtc`; changes to this must pass the authenticated web-route test.
- Evolve database schemas only through EF Core migrations. `PresenterDatabase` performs a one-time baseline of older `EnsureCreated` databases to `InitialSchema`; preserve this compatibility with a real SQLite test.
- Before applying pending schema migrations, copy the SQLite database to `Backup/` with the SQLite backup API; keep at most seven migration backups.
- `PresenterBackupWorker` creates at most one consistent daily file named `presenter-daily-yyyyMMdd.db` at startup and then hourly, using the SQLite backup API. Write it under a temporary name first, then move it atomically; keep the seven newest daily files.
- Media folders are separate persistent entities with `NOCASE`-unique full paths. Uploads use the first enabled folder and must not fall back to the legacy `PresenterSettings.MediaFolder` field.
- `IMediaScanner` is the reliable source of the file inventory: an initial scan plus reconciliation every 30 minutes. The scanner ignores inaccessible paths and reparse points, records only `MediaFileSupport` extensions, and marks missing files instead of deleting records.
- `MediaFolderWatcher` is only the fast detection layer. It coalesces FileSystemWatcher events for one second and then always triggers a full `IMediaScanner` reconciliation; the scanner serializes parallel full scans.
- Check FFprobe candidates in this order: configured path, local `Tools` directory, `PATH`, known installation locations. A candidate is available only after a successful `ffprobe -version`. Automatic installation uses only the exact WinGet ID `Gyan.FFmpeg`.
- FFprobe returns JSON through the central `IFfprobeService`. Analysis state (`ProbeStatus`) and expected browser compatibility (`PlaybackStatus`) are separate persisted values; missing FFprobe must therefore not be stored as an incompatible medium.
- Disabled `VideoAsset` entries must not be scheduled automatically, started manually, or served through `/media/{id}`. Manual reanalysis resets analysis and playback status before requeueing; a local presenter error marks `PlaybackStatus` as `Failed`.
- `MediaProbeQueue` is a deduplicated `Channel` with exactly two consumers. Reconciliation and uploads enqueue only IDs and paths; FFprobe may start only after file size and modification time have been stable for two seconds. Save results only if the file remained unchanged during analysis.
- The media library remains server-renderable and searchable through the GET parameter `q`. `ProbeStatus` and `PlaybackStatus` must remain visually distinguishable; upload forms use Post/Redirect/Get and show the result on the management page.
- Media filters run server-side through `mediaStatus`. Derive “Last playback” and “Segments” from the persisted `PlaybackHistory`; do not create parallel UI state or misuse the FFprobe timestamp.
- `/media/{mediaId}` remains anonymous for the local presenter, serves only available database entries with Range support, and revalidates the canonical file path against enabled configured media folders on every request. Never expose direct file paths to the browser.
- The browser routinely cancels active `/media/{mediaId}` Range requests when seeking, stopping, or changing sources. Treat the resulting `OperationCanceledException` as a client abort (HTTP 499) only at the route boundary and only when `HttpContext.RequestAborted` is set; do not conceal database or other errors.
- `/presenter` uses its own layout-free fullscreen view and connects directly to `/hubs/presenter` through the locally served `presenter.js`. The client implements the SignalR JSON handshake without a CDN dependency, reconnects automatically, and reports Ready/Playing/Paused/Buffering/Ended/Error/Heartbeat.
- `MediaSegmentPlanner` first chooses a suitable video uniformly, then chooses its segment. Only actually played `ActualStart`/`ActualEnd` ranges exclude material; the two-second boundary tolerance must not create substantive overlaps. Randomness remains deterministic in tests through `IRandomSource`.
- Queue and playback history are persisted in SQLite through `IPlaybackStore`. `GetQueueAsync` returns only Pending/Playing entries ordered by `SortOrder`; due to SQLite's `DateTimeOffset` limitation, history is loaded and then sorted in memory. Planning parameters belong to the migrated `PresenterSettings` record.
- `PlaybackQueueService` serializes queue mutations, reserves planned segments against duplicate use, and leaves manual entries unchanged when automatically filling the queue. Only `PlaybackOrchestrator` may issue SignalR playback commands from it; before stopping, Play Now saves only the actual position.
- Moving and removing are allowed only for Pending entries. “Regenerate queue” skips only automatic Pending entries, preserves Playing and manual Pending entries, normalizes their order, and then fills the queue automatically.
- “Play next” converts the selected Pending entry to `ManualNext` and prioritizes it. “Play now” may start only Pending entries, writes the previous Playing entry to history as interrupted at its actual position, and loads the already planned source through `PlaybackOrchestrator`.
- Reset playback history through the serialized `PlaybackQueueService` as well. Queue and history changes from the Web UI remain POST routes and are never anonymous.
- Queue web actions remain authenticated POST routes. `PresenterConnectionState` accepts a terminal transition (`Ended`/`Error`) only once, so duplicate browser events do not skip two queue entries; progression is handled through `IPlaybackCommandService`.
- Accept YouTube input only through `YouTubeUrlParser` and immediately normalize it to an exactly eleven-character video ID and the stable key `youtube:<id>`. Reject foreign hosts, lookalike domains, and non-HTTP(S) schemes.
- External playback stores `MediaId = null` and the normalized `ExternalSourceKey` in queue and history. YouTube entries always need a positive own or maximum duration; without a known player duration, the next automatic insertion starts after the highest actually played or already reserved end for that source.
- Load the YouTube IFrame Player API in the presenter only when needed; after five seconds it must terminate with `Error`. Local playback must not acquire a network dependency; `Ended`, player errors, and API timeouts all advance through the same deduplicated hub path to the next queue position.
- Before fetching metadata, the management UI normalizes YouTube links through the authenticated `/api/youtube/reference` route. It loads the external IFrame API there only after “Load metadata”; a missing duration must leave the manual maximum duration as a working fallback.
- Persist news through `INewsService`. Title and text are required, non-permanent entries need a positive duration, and `ValidUntil` must follow `ValidFrom`; the three modes remain exactly `SplitScreen`, `Ticker`, and `Fullscreen`.
- News commands run only through `INewsCommandService`/`PlaybackOrchestrator`. Ticker and SplitScreen do not pause media; Fullscreen pauses once, displaces active overlay news, and after Hide restores playback at the same position before restoring the overlay. Cancel every scheduled Hide when replacing it.
- The news form distinguishes “Save only” from “Save and show now.” The combined path must persist first and then use the same `INewsCommandService` as the separate “Show now” action; feedback must clearly state whether the news was merely saved or actually displayed.
- `NewsSchedulingWorker` considers only news with at least one validity boundary and polls every five seconds. It stops only the news ID it is tracking; if that news is merely suspended beneath Fullscreen, remove it from the restoration slot. Manual news without a validity window remain independent of the scheduler.
- `PresenterWatchdog` monitors Chrome and a SignalR heartbeat no older than 15 seconds while active or paused. Reload the current queue entry only after a fresh connection; while Paused, always set `autoPlay = false`. Hidden and Stopped reset all recovery counters.
- If the reported Playing position does not advance for 15 seconds while active, the watchdog reloads exactly once. Another 15 seconds without progress marks the current queue entry as failed and advances. Paused never performs stall recovery. Restore `AlwaysOnTop` immediately if actually lost and additionally every 30 seconds, or every five seconds in aggressive mode.
- The management dashboard obtains live state only from the authenticated `/api/status` endpoint and polls every two seconds. Titles are resolved server-side from the queue entry currently marked Playing; FFprobe checks are cached for one minute, and scanner states come from the real `SqliteMediaScanner`.
- The authenticated management UI uses a menu: Dashboard `/`, Playback `/playback`, Media library `/media`, and News `/news`. POST actions always redirect to the appropriate area; login and `/presenter` use separate layouts without the management menu.
- `/health` remains anonymous, returns only coarse application/database status, and must reveal neither titles, file counts, nor internal error text. `/health/details`, like all presenter control commands, requires authentication.

## Workflow

- Before every commit, run `dotnet build BeamerPresenterForLanParties.slnx -c Release` and the relevant tests.
- `AuthenticatedWebRouteTests.Valid_login_renders_management_page` verifies the complete flow of valid login, authentication cookie, and management-page rendering with a temporary database.
- `BeamerPresenter.Browser.Tests` starts real Kestrel and Chromium. Before its first run, execute `pwsh tests/BeamerPresenter.Browser.Tests/bin/Release/net10.0/playwright.ps1 install chromium`; media and socket test doubles stay within the browser context and must not force product test hooks into `presenter.js`.
- Collect coverage in all test projects with `coverage.runsettings` and merge it by source line with `scripts/Assert-Coverage.ps1`. CI must fail below 80 percent; new exclusions are allowed only for generated or purely visual/composition-root code and must be justified in the README and review.
- NuGet versions belong only in `Directory.Packages.props`; do not add version attributes to project files. Keep all `packages.lock.json` files checked in. After intentional package changes, update them with `dotnet restore BeamerPresenterForLanParties.slnx --force-evaluate`; CI and releases use `--locked-mode`.
- `BeamerPresenter.Architecture.Tests` enforces the Clean Architecture references, production C# files under `src`, tests under `tests`, and use of the `.slnx` solution.
- Use small, complete Conventional Commit steps. After a completed releasable feature, run `dotnet versionize --workingDir src/BeamerPresenter.App --configDir ../..` so the app changelog includes all project directories.
- `versionize` manages the changelog, release commit, and tag. Do not edit changelog files manually.
- `v0.x` tags mark development states only and must not create a GitHub release. The release workflow accepts stable tags starting with `v1.0.0`.
- Before the first stable `v1.0.0` tag, manually accept AC-TOP-004 on the real projector alongside the existing competing presentation software; never treat this environment-dependent check as passed on the basis of unit, browser, or mock tests alone.
- When changing release files, keep the expected names `BeamerPresenter-<Version>-Setup.exe` and `BeamerPresenter-<Version>-win-x64-portable.zip`.
