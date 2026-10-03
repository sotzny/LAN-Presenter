param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$manifestPath = Join-Path $publishRoot 'update-manifest.json'
$managedFiles = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse |
    Where-Object { $_.FullName -ne $manifestPath } |
    ForEach-Object { [IO.Path]::GetRelativePath($publishRoot, $_.FullName).Replace('\', '/') } |
    Sort-Object)
foreach ($required in @('BeamerPresenter.App.exe', 'BeamerPresenter.Updater.exe')) {
    if ($managedFiles -notcontains $required) { throw "Missing release file: $required" }
}
@{ Version = $Version; Files = $managedFiles } | ConvertTo-Json -Depth 3 |
    Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
