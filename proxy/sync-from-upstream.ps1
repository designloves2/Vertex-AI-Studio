<#
.SYNOPSIS
    Copies the embedded proxy (index.js + package files) from the vertex-openai-proxy repo.
    The proxy is maintained in that repo; this app only ships a copy of it.
.EXAMPLE
    .\proxy\sync-from-upstream.ps1                       # from C:\AI\vertex-openai-proxy
    .\proxy\sync-from-upstream.ps1 -Upstream D:\src\vertex-openai-proxy
#>
param([string]$Upstream = "C:\AI\vertex-openai-proxy")
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $PSCommandPath
if (-not (Test-Path (Join-Path $Upstream "index.js"))) { throw "index.js not found in $Upstream" }
foreach ($f in "index.js", "package.json", "package-lock.json", "LICENSE") { Copy-Item (Join-Path $Upstream $f) $here -Force }
(git -C $Upstream rev-parse --short HEAD) | Set-Content (Join-Path $here "UPSTREAM_COMMIT") -Encoding ascii
Push-Location $here
try { npm ci --omit=dev --no-audit --no-fund; if ($LASTEXITCODE) { throw "npm ci failed" } } finally { Pop-Location }
Write-Host "Synced proxy from $Upstream ($(Get-Content (Join-Path $here 'UPSTREAM_COMMIT')))"
