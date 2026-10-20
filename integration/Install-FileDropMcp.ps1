#requires -Version 7.3
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][uri] $ApiEndpoint,
    [Parameter(Mandatory)][string[]] $Roots,
    [switch] $AllowInsecureHttp,
    [string] $InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FileDropMcp'),
    [string] $CodexHomePath = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex' }),
    [string] $PackageRoot = $PSScriptRoot,
    [switch] $SkipGlobalInstruction,
    [switch] $Apply
)
$ErrorActionPreference = 'Stop'
function Get-SafeFullPath([string] $Value) {
    if (-not [IO.Path]::IsPathFullyQualified($Value) -or $Value.StartsWith('\\') -or $Value.Substring(2).Contains(':')) { throw 'Use absolute local drive paths; UNC/device/ADS paths are not accepted.' }
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Value))
    $item = if ([IO.File]::Exists($full)) { [IO.FileInfo]::new($full) } else { [IO.DirectoryInfo]::new($full) }
    while ($item) {
        if ($item.Exists -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.LinkTarget)) { throw 'Installation/configuration cannot follow symlinks or junctions.' }
        $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
    }
    return $full
}
function Quote-Toml([string] $Value) { return ConvertTo-Json -InputObject $Value -Compress }
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'This package requires Windows x64 and PowerShell 7.3+.' }
if (-not $ApiEndpoint.IsAbsoluteUri -or ($ApiEndpoint.Scheme -ne 'https' -and -not ($ApiEndpoint.Scheme -eq 'http' -and $AllowInsecureHttp)) -or $ApiEndpoint.UserInfo -or $ApiEndpoint.Query -or $ApiEndpoint.Fragment -or -not $ApiEndpoint.AbsolutePath.EndsWith('/files') -or $ApiEndpoint.OriginalString.Contains('%')) { throw 'Use a fixed canonical HTTPS /files endpoint without credentials/query/fragment. HTTP requires -AllowInsecureHttp.' }
if ($Roots.Count -eq 0 -or $Roots.Count -gt 32) { throw 'Configure between 1 and 32 authorized roots.' }
$checkedRoots = @($Roots | ForEach-Object { $root = Get-SafeFullPath $_; if (-not [IO.Directory]::Exists($root)) { throw 'An authorized root does not exist.' }; $root })
$install = Get-SafeFullPath $InstallDirectory
$codexHome = Get-SafeFullPath $CodexHomePath
$package = Get-SafeFullPath $PackageRoot
$payload = Get-SafeFullPath (Join-Path $package 'server')
$skillSource = Get-SafeFullPath (Join-Path $package 'skill/filedrop-delivery/SKILL.md')
$launcher = Get-SafeFullPath (Join-Path $package 'Launch-CodexWithFileDrop.ps1')
if (-not (Test-Path -LiteralPath (Join-Path $payload 'FileDropMcp.exe')) -or -not (Test-Path -LiteralPath $skillSource) -or -not (Test-Path -LiteralPath $launcher)) { throw 'Incomplete package: executable, skill or launcher is missing.' }
foreach ($payloadFile in @(Get-ChildItem -LiteralPath $payload -File -Recurse)) { Get-SafeFullPath $payloadFile.FullName | Out-Null }
if ([IO.Directory]::Exists($install) -and @(Get-ChildItem -LiteralPath $install -Force).Count -ne 0) { throw 'Installation directory is not empty. Choose a new directory; existing state/files will not be overwritten.' }
$configPath = Get-SafeFullPath (Join-Path $codexHome 'config.toml')
$agentsPath = Get-SafeFullPath (Join-Path $codexHome 'AGENTS.md')
$skillTarget = Get-SafeFullPath (Join-Path $codexHome 'skills/filedrop-delivery')
if (Test-Path -LiteralPath $skillTarget) { throw 'Existing filedrop-delivery skill requires manual review.' }
$existingConfig = if (Test-Path -LiteralPath $configPath) { [IO.File]::ReadAllText($configPath) } else { '' }
# Append a new server table, never rewrite arbitrary TOML or expose its existing values.
if ($existingConfig -match '(?i)filedrop' -or $existingConfig -match '(?im)^\s*["'']?mcp_servers["'']?\s*=') { throw 'Existing FileDrop/inline MCP configuration requires manual review; no changes were made.' }
$existingAgents = if (Test-Path -LiteralPath $agentsPath) { [IO.File]::ReadAllText($agentsPath) } else { '' }
if (-not $SkipGlobalInstruction -and $existingAgents.Contains('FILEDROP-DELIVERY-BEGIN')) { throw 'Existing managed FileDrop instruction requires manual review.' }
$exePath = Join-Path $install 'server/FileDropMcp.exe'
$settingsPath = Join-Path $install 'mcp-config.json'
$block = @(
    '',
    '[mcp_servers.filedrop]',
    ('command = ' + (Quote-Toml $exePath)),
    'env_vars = ["FILE_API_KEY"]',
    'startup_timeout_sec = 15',
    'tool_timeout_sec = 300',
    '',
    '[mcp_servers.filedrop.env]',
    ('FILEDROP_CONFIG = ' + (Quote-Toml $settingsPath)),
    ''
) -join [Environment]::NewLine
$instruction = @'
<!-- FILEDROP-DELIVERY-BEGIN -->
For explicitly requested file/image/document delivery only, follow the filedrop-delivery skill and consult FileDrop MCP policy using the same channel, capability and active task ID. Native first; after one clear technical failure use scoped FileDrop fallback for 24 hours and that active task. Denial, cancellation or pending confirmation never authorizes fallback. Uncertain delivery requires verification before re-delivery or channel change. Never change task IDs to escape a block. MCP does not observe native tools or capture screenshots. Never persist/log bearer URLs, credentials or tool result bodies. Communicate the delivery outcome; leave unrelated tools/workflows unchanged.
<!-- FILEDROP-DELIVERY-END -->
'@
Write-Output ('Preview installation directory: ' + $install)
Write-Output ('Preview Codex config: ' + $configPath)
Write-Output ('Authorized roots: ' + ($checkedRoots -join ', '))
Write-Output ('AllowInsecureHttp: ' + [bool]$AllowInsecureHttp)
Write-Output 'Credential: inherited FILE_API_KEY only; the installer never reads/saves its value.'
Write-Output 'Existing config/AGENTS files are backed up before append; existing content is preserved.'
Write-Output $block
if (-not $Apply -or -not $PSCmdlet.ShouldProcess($install, 'Install local MCP and append backed-up Codex configuration/companion instructions')) { return }
$dotnet = Get-Command dotnet -ErrorAction Stop
$runtimes = & $dotnet.Source --list-runtimes
if (-not ($runtimes -match '^Microsoft.NETCore.App 9\.')) { throw '.NET 9 runtime is required; install manually. No runtime/global tool is installed by this script.' }
New-Item -ItemType Directory -Path $install,$codexHome,$skillTarget -Force | Out-Null
Copy-Item -LiteralPath $payload -Destination (Join-Path $install 'server') -Recurse
Copy-Item -LiteralPath $launcher -Destination (Join-Path $install 'Launch-CodexWithFileDrop.ps1')
Copy-Item -LiteralPath $skillSource -Destination (Join-Path $skillTarget 'SKILL.md')
[ordered]@{ ApiEndpoint=$ApiEndpoint.AbsoluteUri; AllowInsecureHttp=[bool]$AllowInsecureHttp; Roots=$checkedRoots; StateDirectory=(Join-Path $install 'state') } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $settingsPath -Encoding utf8
$backupSuffix = '.bak-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + [guid]::NewGuid().ToString('N')
if (Test-Path -LiteralPath $configPath) { [IO.File]::Copy($configPath,$configPath + $backupSuffix,$false) }
[IO.File]::WriteAllText($configPath,$existingConfig + [Environment]::NewLine + $block,[Text.UTF8Encoding]::new($false))
if (-not $SkipGlobalInstruction) {
    if (Test-Path -LiteralPath $agentsPath) { [IO.File]::Copy($agentsPath,$agentsPath + $backupSuffix,$false) }
    [IO.File]::WriteAllText($agentsPath,$existingAgents + [Environment]::NewLine + $instruction + [Environment]::NewLine,[Text.UTF8Encoding]::new($false))
}
Write-Output 'Installed locally. No credentials, runtime, global dotnet tool, IIS, firewall or ACL changes. Restart Codex with FILE_API_KEY supplied securely.'
