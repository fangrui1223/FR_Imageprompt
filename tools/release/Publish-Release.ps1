param(
    [string]$Version = "2.0.0",
    [string]$OutputRoot = "publish",
    [switch]$InternalDiagnostics
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$dotnet = Join-Path $root ".dotnet\dotnet.exe"
$publishRoot = [IO.Path]::GetFullPath((Join-Path $root $OutputRoot))
$packageName = "FR_Imageprompt-$Version-win-x64"
$packageDirectory = Join-Path $publishRoot $packageName
$zipPath = Join-Path $publishRoot "$packageName.zip"
if (Test-Path -LiteralPath $packageDirectory) {
    throw "Release directory already exists. Choose a clean OutputRoot: $packageDirectory"
}
if (Test-Path -LiteralPath $zipPath) {
    throw "Release archive already exists. Choose a clean OutputRoot: $zipPath"
}
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

$env:APPDATA = Join-Path $root ".appdata"
$env:LOCALAPPDATA = Join-Path $root ".localappdata"
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnethome"
$env:NUGET_PACKAGES = Join-Path $root ".packages"

& $dotnet publish (Join-Path $root "src\PromptVault.App\PromptVault.App.csproj") `
    -c Release -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PromptVaultUserPackage=true -o $packageDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$executablePath = Join-Path $packageDirectory "FR_Imageprompt.exe"
$productVersion = (Get-Item -LiteralPath $executablePath).VersionInfo.ProductVersion
if ($productVersion -ne $Version) {
    throw "Published ProductVersion '$productVersion' does not match requested package version '$Version'."
}

$forbidden = Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object {
    $_.Extension -in @(".pdb", ".lib", ".exp", ".tmp") -or
    $_.Name -like "DirectML.Debug.*"
}
if ($forbidden) {
    throw "User release contains forbidden files: $($forbidden.FullName -join ', ')"
}

$manifestPath = Join-Path $packageDirectory "release-manifest.json"
$packagePrefix = $packageDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$files = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
    if (-not $_.FullName.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish file escaped the package directory: $($_.FullName)"
    }
    [PSCustomObject]@{
        path = $_.FullName.Substring($packagePrefix.Length).Replace("\", "/")
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        purpose = if ($_.Name -eq "FR_Imageprompt.exe") { "FR_Imageprompt application and bundled runtime" } else { "Required runtime asset" }
    }
})
$manifest = [ordered]@{
    product = "FR_Imageprompt"
    version = $Version
    runtime = "win-x64"
    selfContained = $true
    singleFile = $true
    generatedAtUtc = [DateTimeOffset]::UtcNow
    files = $files
    totalBytesBeforeManifest = ($files | Measure-Object -Property bytes -Sum).Sum
    forbiddenPatternsChecked = @("*.pdb", "*.lib", "*.exp", "*.tmp", "DirectML.Debug.*")
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Compress-Archive -LiteralPath $packageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
$report = [ordered]@{
    product = "FR_Imageprompt"
    version = $Version
    packageDirectory = $packageDirectory
    zipPath = $zipPath
    zipBytes = (Get-Item -LiteralPath $zipPath).Length
    zipSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    previousBaselineZipBytes = 94149582
    previousBaselineApplicationBytes = 236873990
    applicationBytes = (Get-Item -LiteralPath $executablePath).Length
}
$reportPath = Join-Path $publishRoot "$packageName-release-report.json"
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8

if ($InternalDiagnostics) {
    $internalDirectory = Join-Path $publishRoot "$packageName-internal-diagnostics"
    & $dotnet publish (Join-Path $root "src\PromptVault.App\PromptVault.App.csproj") `
        -c Debug -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=false -o $internalDirectory
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Compress-Archive -LiteralPath $internalDirectory `
        -DestinationPath (Join-Path $publishRoot "$packageName-internal-diagnostics.zip") `
        -CompressionLevel Optimal
}

$report | ConvertTo-Json -Depth 5
