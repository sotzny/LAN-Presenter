# Beamer Presenter for LAN-Parties

**Sprache:** Deutsch · [English](README.en.md)

[![CI](https://github.com/sotzny/LAN-Presenter/actions/workflows/ci.yml/badge.svg)](https://github.com/sotzny/LAN-Presenter/actions/workflows/ci.yml)
[![Windows](https://img.shields.io/badge/platform-Windows%20x64-0078D4)](https://github.com/sotzny/LAN-Presenter)

**Videos für den Beamer automatisch abspielen und die Wiedergabe bequem vom Browser aus steuern.**

Beamer Presenter ist eine Windows-Desktopanwendung für LAN-Parties und andere Veranstaltungen. Sie verwaltet eine lokale Videobibliothek, stellt daraus eine abwechslungsreiche Wiedergabe-Queue zusammen und zeigt die Clips im Chrome-Kioskmodus auf dem ausgewählten Beamer. Ein passwortgeschütztes Webinterface bietet die Steuerung und Verwaltung – am Rechner selbst oder auf einem Gerät im freigegebenen LAN.

## Warum Beamer Presenter?

Bei einer Veranstaltung soll der Beamer Inhalte zeigen, ohne dass jemand fortlaufend Videos auswählen und weiterschalten muss. Beamer Presenter übernimmt diese Routine: Es findet Videos in den konfigurierten Ordnern, prüft ihre Abspielbarkeit und plant Clips automatisch ein. Lange Videos können in wechselnden Ausschnitten laufen; Verlauf und Segmentplanung helfen dabei, nicht immer dieselben Stellen zu zeigen.

Die Wiedergabe lässt sich jederzeit pausieren, ausblenden, fortsetzen oder beenden. Für kurzfristige Ansagen können News als Lauftext, geteilter Bildschirm oder Vollbild eingeblendet werden.

## Verwaltungs-API

Im Menü **Developer** lässt sich ein API-Key erzeugen, selbst setzen, ersetzen oder deaktivieren. Ein erzeugter Key wird einmalig angezeigt. Gespeichert wird ausschließlich ein gesalzener PBKDF2-SHA512-Hash; eigene Keys benötigen 32 bis 512 Zeichen ohne Leer- oder Steuerzeichen. Ersetzen und Deaktivieren wirken sofort, auch ohne Neustart.

Die JSON-API unter `/api/v1` bietet die Web-Funktionen für News, Presenter, Wiedergabe, Queue, Historie, Medien (einschließlich Upload) und YouTube. Jeder Aufruf benötigt den Header `X-API-Key`. Der Key erlaubt weder die Web-Anmeldung noch die Key-Verwaltung oder Software-Updates. Die API bleibt ohne eingerichteten Key gesperrt.

**Swagger** unter `/swagger` verlangt die Web-Anmeldung. Über **Authorize** wird der API-Key für Testaufrufe eingetragen, ohne ihn dauerhaft im Browser zu speichern. Die **OpenAPI-Definition** unter `/openapi/v1.json` akzeptiert Web-Anmeldung oder API-Key. Swagger und seine Assets werden lokal bereitgestellt.

```powershell
curl.exe -H "X-API-Key: <API-KEY>" http://127.0.0.1:8765/api/v1/news
curl.exe -X POST -H "X-API-Key: <API-KEY>" http://127.0.0.1:8765/api/v1/news/1/show
```

News werden mit `POST /api/v1/news` erstellt und mit `PUT /api/v1/news/{id}` bearbeitet. Beispiel:

```json
{
  "title": "Turnierstart",
  "text": "Bitte zur Turnierleitung kommen.",
  "mode": "Ticker",
  "durationSeconds": 120,
  "permanent": false,
  "priority": 0,
  "showNow": true
}
```

Zeitspannen und Positionen verwenden Sekunden, Zeitpunkte ISO 8601 mit Zeitzone, Modi benannte Werte (`Ticker`, `SplitScreen`, `Fullscreen`). `showNow` ist standardmäßig `false`. **Nur speichern** ersetzt keine laufende Anzeige; **Speichern & jetzt anzeigen** startet bei erfolgreicher Übernahme die Anzeigedauer neu. Geänderte Gültigkeitsfenster berücksichtigt der Scheduler weiterhin. Antworten unterscheiden `saved`, `displayRequested` und `displayed`: Eine durch Priorität oder Fullscreen verdrängte News ist nicht sichtbar. Scheitert das Anzeigen nach dem Speichern, enthält die HTTP-503-Problemantwort weiterhin `saved: true` und den gespeicherten News-Datensatz. Der Aufrufer kann anhand der ID gezielt erneut anzeigen, ohne die News nochmals anzulegen.

API-Fehler liefern Problem Details und HTTP-Statuscodes, keine Login-Weiterleitungen oder internen Fehlertexte. Medienupload verwendet Multipart mit dem Feld `video` und dieselben Grenzen und Prüfungen wie die Webseite. Normales Desktop-Speichern überschreibt den API-Key nicht.

Der Server bindet weiterhin standardmäßig an Loopback. LAN-Zugriff muss bewusst in den Desktop-Einstellungen aktiviert werden; Presenter-Ressourcen bleiben ausschließlich lokal. Über HTTP wird der Key unverschlüsselt übertragen. CORS bleibt deaktiviert.

## Funktionen

- **Automatische Video-Queue:** lokale Videos werden erkannt, analysiert und nach konfigurierbaren Regeln für die Wiedergabe eingeplant.
- **Kontrolle über den Browser:** Queue, Verlauf, Bibliothek, News und Presenter-Zustand sind in getrennten Verwaltungsbereichen erreichbar.
- **Gezielte Wiedergabe:** Clips manuell sofort starten, als Nächstes einreihen, verschieben oder aus der Queue entfernen.
- **YouTube-Import:** gültige YouTube-Links werden lokal geladen und nach der Medienanalyse in die Queue übernommen. Ganze Videos oder ausgewählte Segmente sind möglich.
- **News-Einblendungen:** Meldungen als Ticker, 50:50-Split-Screen oder Vollbild erstellen, bearbeiten, planen, priorisieren und anzeigen.
- **Beamer-Steuerung:** Chrome startet im Kioskmodus auf dem gewählten Monitor. Aktivieren, Pause, Ausblenden und Stop lassen sich zentral steuern.
- **Übersicht und Wiederherstellung:** Dashboard mit Live-Status, Medienanalyse und Scannerzustand; automatische Erholung bei Browser- oder Wiedergabeproblemen.
- **Beamer-Vorschau:** Das Dashboard zeigt den gesamten ausgewählten Beamer-Monitor, einschließlich überdeckender Fenster. Umschaltbar zwischen einem Screenshot alle zehn Sekunden und Live mit einem Bild pro Sekunde, ohne Audio. Die Vorschau benötigt eine Anmeldung und pausiert bei verborgenem Browser-Tab; Aufnahmen bleiben ausschließlich im Arbeitsspeicher.
- **Lokale Datenhaltung:** Einstellungen, Bibliothek, Queue, Verlauf, Logs und Backups liegen im Benutzerprofil unter `%LOCALAPPDATA%\BeamerPresenter\Presenter`.
- **Drei Sprachen:** Desktop-App, Webverwaltung und Beamer-Anzeige stehen auf Deutsch, Englisch und Spanisch zur Verfügung.

Unterstützte lokale Videoformate: `.mp4`, `.m4v`, `.mkv`, `.webm`, `.avi` und `.mov`.

Fehlende Dateien werden beim Medienabgleich automatisch aus der Mediathek entfernt, sofern ihr aktiver Ordner vollständig durchsuchbar und weiterhin erreichbar ist. Bei abgezogenen Wechseldatenträgern oder Zugriffsproblemen bleiben die Einträge erhalten. Der Filter **Fehlt** sowie **Eintrag bereinigen** und **Alle fehlenden Einträge bereinigen** ermöglichen die manuelle Bereinigung auch solcher Einträge. Die Gesamtaktion gilt für die gesamte Mediathek unabhängig vom Suchfilter. Vorhandene Dateien und die Wiedergabehistorie bleiben erhalten.

## Updates und Beenden

Desktop-Statusfenster, Tray und die Web-Seite **„Updates“** zeigen verfügbare Updates, Downloadfortschritt und Installationszeitpunkt. Ein kleiner roter Punkt am Web-Menüeintrag weist auf eine verfügbare Version hin und bleibt auch nach dem Öffnen der Seite sichtbar. Die Anwendung prüft bei jedem Start sofort im Hintergrund und danach alle sechs Stunden auf stabile GitHub-Releases – auch beim ersten Start und beim Autostart, unabhängig von der Web-Anmeldung. Automatische Updates sind standardmäßig aktiviert: Nach vollständig geprüftem Download beginnt ein fünfminütiger Countdown, auch bei laufender Wiedergabe.

Mit **„Sofort“** startet die Installation ohne Countdown; **„1 Stunde später“** verschiebt sie. **„Automatische Updates“** kann dauerhaft abgeschaltet werden; manuelle Prüfung und Installation bleiben möglich. Die Webaktionen verlangen Anmeldung und Antiforgery-Schutz.

Setup und portable Ausgabe werden am bisherigen Ort aktualisiert. Downloads werden anhand der GitHub-SHA-256-Prüfsumme geprüft. Einstellungen, Medien und Datenbank bleiben im Benutzerprofil erhalten. Portable Updates sichern verwaltete Programmdateien und stellen sie bei einem Austauschfehler wieder her; eigene zusätzliche Dateien werden nicht gelöscht.

Die Anwendung beendet vor der Installation Browser und Webserver. Ein hängender Presenter-Prozess wird gezielt beendet; das Setup ersetzt keine Dateien, solange der Prozess noch läuft. Nach einem erfolgreichen Update startet die Anwendung einmal neu und stellt Wiedergabemodus, zuletzt bestätigte Position und noch gültige News wieder her. Fehlen Medium oder Monitor, bleibt die Wiedergabe gestoppt und zeigt einen Fehler.

**Die erste Ausgabe mit Update-Funktion muss einmal regulär per Setup oder ZIP eingespielt werden.** Bereits installierte ältere Ausgaben können diese Funktion noch nicht selbst nachladen. Netzwerk- und Prüffehler unterbrechen die Wiedergabe nicht; eine fehlgeschlagene Installation wird angezeigt und nicht unmittelbar automatisch wiederholt.

„Beenden“ im Tray beendet den Prozess vollständig. Das Schließen des Statusfensters mit dem Fensterkreuz versteckt es weiterhin nur.

## Voraussetzungen

- Windows x64
- Google Chrome
- Für automatische Medienanalyse: FFprobe (FFmpeg). Die Anwendung kann eine vorhandene Installation finden; alternativ lässt sich der Pfad angeben oder FFmpeg über WinGet installieren.
- Für den YouTube-Import wird `yt-dlp` benötigt. Die Anwendung verwaltet das benötigte Werkzeug und speichert geladene Videos im ersten aktiven Medienordner.

## Installation und erster Start

Die stabilen Releases veröffentlichen einen Installer und eine portable Ausgabe. Öffne [Releases](https://github.com/sotzny/LAN-Presenter/releases), lade eine der Dateien herunter und starte die Anwendung:

- `BeamerPresenter-<Version>-Setup.exe` – Installation ohne Administratorrechte
- `BeamerPresenter-<Version>-win-x64-portable.zip` – portable Ausgabe zum Entpacken und Starten

Der Release-Workflow veröffentlicht stabile Versionen ab `v1.0.0`. Entwicklungs-Tags der Reihe `v0.x` erzeugen keinen GitHub-Release.

Jeder GitHub-Release enthält die Änderungen aus dem von `versionize` erzeugten Changelog seit dem vorherigen veröffentlichten Release. Dabei werden auch Zwischenversionen ohne eigenen GitHub-Release berücksichtigt. Fehlen Changelog-Einträge oder enthält der Zeitraum keine Änderungen, bricht die Veröffentlichung ab.

Beim ersten Start:

1. Einen oder mehrere Medienordner festlegen.
2. Ein Web-Passwort setzen.
3. Den Zielmonitor auswählen und bei Bedarf Chrome- oder FFprobe-Pfade konfigurieren.
4. Die Weboberfläche auf dem lokalen Rechner unter [http://localhost:8765](http://localhost:8765) öffnen und anmelden.

Aktiviere „Web UI im LAN freigeben“ in der Desktop-Anwendung, wenn du von einem anderen Gerät im Netzwerk steuern möchtest. Speichere die Einstellungen und starte die Anwendung neu, damit die LAN-Freigabe und ein geänderter Port wirksam werden.

Die Zeile „Windows-Firewall“ prüft den eingetragenen Web-Port automatisch und zeigt den Status für private, öffentliche und Domänennetzwerke. Mit „Erneut prüfen“ kannst du den Status aktualisieren. „In Firewall freigeben“ öffnet den Windows-Administrator-Dialog und erstellt oder aktualisiert eine eigene Regel: eingehendes TCP auf diesem Port, nur für diese Anwendung und Teilnehmer im lokalen Subnetz, in allen drei Netzwerkprofilen. Die Aktion speichert keine Einstellungen und startet die Anwendung nicht neu. Vorhandene passende Regeln werden erkannt; fremde Regeln und Firewall-Profile werden nicht verändert.

Eine Blockregel oder Unternehmensrichtlinie kann die Freigabe verhindern; eingeschränkte oder nicht eindeutig auswertbare Regeln werden entsprechend angezeigt. „Firewall deaktiviert“ bedeutet keine bestätigte Regel-Freigabe. Die Prüfung betrifft die Windows-Firewall; die tatsächliche Erreichbarkeit von einem anderen Gerät muss im jeweiligen Netzwerk geprüft werden. Nach einem Wechsel des Installationspfads die Freigabe erneut prüfen.

Unter „Sprache“ kannst du „Systemstandard“, Deutsch, Englisch oder Spanisch auswählen. Beim Systemstandard verwendet die App beim Start die Windows-Sprache; ist sie nicht verfügbar, erscheint Englisch. Die Auswahl gilt auch für die Weboberfläche auf anderen Geräten und für die Beamer-Anzeige. Sprachänderungen werden nach einem Neustart wirksam.

## Sicherheit und Netzwerk

Die Weboberfläche bindet standardmäßig nur an `127.0.0.1`. Die LAN-Freigabe muss bewusst in der Desktop-Anwendung aktiviert werden. Verwaltungsseiten und Steuerbefehle erfordern ein Passwort und ein Anmelde-Cookie. Das Passwort wird mit PBKDF2-SHA512 und individuellem Salt gespeichert.

Die Kioskseite des Beamers, der Presenter-Hub und die Medienauslieferung bleiben auf lokale Verbindungen beschränkt. Sie sind nicht für LAN-Teilnehmer freigegeben. Logs enthalten keine Passwörter, Cookies oder Request-Bodies.

## Entwicklung

Benötigt werden das .NET 10 SDK und unter Windows eine passende Entwicklungsumgebung für WinForms.

```powershell
git clone https://github.com/sotzny/LAN-Presenter.git
cd LAN-Presenter
dotnet tool restore
dotnet restore BeamerPresenterForLanParties.slnx --locked-mode
dotnet build BeamerPresenterForLanParties.slnx -c Release
dotnet run --project src/BeamerPresenter.App
```

Die Weboberfläche ist anschließend unter `http://localhost:8765` erreichbar. Einstellungen und Daten liegen im lokalen Benutzerprofil, nicht im Repository.

## Tests und Qualitätssicherung

Die CI baut die Solution, prüft das Format und führt die Tests inklusive Browser-Tests mit Chromium aus. Die Coverage-Grenze von 80 Prozent wird für Produktcode ermittelt: Coverlet misst C# und die Chromium-Browser-Tests schreiben Line Coverage für die ausgelieferten JavaScript-Dateien als LCOV. EF-Migrationen, generierte Dateien, der Composition Root, das WinForms-Formular und Razor-Markup sind von der zeilenbasierten Coverage ausgenommen; ihr Verhalten wird durch Migration-, Architektur-, Browser- und Route-Tests geprüft. SonarQube verwendet dieselben C#-Coverage-Ausnahmen und importiert zusätzlich den Browser-LCOV-Bericht.

```powershell
# Browser-Tests benötigen Chromium; zuerst die Solution bauen und dann den Browser installieren.
dotnet build BeamerPresenterForLanParties.slnx -c Release
pwsh tests/BeamerPresenter.Browser.Tests/bin/Release/net10.0/playwright.ps1 install chromium

# Gesamte Testsuite
dotnet test BeamerPresenterForLanParties.slnx -c Release
```

Die Beamer-Vorschau zusätzlich unter Windows am echten Zielmonitor abnehmen: Video und News mit dem sichtbaren Monitorbild vergleichen, ein anderes Fenster darüberlegen und Pause/Hide/Stop prüfen. Zwischen zehn Sekunden und Live wechseln; Live soll ungefähr ein neues Bild pro Sekunde liefern. Zielmonitor abziehen: Die Vorschau muss eine Fehlermeldung anzeigen und darf nicht auf einen anderen Bildschirm wechseln.

Nach Änderungen an Paketversionen die Lockfiles aktualisieren:

```powershell
dotnet restore BeamerPresenterForLanParties.slnx --force-evaluate
```

## Architektur

Die Anwendung besteht aus einer WinForms-Desktop-App und einer im selben Prozess gestarteten ASP.NET-Core-Weboberfläche. Die Desktop-App ist der Composition Root. Die Fachlogik ist in Domain und Application getrennt; Infrastructure stellt unter anderem SQLite-Persistenz und Medienanalyse bereit, Web enthält Oberfläche und HTTP-Endpunkte.

```text
src/
├── BeamerPresenter.Domain         Modelle
├── BeamerPresenter.Application    Fachlogik und Contracts
├── BeamerPresenter.Infrastructure SQLite, Scanner und Medienwerkzeuge
├── BeamerPresenter.Web            Management-UI, Presenter und Endpunkte
└── BeamerPresenter.App            WinForms-Host und Composition Root
```

## Versionierung und Beiträge

Das Projekt verwendet Conventional Commits. Versionen und Changelog werden mit dem festgeschriebenen `versionize`-Tool verwaltet:

```powershell
dotnet tool restore
dotnet versionize --workingDir src/BeamerPresenter.App --configDir ../..
```

Bitte vor einem Beitrag die Architektur- und Sicherheitsregeln in [AGENTS.md](AGENTS.md) beachten. Änderungen an Datenbankschemata erfolgen über EF-Core-Migrationen.

## Screenshots

Screenshots der Verwaltungsoberfläche sind in dieser Repository-Version noch nicht als Dateien abgelegt. Die Seiten sind passwortgeschützt; für aussagekräftige Projektbilder sollten Dashboard, Queue und Mediathek mit bereinigten Beispieldaten aufgenommen und anschließend hier ergänzt werden.
