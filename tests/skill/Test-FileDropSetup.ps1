#requires -Version 5.1
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $project 'skills\filedrop\scripts\Configure-FileDrop.ps1')
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('FileDropSetupTests\'+[guid]::NewGuid().ToString('N'))
$keyOne='TEST-ONLY-onboarding-secret-00000000000000000000'
$keyTwo='TEST-ONLY-rotated-secret-111111111111111111111111'
$count=0;$originalEnvironment=$env:FILE_API_KEY
function Check-Setup($Condition,[string]$Name){if(!$Condition){throw ('FAILED: '+$Name)};$script:count++}
function Fixture-Setup {
    $dir=Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory((Join-Path $dir 'files'))
    return @{Dir=$dir;Config=(Join-Path $dir 'config.json');Root=(Join-Path $dir 'files')}
}
function Save-Setup($F,[string]$Key=$keyOne,[string]$Url='https://files.example.invalid/files',[bool]$Http=$false,[string]$Stamp=''){
    if(!$Stamp){$Stamp=(Read-FDSetup $F.Config).Stamp}
    return Save-FDSetup -File $F.Config -Endpoint $Url -Roots @($F.Root) -AllowHttp $Http -ApiKey $Key -ExpectedStamp $Stamp
}
function Reject-Setup([scriptblock]$Action,[string]$Code,[string]$Name){
    $caught=$false;$script:FDCode=$null
    try{& $Action | Out-Null}catch{$caught=$true}
    Check-Setup ($caught -and $script:FDCode -eq $Code) $Name
}
try{
    Remove-Item Env:FILE_API_KEY -ErrorAction SilentlyContinue
    $f=Fixture-Setup
    Check-Setup ((Read-FDSetup $f.Config).Stamp -eq 'absent' -and ![IO.File]::Exists($f.Config)) 'initial read does not write config'
    Check-Setup (Save-Setup $f).success 'new setup saved'
    $options=Load-FD $f.Config
    Check-Setup ($options.SecretProtection -eq 'CurrentUserDpapi') 'protected credential format selected'
    Check-Setup ((Read-FDKey $options) -eq $keyOne) 'DPAPI roundtrip for current Windows user'
    $raw=[IO.File]::ReadAllText($f.Config)
    $encrypted=[IO.File]::ReadAllBytes($options.SecretFile)
    Check-Setup (!$raw.Contains($keyOne) -and ![Text.Encoding]::UTF8.GetString($encrypted).Contains($keyOne)) 'neither config nor encrypted credential contains plaintext key'
    Check-Setup (![IO.Directory]::Exists((Join-Path $f.Dir 'state'))) 'setup performs no delivery/state changes'
    [void][IO.Directory]::CreateDirectory((Join-Path $f.Dir 'state'))
    [IO.File]::WriteAllText((Join-Path $f.Dir 'state\keep.txt'),'TEST ONLY existing state')
    Check-Setup (Save-Setup $f -Key '' -Url 'https://new.example.invalid/files').success 'URL can change without entering key again'
    $updated=Load-FD $f.Config
    Check-Setup ($updated.SecretFile -eq $options.SecretFile -and (Read-FDKey $updated) -eq $keyOne) 'blank key preserves configured credential'
    Check-Setup ([IO.File]::ReadAllText((Join-Path $f.Dir 'state\keep.txt')) -eq 'TEST ONLY existing state') 'fallback state preserved'
    Check-Setup (Save-Setup $f -Key $keyTwo).success 'credential rotation saved'
    $rotated=Load-FD $f.Config
    Check-Setup ($rotated.SecretFile -ne $options.SecretFile -and (Read-FDKey $rotated) -eq $keyTwo) 'new credential uses independent protected file'
    Check-Setup ([Convert]::ToBase64String([IO.File]::ReadAllBytes($options.SecretFile)) -eq [Convert]::ToBase64String($encrypted)) 'old credential not deleted or overwritten'
    $before=Stamp-FDSetup $f.Config
    Reject-Setup {Save-Setup $f -Url 'http://files.example.invalid/files'} 'configuration_invalid' 'external HTTP requires opt-in'
    Check-Setup ((Stamp-FDSetup $f.Config) -eq $before) 'invalid HTTP does not overwrite config'
    Check-Setup (Save-Setup $f -Url 'http://files.example.invalid/files' -Http $true).success 'explicit external HTTP accepted'
    foreach($url in @('https://user:secret@files.example.invalid/files','https://files.example.invalid/files?q=1','https://files.example.invalid/files#x','ftp://files.example.invalid/files','https://files.example.invalid/other')){
        Reject-Setup {Save-Setup $f -Url $url -Http $true} 'configuration_invalid' 'invalid endpoint rejected'
    }
    foreach($badKey in @('short',('A'*257),('A'*40+' '))){Reject-Setup {Save-Setup $f -Key $badKey} 'credentials_unavailable' 'invalid key rejected'}
    foreach($badRoot in @('relative','\\server\share','C:\fake\file:ads')){
        Reject-Setup {Save-FDSetup $f.Config 'https://files.example.invalid/files' @($badRoot) $false $keyOne (Stamp-FDSetup $f.Config)} 'path_not_allowed' 'unsafe root rejected'
    }
    $oldStamp=Stamp-FDSetup $f.Config
    [void](Save-Setup $f)
    $fileCount=@(Get-ChildItem -LiteralPath $f.Dir -File).Count
    Reject-Setup {Save-Setup $f -Stamp $oldStamp} 'configuration_changed' 'stale window cannot overwrite concurrent edit'
    Check-Setup (@(Get-ChildItem -LiteralPath $f.Dir -File).Count -eq $fileCount) 'stale edit creates no credential file'
    $gate=[IO.FileStream]::new((Join-Path $f.Dir 'setup.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{Reject-Setup {Save-Setup $f} 'configuration_busy' 'concurrent setup locked'}finally{$gate.Dispose()}
    $env:FILE_API_KEY=$keyTwo
    Check-Setup ((Read-FDKey (Load-FD $f.Config)) -eq $keyTwo) 'existing environment credential has priority'
    Remove-Item Env:FILE_API_KEY
    $options=Load-FD $f.Config
    [IO.File]::WriteAllText((Join-Path $f.Dir 'api-key.txt'),$keyOne)
    [IO.File]::WriteAllBytes($options.SecretFile,[byte[]]@(1,2,3,4))
    Reject-Setup {Read-FDKey $options} 'credentials_unavailable' 'invalid encrypted credential fails closed without plaintext fallback'
    $f=Fixture-Setup
    Reject-Setup {Save-Setup $f -Key ''} 'credentials_unavailable' 'first setup requires a credential'
    Check-Setup (![IO.File]::Exists($f.Config)) 'missing credential writes no config'
    [IO.File]::WriteAllText($f.Config,'[]')
    Reject-Setup {Read-FDSetup $f.Config} 'configuration_invalid' 'wrong JSON type rejected'
    $f=Fixture-Setup;[void](Save-Setup $f)
    $data=$script:FDJson.DeserializeObject([IO.File]::ReadAllText($f.Config));$data.SecretProtection='unknown'
    [IO.File]::WriteAllText($f.Config,(Write-FDJson $data))
    Reject-Setup {Load-FD $f.Config} 'configuration_invalid' 'unknown protection rejected'
    $f=Fixture-Setup;[void](Save-Setup $f);$before=Stamp-FDSetup $f.Config
    $writer=(Get-Command Save-FD -CommandType Function).ScriptBlock
    try{
        Set-Item Function:\Save-FD {param($State,$File) throw 'TEST ONLY config write failure'}
        $caught=$false;try{Save-Setup $f -Key $keyTwo | Out-Null}catch{$caught=$true}
        Check-Setup ($caught -and (Stamp-FDSetup $f.Config) -eq $before) 'failed config write preserves original'
        foreach($file in (Get-ChildItem -LiteralPath $f.Dir -File)){
            Check-Setup (![Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($file.FullName)).Contains($keyTwo)) 'failed write leaves no plaintext secret'
        }
    }finally{Set-Item Function:\Save-FD $writer}
    $unsafe=Join-Path $project 'do-not-create-config.json'
    Reject-Setup {Save-FDSetup $unsafe 'https://files.example.invalid/files' @($f.Root) $false $keyOne 'absent'} 'configuration_location_invalid' 'package/repository cannot hold user config'
    Check-Setup (![IO.File]::Exists($unsafe)) 'repository untouched by rejected setup'
    Write-Output ('PASS: '+$count+' onboarding assertions; isolated fake credentials; no UI or network.')
}finally{
    if($originalEnvironment){$env:FILE_API_KEY=$originalEnvironment}else{Remove-Item Env:FILE_API_KEY -ErrorAction SilentlyContinue}
    $prefix=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'FileDropSetupTests')).TrimEnd('\')+'\'
    if(![IO.Path]::GetFullPath($testRoot).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup'}
    if([IO.Directory]::Exists($testRoot)){[IO.Directory]::Delete($testRoot,$true)}
}
