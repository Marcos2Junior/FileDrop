#requires -Version 7.3
[CmdletBinding()]
param([string] $CodexCommand = 'codex', [string[]] $ClientArguments = @())
$ErrorActionPreference = 'Stop'
$previous = $env:FILE_API_KEY
try {
    $credential = Read-Host 'Chave privada de POST FileDrop (somente nesta sessao)' -MaskInput
    if ($credential.Length -lt 32 -or $credential.Length -gt 256 -or $credential -cmatch '[^!-~]') { throw 'Formato de chave invalido.' }
    $env:FILE_API_KEY = $credential
    $credential = $null
    & $CodexCommand @ClientArguments
} finally {
    if ($null -eq $previous) { Remove-Item Env:FILE_API_KEY -ErrorAction SilentlyContinue }
    else { $env:FILE_API_KEY = $previous }
    $previous = $null
}
