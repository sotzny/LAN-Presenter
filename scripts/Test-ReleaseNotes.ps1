$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "presenter-release-notes-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $testRoot | Out-Null
$output = Join-Path $testRoot 'notes.md'
$fixture = Join-Path $testRoot 'changelog.md'
try {
    & "$PSScriptRoot/New-ReleaseNotes.ps1" -Version v1.2.1 -PreviousVersion v1.1.1 -OutputPath $output
    $notes = Get-Content -LiteralPath $output -Raw
    foreach ($expected in @('retain playback status', 'verified automatic updates', 'restore resume', 'compare/v1.1.1...v1.2.1')) {
        if (!$notes.Contains($expected)) { throw "Missing release change: $expected" }
    }
    if ($notes.Contains('allow authenticated media library') -or $notes.Contains('<a name=')) {
        throw 'Release notes include older changes or changelog anchors.'
    }

    # Include the final section at EOF, skip an empty version, and exclude a newer version.
    @'
Changelog
<a name="2.0.0"></a>
## [2.0.0](https://example.com/v2.0.0)
* Future change
<a name="1.1.0"></a>
## [1.1.0](https://example.com/v1.1.0)
<a name="1.0.0"></a>
## [1.0.0](https://example.com/v1.0.0)
### Features
* Erste Änderung mit Umlauten
'@ | Set-Content -LiteralPath $fixture -Encoding utf8NoBOM
    & "$PSScriptRoot/New-ReleaseNotes.ps1" -Version 1.1.0 -ChangelogPath $fixture -OutputPath $output
    $notes = Get-Content -LiteralPath $output -Raw
    if (!$notes.Contains('Erste Änderung mit Umlauten') -or $notes.Contains('Future change') -or $notes.Contains('<a name=')) {
        throw 'First-release notes do not respect version boundaries or UTF-8.'
    }

    foreach ($case in @(
        @{ Version = '3.0.0'; PreviousVersion = '0.0.0' },
        @{ Version = '1.1.0'; PreviousVersion = '1.0.0' },
        @{ Version = '1.0.0'; PreviousVersion = '1.1.0' }
    )) {
        $failed = $false
        try { & "$PSScriptRoot/New-ReleaseNotes.ps1" @case -ChangelogPath $fixture -OutputPath $output }
        catch { $failed = $true }
        if (!$failed) { throw "Invalid release range was accepted: $($case | ConvertTo-Json -Compress)" }
    }
    Write-Output 'Release-note checks passed.'
}
finally {
    foreach ($path in @($output, $fixture)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    Remove-Item -LiteralPath $testRoot
}
