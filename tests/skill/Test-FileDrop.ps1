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
    public int Calls, PostCalls;
    public Action OnPost;
    public bool PostKey, GetKey, Streamed;
    public byte[] Bytes=new byte[]{1,2,3,4,255};
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
        Calls++;
        if(request.Method==HttpMethod.Post) {
            PostCalls++;
            if(OnPost!=null) OnPost();
            PostKey=request.Headers.Contains("X-Api-Key");
            var multipart=(MultipartFormDataContent)request.Content;
            foreach(var part in multipart) Streamed=part is StreamContent;
            await request.Content.ReadAsByteArrayAsync();
            if(Mode=="loss") throw new HttpRequestException("TEST ONLY transport loss");
            if(Mode=="accessdenied") throw new UnauthorizedAccessException("TEST ONLY access failure after send began");
            if(Mode=="timeout") throw new TimeoutException("TEST ONLY timeout");
            if(Mode=="dns") throw new HttpRequestException("TEST ONLY secret https://files.example.invalid/?key=FAKE-ONLY",new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
            if(Mode=="tls") throw new HttpRequestException("TEST ONLY TLS",new System.Security.Authentication.AuthenticationException());
            if(Mode=="cancelled") throw new OperationCanceledException("TEST ONLY cancellation");
            if(Mode.StartsWith("status")) return new HttpResponseMessage((HttpStatusCode)int.Parse(Mode.Substring(6)));
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
        if(Mode=="getloss") throw new HttpRequestException("TEST ONLY download outage");
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
    $data=@{ApiEndpoint='https://files.example.invalid/files';AllowInsecureHttp=$false}
    [IO.File]::WriteAllText($cfg,(Write-FDJson $data))
    $file=Join-Path $dir 'files\input.bin';[IO.File]::WriteAllBytes($file,[byte[]]@(1,2,3,4,255))
    return @{Dir=$dir;Config=$cfg;File=$file;Data=$data}
}
function Run($F,[string]$Action,[string]$Task='task',[string]$Outcome='',$Client=$null,[DateTimeOffset]$Clock=$now,[string]$Capability='file-delivery',[string]$File='',[switch]$Authorized){
    if(!$File){$File=$F.File}
    return Invoke-FileDrop -Action $Action -Channel chat -Capability $Capability -TaskId $Task -Outcome $Outcome -Path $File -Config $F.Config -Client $Client -Now $Clock -UserAuthorized:$Authorized
}
function Activate($F){
    [void](Run $F policy)
    [void](Run $F record -Outcome technical_failure)
}
try{
    $env:FILE_API_KEY=$fakeKey
    $f=Fixture
    $first=Run $f policy
    Check $first.mayTryNative ('native first: '+$first.code)
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
    Check ((Run $f resume -Authorized).code -eq 'resume_not_allowed') 'closed task cannot be reset by authorization'
    foreach($outcome in @('denied','cancelled','pending_confirmation','uncertain')){
        $f=Fixture;[void](Run $f policy);[void](Run $f record -Outcome $outcome)
        Check (!(Run $f record -Outcome technical_failure).mayUpload) ('no fallback: '+$outcome)
        Check (!(Run $f upload).mayUpload) ('upload blocked: '+$outcome)
    }
    foreach($outcome in @('denied','cancelled','pending_confirmation')){
        $f=Fixture;Activate $f;[void](Run $f record -Outcome $outcome)
        $fake=[FileDropFakeHttp]::new();$client=[Net.Http.HttpClient]::new($fake)
        try{
            Check ((Run $f resume).code -eq 'authorization_required') ('resume needs explicit authorization: '+$outcome)
            Check (!(Run $f upload -Client $client).success -and $fake.Calls -eq 0) ('no POST while blocked: '+$outcome)
            $result=Run $f resume -Authorized -Clock $now.AddHours(2)
            Check ($result.success -and $result.mayUpload -and $fake.Calls -eq 0) ('authorized resume preserves pinned fallback without sending: '+$outcome)
            Check ($result.fallbackUntil -eq $now.AddHours(24).ToString('O')) 'resume does not restart cooldown'
            Check ((Run $f upload -Client $client).success -and $fake.Calls -eq 2) ('explicit upload after resume: '+$outcome)
        }finally{$client.Dispose()}
    }
    $f=Fixture;[void](Run $f policy);[void](Run $f record -Outcome denied)
    $result=Run $f resume -Authorized
    Check ($result.mayTryNative -and !$result.mayUpload) 'authorization alone does not activate fallback'
    Check ((Run $f policy).code -eq 'result_pending') 'resumed native attempt is reserved once'
    $f=Fixture
    Check ((Run $f resume -Authorized).code -eq 'resume_not_allowed') 'resume is only for blocked states'
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
            Check (($state -join '') -notmatch 'https?://|\?key|input.bin|TEST-ONLY') 'no secrets/paths in state'
        }finally{$client.Dispose()}
    }
    foreach($mode in @('loss','accessdenied','denied','redirect','crossorigin','scheme','missingexpiry','badget')){
        $f=Fixture;Activate $f
        $fake=[FileDropFakeHttp]::new();$fake.Mode=$mode;$client=[Net.Http.HttpClient]::new($fake)
        try{
            $result=Run $f upload -Client $client
            Check (!$result.success) ('failure surfaced: '+$mode)
            [void](Run $f upload -Client $client)
            Check ($fake.Calls -eq $(if($mode -eq 'badget'){2}else{1})) ('no automatic reupload: '+$mode)
            Check ($result.retryable -eq ($mode -eq 'loss')) ('only technical transport errors are retryable: '+$mode)
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
        Check ((Run $f upload -File $bad).code -eq 'path_not_allowed') 'invalid path is not a permission denial'
        Check ((Run $f policy).mayUpload) 'invalid path does not pin task as denied'
    }
    $f=Fixture;$f.Data.Roots=@((Join-Path $f.Dir 'old-nonexistent-root'),'relative-legacy-value')
    [IO.File]::WriteAllText($f.Config,(Write-FDJson $f.Data));$oldConfig=[IO.File]::ReadAllText($f.Config)
    $outside=Join-Path $f.Dir 'outside-old-root.bin';[IO.File]::WriteAllBytes($outside,[byte[]]@(1,2,3,4,255));Activate $f
    $fake=[FileDropFakeHttp]::new();$client=[Net.Http.HttpClient]::new($fake)
    try{Check ((Run $f upload -File $outside -Client $client).success -and $fake.Calls -eq 2) 'explicit file outside legacy roots uploads with fake transport'}finally{$client.Dispose()}
    Check ([IO.File]::ReadAllText($f.Config) -eq $oldConfig) 'legacy config remains unchanged on use'
    $f=Fixture;Activate $f;$fake=[FileDropFakeHttp]::new();$client=[Net.Http.HttpClient]::new($fake)
    try{
        foreach($bad in @((Join-Path $f.Dir 'missing.bin'),$f.Dir)){
            Check ((Run $f upload -File $bad -Client $client).code -eq 'file_not_found') 'only existing regular files accepted'
        }
        [IO.File]::WriteAllBytes($f.File,[byte[]]@())
        Check ((Run $f upload -Client $client).code -eq 'file_size_invalid') 'empty file rejected'
        Check ($fake.Calls -eq 0) 'invalid files never reach HTTP'
    }finally{$client.Dispose()}
    $f=Fixture;Activate $f
    $stateFile=@(Get-ChildItem -LiteralPath (Join-Path $f.Dir state) -Filter *.json)[0].FullName
    $state=$script:FDJson.DeserializeObject([IO.File]::ReadAllText($stateFile));$state.tasks[(Hash-FD 'task')].phase='uploading';$state.tasks[(Hash-FD 'task')].transport='filedrop'
    [IO.File]::WriteAllText($stateFile,(Write-FDJson $state))
    $legacy=Run $f resume -Authorized
    Check ($legacy.code -eq 'retry_wait' -and $legacy.previousDeliveryUncertain -and $legacy.attempts -eq 1) 'legacy uncertain upload adopted honestly with two-minute delay'
    $legacyReady=Run $f resume -Clock $now.AddMinutes(2)
    Check $legacyReady.mayUpload ('legacy task has supported retry without editing state: '+$legacyReady.code)
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
    $env:FILE_API_KEY=$fakeKey
    $f=Fixture;Activate $f;$fake=[FileDropFakeHttp]::new();$fake.Mode='loss';$client=[Net.Http.HttpClient]::new($fake)
    try{
        $first=Run $f upload -Client $client
        Check (!$first.success -and $first.retryable -and $first.attempts -eq 1 -and $first.requiresVerification) ('first ambiguous attempt recorded honestly: code='+$first.code+' diagnostic='+$first.diagnostic+' attempts='+$first.attempts+' retryable='+$first.retryable)
        Check ((Run $f upload -Client $client -Clock $now.AddSeconds(119)).code -eq 'retry_wait' -and $fake.PostCalls -eq 1) 'cannot retry before two minutes'
        $second=Run $f upload -Client $client -Clock $now.AddMinutes(2)
        Check ($second.attempts -eq 2 -and $fake.PostCalls -eq 2) 'second total attempt after two minutes'
        Check ((Run $f resume -Clock $now.AddMinutes(3)).code -eq 'retry_wait') 'resume respects persisted interval'
        $third=Run $f upload -Client $client -Clock $now.AddMinutes(4)
        Check ($third.attempts -eq 3 -and $fake.PostCalls -eq 3) 'third total attempt after second interval'
        Check ((Run $f upload -Client $client -Clock $now.AddMinutes(6)).code -eq 'retry_authorization_required' -and $fake.PostCalls -eq 3) 'fourth POST blocked without explicit authorization'
        Check ((Run $f resume -Clock $now.AddMinutes(6)).code -eq 'retry_authorization_required') 'resume cannot silently reset limit'
        $info.Arguments='-NoProfile -ExecutionPolicy RemoteSigned -File "'+$helper+'" -Action policy -Channel chat -TaskId task -Path "'+$f.File+'" -Config "'+$f.Config+'"'
        $child=[Diagnostics.Process]::Start($info)
        try{$out=$child.StandardOutput.ReadToEnd();$err=$child.StandardError.ReadToEnd();$child.WaitForExit();$json=$script:FDJson.DeserializeObject($out);Check ($json.code -eq 'retry_authorization_required' -and $json.attempts -eq 3 -and !$err) 'new helper process preserves attempt limit'}finally{$child.Dispose()}
        $granted=Run $f resume -Authorized -Clock $now.AddMinutes(4).AddSeconds(1)
        Check ($granted.maxAttempts -eq 4 -and $granted.code -eq 'retry_wait') 'authorization grants one extra attempt without shortening delay'
        Check ((Run $f resume -Authorized -Clock $now.AddMinutes(5)).maxAttempts -eq 4) 'duplicate authorization call does not accumulate credits'
        $fake.Mode='success';$done=Run $f upload -Client $client -Clock $now.AddMinutes(6)
        Check ($done.success -and $done.attempts -eq 4 -and $done.previousDeliveryUncertain -and $fake.PostCalls -eq 4) 'authorized fourth attempt can succeed without denying earlier uncertainty'
        Check ((Run $f upload -Client $client -Clock $now.AddMinutes(10)).code -eq 'already_delivered' -and $fake.PostCalls -eq 4) 'verified delivery is not automatically uploaded again'
        $history=[IO.File]::ReadAllText(@(Get-ChildItem -LiteralPath (Join-Path $f.Dir state) -Filter *.retries.json)[0].FullName)
        $registry=$script:FDJson.DeserializeObject($history);$ledger=@($registry.files.Values)[0]
        Check ($ledger.history.Count -eq 4 -and @($ledger.history|Where-Object{$_.outcome -eq 'uncertain'}).Count -eq 3) 'earlier uncertain attempts remain in persistent history'
        Check ($history -notmatch 'https?://|\?key|input.bin|TEST-ONLY|FAKE-ONLY') 'retry history contains no paths, links, errors or credentials'
    }finally{$client.Dispose()}
    foreach($mode in @('timeout','dns','status408','status429','status500','status503','getloss','tls','accessdenied','denied','cancelled','status400','status401','status403','status413','status501','status505')){
        $f=Fixture;Activate $f;$fake=[FileDropFakeHttp]::new();$fake.Mode=$mode;$client=[Net.Http.HttpClient]::new($fake)
        try{
            $result=Run $f upload -Client $client
            $technical=$mode -in @('timeout','dns','status408','status429','status500','status503','getloss')
            Check (!$result.success -and $result.retryable -eq $technical) ('retry classification: '+$mode)
            Check ((Write-FDJson $result) -notmatch 'FAKE-ONLY|TEST ONLY') 'diagnostics omit raw exception messages'
            [void](Run $f upload -Client $client -Clock $now.AddMinutes(2))
            Check ($fake.PostCalls -eq $(if($technical){2}else{1})) ('retry allowed only for technical failure: '+$mode)
        }finally{$client.Dispose()}
    }
    $f=Fixture;Activate $f;$fake=[FileDropFakeHttp]::new();$fake.Mode='loss';$client=[Net.Http.HttpClient]::new($fake)
    try{
        [void](Run $f upload -Client $client)
        foreach($name in @('second.png','third.png')){
            $file=Join-Path $f.Dir $name;[IO.File]::WriteAllBytes($file,[byte[]]@(1,2,3,4,255));$fake.Mode='success'
            $other=Run $f upload -Client $client -File $file
            Check ($other.success -and $other.attempts -eq 1) 'uncertain first file does not block other files or consume their budget'
        }
    }finally{$client.Dispose()}
    $f=Fixture;Activate $f;$fake=[FileDropFakeHttp]::new();$fake.Mode='loss';$client=[Net.Http.HttpClient]::new($fake)
    try{
        [void](Run $f upload -Client $client)
        $retryFile=@(Get-ChildItem -LiteralPath (Join-Path $f.Dir state) -Filter *.retries.json)[0].FullName
        $raw=$script:FDJson.DeserializeObject([IO.File]::ReadAllText($retryFile));$key=@($raw.files.Keys)[0]
        $raw.files[$key].status='uploading';$raw.files[$key].nextAt=$now.AddMinutes(6).ToString('O');$raw.files[$key].history[0].diagnostic='interrupted'
        [IO.File]::WriteAllText($retryFile,(Write-FDJson $raw))
        $main=@(Get-ChildItem -LiteralPath (Join-Path $f.Dir state) -Filter *.json|Where-Object{$_.Name -notlike '*.retries.json'})[0].FullName
        $raw=$script:FDJson.DeserializeObject([IO.File]::ReadAllText($main));$raw.tasks[(Hash-FD 'task')].phase='uploading'
        [IO.File]::WriteAllText($main,(Write-FDJson $raw))
        Check ((Run $f resume -Clock $now.AddSeconds(359)).code -eq 'retry_wait') 'interrupted reservation preserves conservative interval'
        $fake.Mode='success';$recovered=Run $f upload -Client $client -Clock $now.AddMinutes(6)
        Check ($recovered.success -and $recovered.attempts -eq 2 -and $recovered.previousDeliveryUncertain -and $fake.PostCalls -eq 2) 'crash recovery retains count and earlier uncertainty'
        $raw=$script:FDJson.DeserializeObject([IO.File]::ReadAllText($retryFile));$raw.files[$key].nextAt=$now.ToString('O')
        [IO.File]::WriteAllText($retryFile,(Write-FDJson $raw))
        Check ((Run $f upload -Client $client -Clock $now.AddMinutes(10)).code -eq 'retry_state_invalid' -and $fake.PostCalls -eq 2) 'invalid persisted interval cannot allow early retry'
    }finally{$client.Dispose()}
    $f=Fixture;Activate $f;$fake=[FileDropFakeHttp]::new();$client=[Net.Http.HttpClient]::new($fake)
    try{
        Remove-Item Env:FILE_API_KEY
        $invalid=Run $f upload -Client $client
        Check ($invalid.code -eq 'credentials_unavailable' -and !$invalid.retryable -and $fake.PostCalls -eq 0) 'invalid local credential never starts retry budget or POST'
    }finally{$env:FILE_API_KEY=$fakeKey;$client.Dispose()}
    $f=Fixture;Activate $f;$fake=[FileDropFakeHttp]::new();$fake.Mode='loss';$client=[Net.Http.HttpClient]::new($fake)
    try{
        [void](Run $f upload -Client $client)
        $retryFile=@(Get-ChildItem -LiteralPath (Join-Path $f.Dir state) -Filter *.retries.json)[0].FullName
        [IO.File]::WriteAllText($retryFile,'{invalid')
        Check ((Run $f upload -Client $client -Clock $now.AddMinutes(5)).code -eq 'retry_state_invalid' -and $fake.PostCalls -eq 1) 'corrupt retry state cannot authorize another POST'
    }finally{$client.Dispose()}
    $f=Fixture;Activate $f;$fake=[FileDropFakeHttp]::new();$client=[Net.Http.HttpClient]::new($fake)
    try{
        $script:nested=$null;$fake.OnPost=[Action]{$script:nested=Run $f upload -Client $client}
        Check ((Run $f upload -Client $client).success -and $script:nested.code -eq 'state_busy' -and $fake.PostCalls -eq 1) 'scope lock prevents concurrent duplicate POST'
    }finally{$client.Dispose()}
    $f=Fixture;Activate $f;[void](Run $f record -Outcome denied)
    $info.Arguments='-NoProfile -ExecutionPolicy RemoteSigned -File "'+$helper+'" -Action resume -Channel chat -TaskId task -UserAuthorized -Config "'+$f.Config+'"'
    $child=[Diagnostics.Process]::Start($info)
    try{
        $out=$child.StandardOutput.ReadToEnd();$err=$child.StandardError.ReadToEnd()
        if(!$child.WaitForExit(20000)){$child.Kill();throw 'Test child timed out.'}
        $json=$script:FDJson.DeserializeObject($out)
        Check ($child.ExitCode -eq 0 -and $json.success -and $json.mayUpload -and !$err) 'CLI exposes explicit authorized resume'
    }finally{$child.Dispose()}
    $f=Fixture;Activate $f;$savedWriter=(Get-Command Save-FD -CommandType Function).ScriptBlock
    $fake=[FileDropFakeHttp]::new();$client=[Net.Http.HttpClient]::new($fake)
    try{
        Set-Item -Path Function:\Save-FD -Value {param($State,$File) if($File.EndsWith('.retries.json')){throw 'TEST ONLY retry reservation write failure'}; & $savedWriter $State $File}
        $result=Run $f upload -Client $client
        Check (!$result.success -and $fake.PostCalls -eq 0 -and $result.code -eq 'retry_state_save_failed') 'counter reservation must persist before any POST'
    }finally{Set-Item -Path Function:\Save-FD -Value $savedWriter;$client.Dispose()}
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
