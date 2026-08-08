param(
    [Parameter(Mandatory = $true)]
    [string]$SettingsPath,
    [string]$OutputRoot = ".m6-isolated\publish-tradeoffs"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$dotnet = Join-Path $root ".dotnet\dotnet.exe"
$settings = [IO.Path]::GetFullPath((Join-Path $root $SettingsPath))
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputRoot))
$isolatedPrefix = [IO.Path]::GetFullPath((Join-Path $root ".m6-isolated")) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($isolatedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Tradeoff output must remain under .m6-isolated."
}
if (-not (Test-Path -LiteralPath $settings)) { throw "Settings file was not found: $settings" }
$settingsJson = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
$libraryRoot = [IO.Path]::GetFullPath([string]$settingsJson.LibraryRoot)
if (-not $libraryRoot.StartsWith($isolatedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The settings file must point to a synthetic library under .m6-isolated."
}
if ($settingsJson.CaptureListeningEnabled -ne $false) {
    throw "CaptureListeningEnabled must be false for isolated startup measurements."
}
if (Test-Path -LiteralPath $output) {
    throw "Tradeoff output already exists. Choose a clean OutputRoot: $output"
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

$env:APPDATA = Join-Path $root ".appdata"
$env:LOCALAPPDATA = Join-Path $root ".localappdata"
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnethome"
$env:NUGET_PACKAGES = Join-Path $root ".packages"

# ReadyToRun needs its runtime crossgen package in project.assets.json. Restore it
# explicitly so the comparison is reproducible from a clean checkout.
& $dotnet restore (Join-Path $root "src\PromptVault.App\PromptVault.App.csproj") `
    -r win-x64 -p:PublishReadyToRun=true `
    --configfile (Join-Path $root "NuGet.Config")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$variants = @(
    [PSCustomObject]@{ name = "multifile"; singleFile = $false; readyToRun = $false },
    [PSCustomObject]@{ name = "singlefile"; singleFile = $true; readyToRun = $false },
    [PSCustomObject]@{ name = "multifile-r2r"; singleFile = $false; readyToRun = $true },
    [PSCustomObject]@{ name = "singlefile-r2r"; singleFile = $true; readyToRun = $true }
)

function Measure-Launch {
    param(
        [string]$Executable,
        [string]$TracePath,
        [string]$ExtractPath
    )
    $env:PROMPTVAULT_DIAGNOSTICS = "1"
    $env:PROMPTVAULT_DIAGNOSTICS_PATH = $TracePath
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $ExtractPath
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Executable `
        -ArgumentList "--settings `"$settings`"" `
        -PassThru -WindowStyle Hidden
    $entry = $null
    try {
        while ($clock.Elapsed -lt [TimeSpan]::FromSeconds(20)) {
            if ($process.HasExited) {
                throw "Application exited before first content was ready. Exit code: $($process.ExitCode)"
            }
            if (Test-Path -LiteralPath $TracePath) {
                $lines = @(Get-Content -LiteralPath $TracePath -ErrorAction SilentlyContinue)
                foreach ($line in $lines) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    try {
                        $candidate = $line | ConvertFrom-Json
                        if ($candidate.name -eq "gallery-first-content-ready") { $entry = $candidate }
                    } catch {
                    }
                }
                if ($null -ne $entry) { break }
            }
            Start-Sleep -Milliseconds 50
        }
        if ($null -eq $entry) { throw "Timed out waiting for gallery-first-content-ready." }
        return [PSCustomObject]@{
            wallClockMs = [Math]::Round($clock.Elapsed.TotalMilliseconds, 3)
            processElapsedMs = [double]$entry.processElapsedMs
            extractionFiles = if (Test-Path -LiteralPath $ExtractPath) {
                @(Get-ChildItem -LiteralPath $ExtractPath -File -Recurse).Count
            } else { 0 }
            launchSucceeded = $true
        }
    } finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }
}

