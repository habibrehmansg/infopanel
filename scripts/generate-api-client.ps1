# Regenerate the InfoPanel API client from the live OpenAPI spec.
# Prerequisites: dotnet tool restore (installs NSwag)
#
# Usage: pwsh scripts/generate-api-client.ps1

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$apiDir     = Join-Path (Join-Path $repoRoot 'InfoPanel') 'ApiClient'
$specFile   = Join-Path $apiDir 'openapi.json'
$nswagFile  = Join-Path $apiDir 'nswag.json'
$specUrl    = 'https://api.infopanel.net/openapi.json'

Write-Host "Fetching OpenAPI spec from $specUrl ..."
Invoke-RestMethod -Uri $specUrl -OutFile $specFile
Write-Host "Saved spec to $specFile"

Write-Host "Running NSwag code generation ..."
Push-Location $apiDir
try {
    dotnet nswag run $nswagFile
} finally {
    Pop-Location
}

Write-Host "Done. Generated client at InfoPanel/ApiClient/InfoPanelApiClient.cs"
