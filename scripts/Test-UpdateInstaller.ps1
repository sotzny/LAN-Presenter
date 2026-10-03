param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
# Compile the supplied setup with this isolated AppId first; never change a real installation's registration.
$testAppId = '9A97E734-DDE7-471B-A119-A47A31B00D44'
$testRegistry = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$testAppId}_is1"
if (Test-Path -LiteralPath $testRegistry) { throw 'The isolated test AppId is already registered.' }
$setupFile = (Resolve-Path -LiteralPath $SetupPath).Path
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testRoot = Join-Path $tempRoot ('BeamerPresenter.InstallerTest-' + [Guid]::NewGuid().ToString('N'))
$targetDirectory = Join-Path $testRoot 'installed with spaces'
$otherDirectory = Join-Path $testRoot 'independent'
$oldProcess = $null
$otherProcess = $null
try {
    New-Item -ItemType Directory -Path $targetDirectory, $otherDirectory | Out-Null
    foreach ($directory in @($targetDirectory, $otherDirectory)) {
        Copy-Item -LiteralPath $env:ComSpec -Destination (Join-Path $directory 'BeamerPresenter.App.exe')
    }
    Set-Content -LiteralPath (Join-Path $targetDirectory 'own-file.txt') -Value 'keep'
    $oldProcess = Start-Process -FilePath (Join-Path $targetDirectory 'BeamerPresenter.App.exe') -ArgumentList '/d /c ping -t 127.0.0.1 >nul' -PassThru -WindowStyle Hidden
    $otherProcess = Start-Process -FilePath (Join-Path $otherDirectory 'BeamerPresenter.App.exe') -ArgumentList '/d /c ping -t 127.0.0.1 >nul' -PassThru -WindowStyle Hidden
    $setupProcess = Start-Process -FilePath $setupFile -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NORESTARTAPPLICATIONS', '/SP-', '/NOICONS', '/TASKS=', "/DIR=`"$targetDirectory`"") -PassThru -WindowStyle Hidden
    if (!$setupProcess.WaitForExit(120000)) { throw 'Test setup did not terminate within two minutes.' }
    if ($setupProcess.ExitCode -ne 0) { throw "Test setup failed with exit code $($setupProcess.ExitCode)." }
    $oldProcess.Refresh(); $otherProcess.Refresh()
    if (!$oldProcess.HasExited) { throw 'Setup did not terminate the legacy process.' }
    if ($otherProcess.HasExited) { throw 'Setup terminated the independent process.' }
    $expected = (Get-FileHash -LiteralPath (Join-Path $publishRoot 'BeamerPresenter.App.exe') -Algorithm SHA256).Hash
    $actual = (Get-FileHash -LiteralPath (Join-Path $targetDirectory 'BeamerPresenter.App.exe') -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw 'Installed executable differs from the release payload.' }
    if ((Get-Content -LiteralPath (Join-Path $targetDirectory 'own-file.txt')).Trim() -ne 'keep') { throw 'Setup changed an additional user file.' }
    if (!(Test-Path -LiteralPath $testRegistry)) { throw 'The setup used an unexpected AppId.' }
    Write-Host 'Installer smoke passed: legacy process stopped, independent process retained, payload verified.'
}
finally {
    foreach ($process in @($oldProcess, $otherProcess)) {
        if ($null -ne $process) {
            $process.Refresh()
            if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }
    $uninstaller = Join-Path $targetDirectory 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        $uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -PassThru -WindowStyle Hidden
        if (!$uninstallProcess.WaitForExit(30000) -or $uninstallProcess.ExitCode -ne 0) { throw 'Test uninstall failed.' }
    }
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if (!$resolvedTestRoot.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    if (Test-Path -LiteralPath $resolvedTestRoot) { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force }
}
