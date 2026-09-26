# Beamer Presenter for LAN Parties

**Language:** [Deutsch](README.md) · English

[![CI](https://github.com/sotzny/LAN-Presenter/actions/workflows/ci.yml/badge.svg)](https://github.com/sotzny/LAN-Presenter/actions/workflows/ci.yml)
[![Windows](https://img.shields.io/badge/platform-Windows%20x64-0078D4)](https://github.com/sotzny/LAN-Presenter)

**Play videos on a projector automatically and control playback from a browser.**

Beamer Presenter is a Windows desktop application for LAN parties and other events. It manages a local video library, builds a varied playback queue, and shows clips in Chrome kiosk mode on the selected projector. A password-protected web interface provides control and management from the same computer or another device on the permitted LAN.

## Why Beamer Presenter?

At an event, the projector should keep showing content without someone continuously selecting and advancing videos. Beamer Presenter handles that routine: it finds videos in configured folders, checks whether they can be played, and schedules clips automatically. Long videos can play in changing segments; playback history and segment planning help avoid showing the same parts repeatedly.

Playback can be paused, hidden, resumed, or stopped at any time. Announcements can appear as a ticker, split screen, or fullscreen overlay.

## Features

- **Automatic video queue:** Local videos are discovered, analyzed, and scheduled according to configurable rules.
- **Browser control:** Queue, history, library, news, and presenter status have separate management pages.
- **Direct playback control:** Start a clip immediately, queue it next, move it, or remove it from the queue.
- **YouTube import:** Valid YouTube links are downloaded locally and added to the queue after media analysis. Whole videos or selected segments can be played.
- **News overlays:** Create, schedule, prioritize, and display announcements as a ticker, 50:50 split screen, or fullscreen overlay.
- **Projector control:** Chrome starts in kiosk mode on the selected monitor. Activation, pause, hide, and stop are managed centrally.
- **Monitoring and recovery:** A dashboard shows live status, media analysis, and scanner state; the app recovers automatically from browser or playback problems.
- **Local data storage:** Settings, library, queue, history, logs, and backups are stored in the user profile under `%LOCALAPPDATA%\BeamerPresenter\Presenter`.
- **Three languages:** The desktop app, web management interface, and projector display are available in German, English, and Spanish.

Supported local video formats: `.mp4`, `.m4v`, `.mkv`, `.webm`, `.avi`, and `.mov`.

## Requirements

- Windows x64
- Google Chrome
- FFprobe (FFmpeg) for automatic media analysis. The app can detect an existing installation; alternatively, specify its path or install FFmpeg through WinGet.
- `yt-dlp` for YouTube imports. The app manages the required tool and stores downloaded videos in the first enabled media folder.

## Installation and first run

Stable releases provide an installer and a portable package. Open [Releases](https://github.com/sotzny/LAN-Presenter/releases), download one of these files, and start the app:

- `BeamerPresenter-<Version>-Setup.exe` — installs without administrator rights
- `BeamerPresenter-<Version>-win-x64-portable.zip` — unpack and run the portable package

The release workflow publishes stable versions starting with `v1.0.0`. Development tags in the `v0.x` series do not create a GitHub release.

On first run:

1. Configure one or more media folders.
2. Set a web password.
3. Select the target monitor and, if needed, configure the Chrome or FFprobe paths.
4. Open the web interface at [http://localhost:8765](http://localhost:8765) on the local computer and sign in.

Enable “Allow Web UI on LAN” in the desktop app if you want to control it from another device on the network. The change takes effect after a restart; you may also need to allow the web port through Windows Firewall.

Under “Language,” select “System default,” German, English, or Spanish. With the system default, the app uses the Windows language at startup and falls back to English if that language is unavailable. The choice also applies to the web interface on other devices and to the projector display. Language changes take effect after a restart.

## Security and network access

By default, the web interface listens only on `127.0.0.1`. LAN access must be enabled deliberately in the desktop app. Management pages and control commands require a password and an authentication cookie. The password is stored using PBKDF2-SHA512 with an individual salt.

The projector's kiosk page, presenter hub, and media delivery remain restricted to local connections. They are not exposed to LAN clients. Logs do not contain passwords, cookies, or request bodies.

## Development

You need the .NET 10 SDK and a suitable Windows development environment for WinForms.

```powershell
git clone https://github.com/sotzny/LAN-Presenter.git
cd LAN-Presenter
dotnet tool restore
dotnet restore BeamerPresenterForLanParties.slnx --locked-mode
dotnet build BeamerPresenterForLanParties.slnx -c Release
dotnet run --project src/BeamerPresenter.App
```

The web interface is then available at `http://localhost:8765`. Settings and data are stored in the local user profile, outside the repository.

## Tests and quality checks

CI builds the solution, checks formatting, and runs the tests, including Chromium browser tests. The 80 percent coverage threshold is measured for product code: Coverlet measures C#, and the Chromium browser tests write line coverage for the shipped JavaScript files as LCOV. EF migrations, generated files, the composition root, the WinForms form, and Razor markup are excluded from line coverage; their behavior is checked by migration, architecture, browser, and route tests. SonarQube uses the same C# coverage exclusions and also imports the browser LCOV report.

```powershell
# Browser tests need Chromium. Build the solution first, then install the browser.
dotnet build BeamerPresenterForLanParties.slnx -c Release
pwsh tests/BeamerPresenter.Browser.Tests/bin/Release/net10.0/playwright.ps1 install chromium

# Full test suite
dotnet test BeamerPresenterForLanParties.slnx -c Release
```

After changing package versions, update the lock files:

```powershell
dotnet restore BeamerPresenterForLanParties.slnx --force-evaluate
```

## Architecture

The application consists of a WinForms desktop app and an ASP.NET Core web interface running in the same process. The desktop app is the composition root. Business logic is separated into Domain and Application; Infrastructure provides SQLite persistence and media analysis, among other services, while Web contains the interface and HTTP endpoints.

```text
src/
├── BeamerPresenter.Domain         Models
├── BeamerPresenter.Application    Business logic and contracts
├── BeamerPresenter.Infrastructure SQLite, scanner, and media tools
├── BeamerPresenter.Web            Management UI, presenter, and endpoints
└── BeamerPresenter.App            WinForms host and composition root
```

## Versioning and contributions

The project uses Conventional Commits. Versions and the changelog are managed with the pinned `versionize` tool:

```powershell
dotnet tool restore
dotnet versionize --workingDir src/BeamerPresenter.App --configDir ../..
```

Before contributing, read the architecture and security rules in [AGENTS.en.md](AGENTS.en.md). Database schema changes must use EF Core migrations.

## Screenshots

Screenshots of the management interface are not yet stored as files in this repository version. The pages are password-protected; useful project screenshots should show the dashboard, queue, and media library with sanitized sample data before being added here.
