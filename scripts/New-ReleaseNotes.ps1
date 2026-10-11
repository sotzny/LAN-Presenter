param(
    [Parameter(Mandatory)][ValidatePattern('^v?\d+\.\d+\.\d+$')][string]$Version,
    [ValidatePattern('^v?\d+\.\d+\.\d+$')][string]$PreviousVersion = '0.0.0',
    [string]$ChangelogPath = "$PSScriptRoot/../src/BeamerPresenter.App/CHANGELOG.md",
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$Repository = 'sotzny/LAN-Presenter'
)
$ErrorActionPreference = 'Stop'
$current = [version]$Version.TrimStart('v')
$previous = [version]$PreviousVersion.TrimStart('v')
if ($previous -ge $current) { throw 'Previous version must be older than the release version.' }

# Versionize places an HTML anchor before each heading; it is not part of the notes.
$changelog = (Get-Content -LiteralPath $ChangelogPath -Raw) -replace '(?m)^<a name="[^"]+"></a>\r?\n', ''
$headings = [regex]::Matches($changelog, '(?m)^## \[(?<Version>\d+\.\d+\.\d+)\][^\r\n]*')
if ($current.ToString() -notin @($headings | ForEach-Object { $_.Groups['Version'].Value })) {
    throw "Release version $current was not found in the changelog."
}

$sections = @(for ($index = 0; $index -lt $headings.Count; $index++) {
    $heading = $headings[$index]
    $sectionVersion = [version]$heading.Groups['Version'].Value
    if ($sectionVersion -le $previous -or $sectionVersion -gt $current) { continue }
    $bodyStart = $heading.Index + $heading.Length
    $bodyEnd = if ($index + 1 -lt $headings.Count) { $headings[$index + 1].Index } else { $changelog.Length }
    $body = $changelog.Substring($bodyStart, $bodyEnd - $bodyStart).Trim()
    if ($body) { $heading.Value + "`n`n" + $body }
})
if ($sections.Count -eq 0) { throw "No changes found for release $current after $previous." }

$notes = $sections -join "`n`n"
if ($previous -gt [version]'0.0.0') {
    $notes += "`n`n**Full Changelog**: https://github.com/$Repository/compare/v$previous...v$current"
}
Set-Content -LiteralPath $OutputPath -Value $notes -Encoding utf8NoBOM
