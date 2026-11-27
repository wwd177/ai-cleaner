<#
.SYNOPSIS
    Updates Scoop and WinGet package manifests with the exact published version and SHA256 checksum.
.EXAMPLE
    .\packaging\Update-PackageManifests.ps1 -Version 3.2.0 -Sha256 "<64-hex-sha256>"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$Sha256
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hashLower = $Sha256.ToLowerInvariant()
$hashUpper = $Sha256.ToUpperInvariant()

$scoopPath = Join-Path $root 'packaging\scoop\ai-cleaner.json'
$scoop = Get-Content -LiteralPath $scoopPath -Raw -Encoding UTF8 | ConvertFrom-Json
$scoop.version = $Version
$scoop.architecture.'64bit'.url = "https://github.com/wwd177/ai-cleaner/releases/download/v$Version/AICleaner-win-x64.zip"
$scoop.architecture.'64bit'.hash = $hashLower
$scoop | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $scoopPath -Encoding UTF8

foreach ($name in @('wwd177.AICleaner.yaml', 'wwd177.AICleaner.installer.yaml', 'wwd177.AICleaner.locale.en-US.yaml', 'wwd177.AICleaner.locale.zh-CN.yaml')) {
    $path = Join-Path $root "packaging\winget\$name"
    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $text = [regex]::Replace($text, '(?m)^PackageVersion:\s*.*$', "PackageVersion: $Version")
    $text = [regex]::Replace($text, 'releases/download/v[^/]+/AICleaner-win-x64\.zip', "releases/download/v$Version/AICleaner-win-x64.zip")
    $text = [regex]::Replace($text, '(?m)^(\s*InstallerSha256:\s*).*$', "`${1}$hashUpper")
    Set-Content -LiteralPath $path -Value $text -Encoding UTF8 -NoNewline
}

Write-Host "Updated Scoop and WinGet manifests for v$Version (SHA256=$hashLower)."
