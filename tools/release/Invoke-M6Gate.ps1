param(
    [string]$OutputDirectory = "artifacts\m6",
    [string]$FixedLibrary = ".m6-isolated\benchmark-30000",
    [string]$UiTrace = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$dotnet = Join-Path $root ".dotnet\dotnet.exe"
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$fixed = [IO.Path]::GetFullPath((Join-Path $root $FixedLibrary))
New-Item -ItemType Directory -Force -Path $output | Out-Null
New-Item -ItemType Directory -Force -Path $fixed | Out-Null

$env:APPDATA = Join-Path $root ".appdata"
$env:LOCALAPPDATA = Join-Path $root ".localappdata"
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnethome"
$env:NUGET_PACKAGES = Join-Path $root ".packages"

& $dotnet test (Join-Path $root "tests\PromptVault.Tests\PromptVault.Tests.csproj") -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$benchmark = Join-Path $output "benchmark-30000.json"
& $dotnet run --project (Join-Path $root "tools\PromptVault.Benchmark\PromptVault.Benchmark.csproj") -c Release --no-restore -- --counts 30000 --iterations 12 --fixed-root $fixed --gate --output $benchmark
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$images = Join-Path $output "image-performance.json"
& $dotnet run --project (Join-Path $root "tools\PromptVault.PerformanceGate\PromptVault.PerformanceGate.csproj") -c Release --no-restore -- --output $images
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$virtualization = Join-Path $output "virtualization.json"
& $dotnet run --project (Join-Path $root "tools\PromptVault.VirtualizationProbe\PromptVault.VirtualizationProbe.csproj") -c Release --no-restore -- $virtualization
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$similarity = Join-Path $output "similarity.json"
& $dotnet run --project (Join-Path $root "tools\PromptVault.SimilarityProbe\PromptVault.SimilarityProbe.csproj") -c Release --no-restore -- $similarity
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$gateArguments = @(
    "--benchmark", $benchmark,
    "--performance", $images,
    "--virtualization", $virtualization,
    "--similarity", $similarity,
    "--output", (Join-Path $output "m6-regression-gate.json")
)
if (-not [string]::IsNullOrWhiteSpace($UiTrace)) {
    $gateArguments += @("--trace", [IO.Path]::GetFullPath((Join-Path $root $UiTrace)))
}
& $dotnet run --project (Join-Path $root "tools\PromptVault.M6Gate\PromptVault.M6Gate.csproj") -c Release --no-restore -- @gateArguments
exit $LASTEXITCODE
