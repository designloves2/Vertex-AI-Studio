<#
.SYNOPSIS
    Builds the Windows installer (Velopack Setup.exe) and, with -Publish, uploads it as a
    GitHub release that installed apps pick up through auto-update.

.DESCRIPTION
    Version = <Version> in src/ImageStudio.App/ImageStudio.App.csproj (bump it for every release).
    Output: artifacts\releases\VertexAIStudio-win-Setup.exe (+ full/delta .nupkg, releases.win.json).

    Requirements: .NET 8 SDK, Node.js (bundled as the embedded proxy runtime), vpk (dotnet tool install -g vpk --version 1.2.161), gh (logged in) for -Publish.

.EXAMPLE
    .\release.ps1            # build installer only
    .\release.ps1 -Publish   # build + create GitHub release v<version>
#>
param([switch]$Publish)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSCommandPath
$repoUrl = "https://github.com/designloves2/Vertex-AI-Studio"
# Install folder = %LOCALAPPDATA%\<packId>, wiped on uninstall. It must differ from the data
# folder (%LOCALAPPDATA%\VertexAIStudio: conversations DB, images, settings) so data survives.
$packId = "VertexAIStudioApp"
$csproj = Join-Path $root "src\ImageStudio.App\ImageStudio.App.csproj"
$version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Vertex AI Studio $version"

# .NET 8 SDK: system install, else the per-user one.
if (-not ((Get-Command dotnet -ErrorAction SilentlyContinue) -and (dotnet --list-sdks 2>$null)) -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
    $env:Path = "$env:DOTNET_ROOT;$env:Path"
}
$env:Path = "$env:USERPROFILE\.dotnet\tools;$env:Path"
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { throw "vpk not found: dotnet tool install -g vpk --version 1.2.161" }

$publishDir = Join-Path $root "artifacts\publish"
$releaseDir = Join-Path $root "artifacts\releases"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force $releaseDir | Out-Null

dotnet test (Join-Path $root "tests\ImageStudio.Tests") -c Release
if ($LASTEXITCODE) { throw "tests failed" }

dotnet publish $csproj -c Release -r win-x64 --self-contained -o $publishDir
if ($LASTEXITCODE) { throw "dotnet publish failed" }

# Embedded Vertex OpenAI Proxy: index.js + production node_modules + node.exe under <app>\proxy.
$proxySrc = Join-Path $root "proxy"
$proxyOut = Join-Path $publishDir "proxy"
New-Item -ItemType Directory -Force $proxyOut | Out-Null
foreach ($f in "index.js", "package.json", "package-lock.json", "LICENSE", "UPSTREAM_COMMIT") { Copy-Item (Join-Path $proxySrc $f) $proxyOut }
Push-Location $proxyOut
try { npm ci --omit=dev --no-audit --no-fund; if ($LASTEXITCODE) { throw "npm ci failed" } } finally { Pop-Location }
$node = (Get-Command node -ErrorAction Stop).Source
New-Item -ItemType Directory -Force (Join-Path $proxyOut "node") | Out-Null
Copy-Item $node (Join-Path (Join-Path $proxyOut "node") "node.exe")
Write-Host "Embedded proxy: $(Get-Content (Join-Path $proxySrc 'UPSTREAM_COMMIT')) with node $(& $node --version)"

$token = $null
if ($Publish) {
    $token = (gh auth token).Trim()
    # Previous release lets vpk build a small delta package for auto-update.
    vpk download github --repoUrl $repoUrl --token $token -o $releaseDir
}

vpk pack --packId $packId --packVersion $version --runtime win-x64 --packDir $publishDir --mainExe VertexAIStudio.exe `
    --packTitle "Vertex AI Studio" --packAuthors "designloves2" --icon (Join-Path $root "src\ImageStudio.App\Assets\app.ico") `
    -o $releaseDir
if ($LASTEXITCODE) { throw "vpk pack failed" }

if ($Publish) {
    vpk upload github --repoUrl $repoUrl --token $token -o $releaseDir --publish `
        --releaseName "Vertex AI Studio v$version" --tag "v$version"
    if ($LASTEXITCODE) { throw "vpk upload failed" }
    Write-Host "Published: $repoUrl/releases/tag/v$version"
}
Write-Host "Installer: $(Join-Path $releaseDir "$packId-win-Setup.exe")"