$results = @()
foreach ($variant in $variants) {
    $variantRoot = Join-Path $output $variant.name
    $publish = Join-Path $variantRoot "app"
    New-Item -ItemType Directory -Force -Path $variantRoot | Out-Null
    & $dotnet publish (Join-Path $root "src\PromptVault.App\PromptVault.App.csproj") `
        -c Release -r win-x64 --self-contained true --no-restore `
        -p:DefineConstants=PROMPTVAULT_PERF_DIAGNOSTICS `
        "-p:PublishSingleFile=$($variant.singleFile.ToString().ToLowerInvariant())" `
        "-p:IncludeNativeLibrariesForSelfExtract=$($variant.singleFile.ToString().ToLowerInvariant())" `
        "-p:PublishReadyToRun=$($variant.readyToRun.ToString().ToLowerInvariant())" `
        -p:PromptVaultUserPackage=true -o $publish
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $zip = Join-Path $variantRoot "$($variant.name).zip"
    Compress-Archive -LiteralPath $publish -DestinationPath $zip -CompressionLevel Optimal
    $samples = @()
    for ($index = 0; $index -lt 4; $index++) {
        $trace = Join-Path $variantRoot "startup-$index.jsonl"
        $extract = Join-Path $variantRoot "extract-$index"
        $samples += Measure-Launch `
            -Executable (Join-Path $publish "FR_Imageprompt.exe") `
            -TracePath $trace `
            -ExtractPath $extract
    }
    $files = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
    $results += [PSCustomObject]@{
        name = $variant.name
        singleFile = $variant.singleFile
        readyToRun = $variant.readyToRun
        fileCount = $files.Count
        applicationBytes = ($files | Measure-Object -Property Length -Sum).Sum
        zipBytes = (Get-Item -LiteralPath $zip).Length
        coldFirstContentMs = $samples[0].processElapsedMs
        hotFirstContentP50Ms = [Math]::Round((($samples | Select-Object -Skip 1 | Sort-Object processElapsedMs)[1]).processElapsedMs, 3)
        maximumExtractionFiles = ($samples | Measure-Object -Property extractionFiles -Maximum).Maximum
        launches = $samples
        antivirusObservation = "No launch block or quarantine was observed during four local starts; the unsigned build can still trigger reputation warnings on other machines."
    }
}

$single = $results | Where-Object { $_.name -eq "singlefile" }
$multi = $results | Where-Object { $_.name -eq "multifile" }
$singleStartupPenalty = if ($multi.hotFirstContentP50Ms -le 0) {
    0
} else {
    [Math]::Round(($single.hotFirstContentP50Ms - $multi.hotFirstContentP50Ms) / $multi.hotFirstContentP50Ms * 100, 2)
}
$decision = if ($singleStartupPenalty -le 20 -and $single.zipBytes -le $multi.zipBytes * 1.1) {
    "Keep ordinary self-contained single-file without ReadyToRun: its startup penalty is within 20%, distribution remains one executable, and ReadyToRun size is avoided."
} else {
    "Use ordinary self-contained multi-file: measured single-file startup or compressed-size cost exceeded the release threshold."
}
$report = [ordered]@{
    milestone = "M6-05"
    measuredAtUtc = [DateTimeOffset]::UtcNow
    settingsSafety = [ordered]@{
        settingsPath = "[explicit synthetic settings]"
        libraryKind = "synthetic"
        captureListeningEnabled = $false
    }
    variants = $results
    singleFileHotStartupPenaltyPercent = $singleStartupPenalty
    decision = $decision
    extractionNote = "DOTNET_BUNDLE_EXTRACT_BASE_DIR was isolated per launch; extractionFiles records bundle extraction behavior."
    antivirusScope = "Local launch observation only; no claim is made for third-party antivirus reputation systems."
}
$reportPath = Join-Path $output "publish-tradeoff-report.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
$report | ConvertTo-Json -Depth 8
