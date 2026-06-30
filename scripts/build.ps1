param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root ".dotnet\dotnet.exe"
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }

& $dotnet restore (Join-Path $root "PromptVault.slnx") --configfile (Join-Path $root "NuGet.Config")
& $dotnet test (Join-Path $root "tests\PromptVault.Tests\PromptVault.Tests.csproj") -c $Configuration --no-restore
& $dotnet publish (Join-Path $root "src\PromptVault.App\PromptVault.App.csproj") -c $Configuration -r win-x64 --self-contained true --no-restore -o (Join-Path $root "publish\PromptVault-win-x64")
