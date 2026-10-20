#requires -Version 5.1
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $project 'skills\filedrop\scripts\filedrop.ps1')
Add-Type -ReferencedAssemblies 'System.Net.Http.dll' -TypeDefinition @'
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public class FileDropFakeHttp : HttpMessageHandler {
    public string Mode="success", Endpoint="https://files.example.invalid/files";
    public int Calls;
    public bool PostKey, GetKey, Streamed;
    public byte[] Bytes=new byte[]{1,2,3,4,255};
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
        Calls++;
        if(request.Method==HttpMethod.Post) {
            PostKey=request.Headers.Contains("X-Api-Key");
            var multipart=(MultipartFormDataContent)request.Content;
            foreach(var part in multipart) Streamed=part is StreamContent;
            await request.Content.ReadAsByteArrayAsync();
            if(Mode=="loss") throw new HttpRequestException("TEST ONLY transport loss");
            if(Mode=="denied" || Mode=="redirect") {
                var stop=new HttpResponseMessage(Mode=="denied"?HttpStatusCode.Unauthorized:HttpStatusCode.Redirect);
                stop.Headers.Location=new Uri("https://other.example.invalid/");
                return stop;
            }
            string url=Endpoint+"/"+new string('a',64)+"?key="+new string('A',43);
            if(Mode=="crossorigin") url=url.Replace("files.example.invalid","other.example.invalid");
            if(Mode=="scheme") url=url.Replace("https:","http:");
            var r=new HttpResponseMessage(HttpStatusCode.Created);
            r.Content=new StringContent("{\"id\":\""+new string('a',64)+"\",\"url\":\""+url+"\",\"contentType\":\"application/octet-stream\"}",Encoding.UTF8,"application/json");
            if(Mode!="missingexpiry") r.Headers.Add("X-FileDrop-Expires-At","2026-10-27T12:00:00.0000000+00:00");
            return r;
        }
        GetKey=request.Headers.Contains("X-Api-Key");
        var get=new HttpResponseMessage(HttpStatusCode.OK);
        get.Content=new ByteArrayContent(Mode=="badget"?new byte[]{9}:Bytes);
        get.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        get.Headers.Add("X-Content-Type-Options","nosniff");
        return get;
    }
}
'@
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('FileDropSkillTests\'+[guid]::NewGuid().ToString('N'))
$tests=0
$fakeKey='TEST-ONLY-fictitious-secret-00000000000000000000'
$now=[DateTimeOffset]::Parse('2026-10-20T12:00:00Z')
function Check($Condition,[string]$Name){if(!$Condition){throw ('FAILED: '+$Name)};$script:tests++}
function Fixture {
    $dir=Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory((Join-Path $dir 'files'))
    $cfg=Join-Path $dir 'config.json'
    $data=@{ApiEndpoint='https://files.example.invalid/files';Roots=@((Join-Path $dir 'files'));AllowInsecureHttp=$false}
    [IO.File]::WriteAllText($cfg,(Write-FDJson $data))
    $file=Join-Path $dir 'files\input.bin';[IO.File]::WriteAllBytes($file,[byte[]]@(1,2,3,4,255))
    return @{Dir=$dir;Config=$cfg;File=$file;Data=$data}
}
function Run($F,[string]$Action,[string]$Task='task',[string]$Outcome='',$Client=$null,[DateTimeOffset]$Clock=$now,[string]$Capability='file-delivery',[string]$File=''){
    if(!$File){$File=$F.File}
    return Invoke-FileDrop -Action $Action -Channel chat -Capability $Capability -TaskId $Task -Outcome $Outcome -Path $File -Config $F.Config -Client $Client -Now $Clock
}
function Activate($F){
    [void](Run $F policy)
    [void](Run $F record -Outcome technical_failure)
}
try{
    $env:FILE_API_KEY=$fakeKey
    $f=Fixture
    Check (Run $f policy).mayTryNative 'native first'
    Check ((Run $f policy).action -eq 'verify') 'no duplicate unreported native attempt'
    $active=Run $f record -Outcome technical_failure
    Check ($active.success -and $active.mayUpload) 'clear technical failure persists fallback'
    $next=Run $f policy -Task 'new-task'
    Check $next.mayUpload ('cooldown shared in same capability: '+$next.code)
    Check ((Run $f policy -Capability 'other').mayTryNative) 'capabilities independent'
    Check ((Run $f policy -Clock $now.AddHours(25)).mayUpload) 'active task remains pinned'
    [void](Run $f end)
    Check ((Run $f policy).action -eq 'stop') 'closed task cannot resume'
    Check ((Run $f policy -Task after-cooldown -Clock $now.AddHours(25)).mayTryNative) 'fresh task after cooldown native'
    foreach($outcome in @('denied','cancelled','pending_confirmation','uncertain')){
        $f=Fixture;[void](Run $f policy);[void](Run $f record -Outcome $outcome)
        Check (!(Run $f record -Outcome technical_failure).mayUpload) ('no fallback: '+$outcome)
        Check (!(Run $f upload).mayUpload) ('upload blocked: '+$outcome)
    }
    $f=Fixture;[void](Run $f policy);[void](Run $f record -Outcome uncertain)
    Check ((Run $f record -Outcome verified_not_delivered).mayUpload) 'confirmed non-delivery resolves uncertainty'
    foreach($scheme in @('https','http')){
        $f=Fixture;$f.Data.ApiEndpoint=$scheme+'://files.example.invalid/files'
        $f.Data.AllowInsecureHttp=($scheme -eq 'http')
        [IO.File]::WriteAllText($f.Config,(Write-FDJson $f.Data));Activate $f
        $fake=[FileDropFakeHttp]::new();$fake.Endpoint=$f.Data.ApiEndpoint;$client=[Net.Http.HttpClient]::new($fake)
        try{
            $result=Run $f upload -Client $client
            Check $result.success ($scheme+' roundtrip')
            Check ($fake.Calls -eq 2 -and $fake.PostKey -and !$fake.GetKey -and $fake.Streamed) 'one streaming POST and credential-free GET'
            Check ($result.expiresAt -and $result.url -and $result.contentType) 'agent result fields'
            $state=@(Get-ChildItem -LiteralPath (Join-Path $f.Dir state) -Filter *.json)|ForEach-Object{[IO.File]::ReadAllText($_.FullName)}
            Check (($state -join '') -notmatch 'https?|\?key|input.bin|TEST-ONLY') 'no secrets/paths in state'
        }finally{$client.Dispose()}
    }
    foreach($mode in @('loss','denied','redirect','crossorigin','scheme','missingexpiry','badget')){
        $f=Fixture;Activate $f
        $fake=[FileDropFakeHttp]::new();$fake.Mode=$mode;$client=[Net.Http.HttpClient]::new($fake)
        try{
            $result=Run $f upload -Client $client
            Check (!$result.success) ('failure surfaced: '+$mode)
            [void](Run $f upload -Client $client)
            Check ($fake.Calls -eq $(if($mode -eq 'badget'){2}else{1})) ('no automatic reupload: '+$mode)
        }finally{$client.Dispose()}
    }
    $f=Fixture;$f.Data.ApiEndpoint='http://files.example.invalid/files'
    [IO.File]::WriteAllText($f.Config,(Write-FDJson $f.Data))
    Check ((Run $f policy).code -eq 'configuration_invalid') 'external HTTP default rejected'
    foreach($url in @('ftp://files.example.invalid/files','https://user:secret@files.example.invalid/files','https://files.example.invalid/files?q=1','https://files.example.invalid/files#x')){
        $f=Fixture;$f.Data.ApiEndpoint=$url;$f.Data.AllowInsecureHttp=$true
        [IO.File]::WriteAllText($f.Config,(Write-FDJson $f.Data))
        Check ((Run $f policy).code -eq 'configuration_invalid') 'invalid endpoint rejected with optin'
    }
    foreach($bad in @('..\outside.bin','\\server\share\file','C:\fake\file:ads')){
        $f=Fixture;Activate $f
        Check ((Run $f upload -File $bad).code -eq 'permission_denied') 'unapproved path denied'
    }
    $f=Fixture;Activate $f
    $large=[IO.File]::Create($f.File);$large.SetLength(104857601);$large.Dispose()
    Check ((Run $f upload).code -eq 'file_size_invalid') '100MiB limit checked before network'
    $f=Fixture;Activate $f
    Remove-Item Env:FILE_API_KEY
    [IO.File]::WriteAllText((Join-Path $f.Dir 'api-key.txt'),$fakeKey)
    $fake=[FileDropFakeHttp]::new();$client=[Net.Http.HttpClient]::new($fake)
    try{Check ((Run $f upload -Client $client).success) 'separate user secret file supported'}finally{$client.Dispose()}
    $f=Fixture;Activate $f
    Add-Type -AssemblyName System.Security
    $protected=Join-Path $f.Dir 'api-key.dpapi'
    [IO.File]::WriteAllBytes($protected,[Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($fakeKey),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $f.Data.SecretFile=$protected;$f.Data.SecretProtection='CurrentUserDpapi'
    [IO.File]::WriteAllText($f.Config,(Write-FDJson $f.Data))
    $fake=[FileDropFakeHttp]::new();$client=[Net.Http.HttpClient]::new($fake)
    try{
        Check ((Run $f upload -Client $client).success) 'DPAPI credential supports upload/GET roundtrip'
        Check ($fake.Calls -eq 2 -and $fake.PostKey -and !$fake.GetKey) 'protected credential only accompanies POST'
    }finally{$client.Dispose()}
    $f=Fixture;[void](Run $f policy)
    $stateFile=@(Get-ChildItem -LiteralPath (Join-Path $f.Dir state) -Filter *.json)[0].FullName
    [IO.File]::WriteAllText($stateFile,'{invalid')
    Check ((Run $f policy).code -eq 'state_invalid') 'corrupt state fails closed'
    $f=Fixture;[void](Run $f policy)
    $lockFile=@(Get-ChildItem -LiteralPath (Join-Path $f.Dir state) -Filter *.lock)[0].FullName
    $gate=[IO.FileStream]::new($lockFile,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{Check ((Run $f policy -Task concurrent).code -eq 'state_busy') 'concurrent scope locked before action'}finally{$gate.Dispose()}
    $f=Fixture
    $info=[Diagnostics.ProcessStartInfo]::new()
    $info.FileName=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $helper=Join-Path $project 'skills\filedrop\scripts\filedrop.ps1'
    $info.Arguments='-NoProfile -ExecutionPolicy RemoteSigned -File "'+$helper+'" -Action policy -TaskId cli-fixture -Config "'+$f.Config+'"'
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $child=[Diagnostics.Process]::Start($info)
    try{
        $out=$child.StandardOutput.ReadToEnd();$err=$child.StandardError.ReadToEnd()
        if(!$child.WaitForExit(20000)){$child.Kill();throw 'Test child timed out.'}
        $json=$script:FDJson.DeserializeObject($out)
        Check ($child.ExitCode -eq 0 -and $json.success -and $json.mayTryNative) 'real CLI JSON and exit status'
        Check (!$err -and $out -notmatch 'TEST-ONLY|\?key=') 'real CLI emits no credential or debug output'
    }finally{$child.Dispose()}
    $f=Fixture;$savedWriter=(Get-Command Save-FD -CommandType Function).ScriptBlock
    try{
        Set-Item -Path Function:\Save-FD -Value {param($State,$File) throw 'TEST ONLY simulated state write failure'}
        $result=Run $f policy
        Check (!$result.success -and !$result.mayTryNative -and !$result.mayUpload -and $result.code -eq 'state_save_failed') 'failed state persistence never authorizes delivery'
    }finally{Set-Item -Path Function:\Save-FD -Value $savedWriter}
    Write-Output ('PASS: '+$tests+' assertions; fake transport only.')
}finally{
    Remove-Item Env:FILE_API_KEY -ErrorAction SilentlyContinue
    $expected=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'FileDropSkillTests')).TrimEnd('\')+'\'
    if(![IO.Path]::GetFullPath($testRoot).StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup.'}
    if([IO.Directory]::Exists($testRoot)){[IO.Directory]::Delete($testRoot,$true)}
}
