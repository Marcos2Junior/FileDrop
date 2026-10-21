#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('policy','record','upload','resume','end')][string]$Action='policy',
    [string]$Channel='codex-chat', [string]$Capability='file-delivery', [string]$TaskId='',
    [string]$Outcome='', [string]$Path='', [switch]$UserAuthorized,
    [string]$Config=$(if($env:FILEDROP_SKILL_CONFIG){$env:FILEDROP_SKILL_CONFIG}else{Join-Path $env:LOCALAPPDATA 'FileDropSkill\config.json'})
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.Web.Extensions
if(-not ('FileDropLocalPath' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
public static class FileDropLocalPath {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern uint GetFinalPathNameByHandle(IntPtr file, StringBuilder path, uint size, uint flags);
    public static string Resolve(FileStream file) {
        var path = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(file.SafeFileHandle.DangerousGetHandle(), path, 32768, 0);
        if(length==0 || length>=32768) throw new IOException("path_not_allowed");
        string value=path.ToString();
        if(value.StartsWith(@"\\?\")) value=value.Substring(4);
        return System.IO.Path.GetFullPath(value);
    }
}
'@
}
$script:FDJson=New-Object System.Web.Script.Serialization.JavaScriptSerializer
$script:FDJson.MaxJsonLength=1048576
$script:FDJson.RecursionLimit=20
function Write-FDJson($Value){return ConvertTo-Json -InputObject $Value -Depth 20 -Compress}
function StateMap-FD($Value){
    $map=@{}
    foreach($key in $Value.Keys){
        $v=$Value[$key]
        $map[$key]=if($v -is [Collections.IDictionary]){StateMap-FD $v}elseif($v -is [array]){,$v}else{$v}
    }
    return $map
}
function Fail-FD([string]$Code){$script:FDCode=$Code;throw 'FileDrop stopped.'}
function Local-FD([string]$Value){
    if(!$Value -or $Value -notmatch '^[A-Za-z]:[\\/]' -or $Value.Substring(2).Contains(':')){Fail-FD 'path_not_allowed'}
    $full=[IO.Path]::GetFullPath($Value).TrimEnd('\','/')
    $item=if([IO.File]::Exists($full)){[IO.FileInfo]::new($full)}else{[IO.DirectoryInfo]::new($full)}
    while($item){
        if($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){Fail-FD 'path_not_allowed'}
        $item=if($item -is [IO.FileInfo]){$item.Directory}else{$item.Parent}
    }
    return $full
}
function Inside-FD([string]$File,[string]$Root){
    return $File.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)
}
function Load-FD([string]$File){
    $full=Local-FD $File
    if(!(Test-Path -LiteralPath $full -PathType Leaf) -or ([IO.FileInfo]$full).Length -gt 65536){Fail-FD 'configuration_invalid'}
    $o=$script:FDJson.DeserializeObject([IO.File]::ReadAllText($full))
    return Options-FD $o $full
}
function Options-FD($o,[string]$full){
    if($o -isnot [Collections.IDictionary]){Fail-FD 'configuration_invalid'}
    $uri=$null
    if(![uri]::TryCreate([string]$o.ApiEndpoint,[UriKind]::Absolute,[ref]$uri) -or
       ($uri.Scheme -ne 'https' -and !($uri.Scheme -eq 'http' -and $o.AllowInsecureHttp -is [bool] -and $o.AllowInsecureHttp)) -or
       $uri.UserInfo -or $uri.Query -or $uri.Fragment -or !$uri.AbsolutePath.EndsWith('/files') -or $uri.OriginalString.Contains('%')){Fail-FD 'configuration_invalid'}
    $secret=Local-FD (Join-Path ([IO.Path]::GetDirectoryName($full)) 'api-key.txt')
    if($o.SecretFile){$secret=Local-FD ([string]$o.SecretFile)}
    $protection=if($o.SecretProtection){[string]$o.SecretProtection}else{'None'}
    if($protection -notin @('None','CurrentUserDpapi')){Fail-FD 'configuration_invalid'}
    return @{Endpoint=$uri;SecretFile=$secret;SecretProtection=$protection;State=(Join-Path ([IO.Path]::GetDirectoryName($full)) 'state')}
}
function Read-FDKey($Options){
    $key=$env:FILE_API_KEY;$clear=$null
    try{
        if(!$key -and $Options.SecretFile){
            $max=if($Options.SecretProtection -eq 'CurrentUserDpapi'){4096}else{258}
            if(![IO.File]::Exists($Options.SecretFile) -or ([IO.FileInfo]$Options.SecretFile).Length -gt $max){Fail-FD 'credentials_unavailable'}
            if($Options.SecretProtection -eq 'CurrentUserDpapi'){
                Add-Type -AssemblyName System.Security
                try{$clear=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($Options.SecretFile),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)}catch{Fail-FD 'credentials_unavailable'}
                $key=[Text.Encoding]::UTF8.GetString($clear)
            }else{$key=[IO.File]::ReadAllText($Options.SecretFile).TrimEnd([char[]]@(13,10))}
        }
        if(!$key -or $key.Length -lt 32 -or $key.Length -gt 256 -or $key -cmatch '[^!-~]'){Fail-FD 'credentials_unavailable'}
        return $key
    }finally{if($clear){[Array]::Clear($clear,0,$clear.Length)}}
}
function Hash-FD([string]$Value){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
}
function Save-FD($State,[string]$File){
    $json=Write-FDJson $State
    if([Text.Encoding]::UTF8.GetByteCount($json) -gt 1048576){Fail-FD 'state_full'}
    $tmp=$File+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try{
        [IO.File]::WriteAllText($tmp,$json,[Text.UTF8Encoding]::new($false))
        if([IO.File]::Exists($File)){[IO.File]::Replace($tmp,$File,[NullString]::Value)}else{[IO.File]::Move($tmp,$File)}
    }finally{if([IO.File]::Exists($tmp)){[IO.File]::Delete($tmp)}}
}
function New-FDRetry {
    return @{attempts=0;limit=3;nextAt=$null;status='ready';history=@()}
}
function Load-FDRetries([string]$File){
    if(![IO.File]::Exists($File)){return @{version=1;files=@{}}}
    try{
        if(([IO.FileInfo]$File).Length -gt 1048576){throw 'invalid'}
        $r=StateMap-FD ($script:FDJson.DeserializeObject([IO.File]::ReadAllText($File)))
        if($r.Count -ne 2 -or $r.version -ne 1 -or $r.files -isnot [Collections.IDictionary]){throw 'invalid'}
        $diagnostics=@('interrupted','legacy_unknown_time','transport_error','dns_failure','connection_refused','connection_reset','network_unreachable','timeout','rate_limited','server_unavailable','permission_denied','cancelled','tls_failure','contract_invalid','content_mismatch','redirect_rejected','http_rejected','delivered')
        foreach($entry in $r.files.GetEnumerator()){
            $v=$entry.Value
            if($entry.Key -cnotmatch '^[a-f0-9]{64}$' -or $v -isnot [Collections.IDictionary] -or $v.Count -ne 5 -or
               $v.attempts -isnot [int] -or $v.limit -isnot [int] -or $v.attempts -lt 1 -or $v.limit -lt 3 -or $v.attempts -gt $v.limit -or
               $v.status -notin @('ready','uploading','retryable','definitive','delivered') -or $v.history -isnot [array] -or $v.history.Count -ne $v.attempts){throw 'invalid'}
            if(!$v.nextAt){throw 'invalid'}
            $next=[DateTimeOffset]::ParseExact($v.nextAt,'O',[Globalization.CultureInfo]::InvariantCulture)
            $previous=[DateTimeOffset]::MinValue
            foreach($h in $v.history){
                if($h -isnot [Collections.IDictionary] -or $h.Count -ne 4 -or $h.outcome -notin @('uncertain','rejected','delivered') -or
                   $h.diagnostic -notin $diagnostics -or $h.httpStatus -isnot [int] -or $h.httpStatus -lt 0 -or $h.httpStatus -gt 599){throw 'invalid'}
                $at=[DateTimeOffset]::ParseExact($h.at,'O',[Globalization.CultureInfo]::InvariantCulture)
                if($at -lt $previous){throw 'invalid'};$previous=$at
            }
            if($next -lt $previous.AddMinutes(2)){throw 'invalid'}
        }
        return $r
    }catch{Fail-FD 'retry_state_invalid'}
}
function RetryDecision-FD($Retry){
    $code='retry_ready';$action='filedrop'
    if($Retry.status -eq 'delivered'){$code='already_delivered';$action='stop'}
    elseif($Retry.status -eq 'definitive'){$code='definitive_failure';$action='stop'}
    elseif($Retry.attempts -ge $Retry.limit){$code='retry_authorization_required';$action='stop'}
    elseif($Retry.nextAt -and [DateTimeOffset]::Parse($Retry.nextAt) -gt $script:FDNow){$code='retry_wait';$action='wait'}
    return @{success=$true;action=$action;code=$code;mayUpload=($action -eq 'filedrop');mayTryNative=$false;attempts=$Retry.attempts;maxAttempts=$Retry.limit;nextAttemptAt=$Retry.nextAt;previousDeliveryUncertain=(@($Retry.history|Where-Object{$_.outcome -eq 'uncertain'}).Count -gt 0)}
}
function FileDecision-FD($State,$Task,$Retry){
    $decision=Decision-FD $State $Task
    if($Retry -and ($decision.mayUpload -or ($Task.transport -eq 'filedrop' -and $Task.phase -in @('uncertain','uploading')))){
        $decision=RetryDecision-FD $Retry;$decision.fallbackUntil=$State.until
    }
    return $decision
}
function Exception-FDDiagnostic($Exception,[bool]$TimedOut){
    $d=@{diagnostic='transport_error';retryable=$false;denied=$false;cancelled=$false}
    for($e=$Exception;$e;$e=$e.InnerException){
        if($e -is [UnauthorizedAccessException]){$d.diagnostic='permission_denied';$d.denied=$true;$d.retryable=$false;return $d}
        if($e -is [Security.Authentication.AuthenticationException] -or $e -is [Security.SecurityException] -or ($e -is [Net.WebException] -and $e.Status -in @('TrustFailure','SecureChannelFailure'))){$d.diagnostic='tls_failure';$d.retryable=$false;return $d}
        if($e -is [Net.Sockets.SocketException]){
            if($e.SocketErrorCode -eq [Net.Sockets.SocketError]::AccessDenied){$d.diagnostic='permission_denied';$d.denied=$true;$d.retryable=$false;return $d}
            $d.retryable=$true
            $d.diagnostic=switch($e.SocketErrorCode){'HostNotFound'{'dns_failure'} 'TryAgain'{'dns_failure'} 'ConnectionRefused'{'connection_refused'} 'ConnectionReset'{'connection_reset'} 'NetworkUnreachable'{'network_unreachable'} 'HostUnreachable'{'network_unreachable'} 'TimedOut'{'timeout'} default{'transport_error'}}
            return $d
        }
        if($e -is [OperationCanceledException]){$d.retryable=$TimedOut;if($TimedOut){$d.diagnostic='timeout'}else{$d.diagnostic='cancelled';$d.cancelled=$true};return $d}
        if($e -is [TimeoutException]){$d.diagnostic='timeout';$d.retryable=$true;return $d}
        if($e -is [Net.Http.HttpRequestException] -or $e -is [IO.IOException]){$d.retryable=$true}
    }
    return $d
}
function Test-FDTransientStatus([int]$Status){return ($Status -in @(408,429) -or ($Status -ge 500 -and $Status -notin @(501,505)))}
function Decision-FD($State,$Task){
    $a='stop';$code=$Task.phase
    switch($Task.phase){
        'denied'{$code='permission_denied'}
        'pending_confirmation'{$a='wait'}
        'native'{$a='verify';$code='result_pending'}
        'uploading'{$a='verify';$code='delivery_uncertain'}
        'uncertain'{$a='verify';$code='delivery_uncertain'}
        'ready'{
            if($Task.pinned -or ($State.until -and [DateTimeOffset]::Parse($State.until) -gt $script:FDNow)){
                $Task.pinned=$true;$a='filedrop';$code='fallback_active'
            }else{$a='native';$code='native_first'}
        }
    }
    return @{success=$true;action=$a;code=$code;mayTryNative=($a -eq 'native');mayUpload=($a -eq 'filedrop');fallbackUntil=$State.until}
}
function Read-FDJson($Content,$Token){
    $stream=$Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $buffer=New-Object byte[] 8193;$total=0
    while($total -lt $buffer.Length){
        $n=$stream.ReadAsync($buffer,$total,$buffer.Length-$total,$Token).GetAwaiter().GetResult()
        if(!$n){break};$total+=$n
    }
    if($total -gt 8192){Fail-FD 'upload_response_invalid'}
    return $script:FDJson.DeserializeObject([Text.Encoding]::UTF8.GetString($buffer,0,$total))
}
function Send-FD($Options,[string]$File,$Client,[scriptblock]$BeforePost=$null){
    $stream=$null;$post=$null;$response=$null;$get=$null;$verified=$null;$timer=$null;$own=$false
    $sent=$false;$known=$null;$phase='ready';$stage='local'
    try{
        $full=Local-FD $File
        if(![IO.File]::Exists($full)){Fail-FD 'file_not_found'}
        $stream=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        [void](Local-FD ([FileDropLocalPath]::Resolve($stream)))
        if($stream.Length -lt 1 -or $stream.Length -gt 104857600){Fail-FD 'file_size_invalid'}
        $key=Read-FDKey $Options
        $length=$stream.Length;$sha=[Security.Cryptography.SHA256]::Create()
        try{$expected=$sha.ComputeHash($stream)}finally{$sha.Dispose()}
        $stream.Position=0
        if(!$Client){
            $handler=[Net.Http.HttpClientHandler]::new()
            $handler.AllowAutoRedirect=$false;$handler.UseCookies=$false;$handler.UseDefaultCredentials=$false
            $Client=[Net.Http.HttpClient]::new($handler);$Client.Timeout=[TimeSpan]::FromMinutes(4);$own=$true
        }
        $timer=[Threading.CancellationTokenSource]::new();$timer.CancelAfter([TimeSpan]::FromMinutes(4));$ct=$timer.Token
        $post=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,$Options.Endpoint)
        $post.Headers.Add('X-Api-Key',$key);$key=$null
        $form=[Net.Http.MultipartFormDataContent]::new();$post.Content=$form
        $part=[Net.Http.StreamContent]::new($stream)
        $part.Headers.ContentType=[Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
        $form.Add($part,'file',[IO.Path]::GetFileName($full))
        if($BeforePost){& $BeforePost $full}
        $sent=$true;$stage='post'
        $response=$Client.SendAsync($post,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$ct).GetAwaiter().GetResult()
        $status=[int]$response.StatusCode
        if($status -ne 201){
            $transient=Test-FDTransientStatus $status
            if($status -in @(401,403)){$phase='denied'}elseif($transient -or ($status -ge 300 -and $status -lt 400)){$phase='uncertain'}
            $diagnostic=if($phase -eq 'denied'){'permission_denied'}elseif($status -eq 429){'rate_limited'}elseif($status -eq 408){'timeout'}elseif($transient){'server_unavailable'}elseif($status -ge 300 -and $status -lt 400){'redirect_rejected'}else{'http_rejected'}
            return @{success=$false;code=$(if($phase -eq 'denied'){'permission_denied'}elseif($diagnostic -eq 'redirect_rejected'){'redirect_rejected'}else{'upload_rejected'});diagnostic=$diagnostic;stage=$stage;retryable=$transient;httpStatus=$status;requiresVerification=($phase -eq 'uncertain');phase=$phase}
        }
        $phase='uncertain';$r=Read-FDJson $response.Content $ct;$url=$null
        $types=@('image/png','image/jpeg','image/gif','image/webp','video/mp4','video/webm','video/ogg','audio/mp4','audio/wav','audio/ogg','audio/mpeg','application/pdf','application/octet-stream')
        if($r.id -cnotmatch '^[a-f0-9]{64}$' -or $r.contentType -notin $types -or ![uri]::TryCreate([string]$r.url,[UriKind]::Absolute,[ref]$url) -or
           $url.Scheme -ne $Options.Endpoint.Scheme -or $url.Authority -ne $Options.Endpoint.Authority -or $url.UserInfo -or $url.Fragment -or
           $url.AbsolutePath -cne ($Options.Endpoint.AbsolutePath+'/'+$r.id) -or $url.Query -cnotmatch '^\?key=[A-Za-z0-9_-]{43}$'){Fail-FD 'upload_response_invalid'}
        $known=@{success=$false;code='verification_failed';requiresVerification=$true;url=$r.url;name=[IO.Path]::GetFileName($full);contentType=$r.contentType;phase='uncertain';retryable=$false;diagnostic='contract_invalid';stage='download';httpStatus=0}
        $expiry=[DateTimeOffset]::MinValue
        $values=@(if($response.Headers.Contains('X-FileDrop-Expires-At')){$response.Headers.GetValues('X-FileDrop-Expires-At')})
        if($values.Count -ne 1 -or ![DateTimeOffset]::TryParseExact([string]$values[0],'O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None,[ref]$expiry)){
            $known.code='expiry_metadata_missing';return $known
        }
        $known.expiresAt=$expiry.ToString('O')
        $stage='download';$get=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$url)
        $verified=$Client.SendAsync($get,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$ct).GetAwaiter().GetResult()
        if([int]$verified.StatusCode -ne 200){$known.httpStatus=[int]$verified.StatusCode;$known.retryable=Test-FDTransientStatus $known.httpStatus;$known.diagnostic=if($known.retryable){'server_unavailable'}elseif($known.httpStatus -in @(401,403)){'permission_denied'}else{'http_rejected'};return $known}
        if($verified.Content.Headers.ContentType.MediaType -ne $r.contentType -or !$verified.Headers.Contains('X-Content-Type-Options') -or (@($verified.Headers.GetValues('X-Content-Type-Options')) -join ',') -ne 'nosniff'){return $known}
        $known.diagnostic='content_mismatch'
        $body=$verified.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $sha=[Security.Cryptography.SHA256]::Create();$buffer=New-Object byte[] 65536;$total=0
        try{
            while(($n=$body.ReadAsync($buffer,0,$buffer.Length,$ct).GetAwaiter().GetResult()) -gt 0){
                $total+=$n;if($total -gt $length){return $known}
                [void]$sha.TransformBlock($buffer,0,$n,$buffer,0)
            }
            [void]$sha.TransformFinalBlock([byte[]]@(),0,0)
            if($total -ne $length -or [Convert]::ToBase64String($sha.Hash) -ne [Convert]::ToBase64String($expected)){return $known}
        }finally{$sha.Dispose()}
        $known.success=$true;$known.code='delivered';$known.requiresVerification=$false;$known.phase='ready';$known.diagnostic='delivered'
        return $known
    }catch{
        $d=Exception-FDDiagnostic $_.Exception ($timer -and $timer.IsCancellationRequested)
        if($known){$known.retryable=$d.retryable;$known.diagnostic=$d.diagnostic;return $known}
        $retryable=$sent -and $d.retryable -and !$script:FDCode
        return @{success=$false;code=$(if($d.denied){'permission_denied'}elseif($d.cancelled){'cancelled'}elseif($script:FDCode){$script:FDCode}elseif($sent){'delivery_uncertain'}else{'local_file_error'});stage=$stage;diagnostic=$(if($script:FDCode){'contract_invalid'}else{$d.diagnostic});retryable=$retryable;requiresVerification=$sent;phase=$(if($d.denied){'denied'}elseif($d.cancelled){'cancelled'}elseif($sent){'uncertain'}else{'ready'})}
    }finally{
        foreach($d in @($verified,$get,$response,$post,$stream,$timer)){if($d){$d.Dispose()}}
        if($own -and $Client){$Client.Dispose()}
    }
}
function Invoke-FileDrop {
    param([string]$Action,[string]$Channel,[string]$Capability,[string]$TaskId,[string]$Outcome,[string]$Path,[string]$Config,
          $Client=$null,[DateTimeOffset]$Now=[DateTimeOffset]::UtcNow,[switch]$UserAuthorized)
    $fixedClock=$PSBoundParameters.ContainsKey('Now')
    $script:FDCode=$null;$script:FDNow=$Now;$gate=$null;$result=$null
    try{
        foreach($id in @($Channel,$Capability,$TaskId)){if($id -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$'){Fail-FD 'invalid_scope'}}
        $o=Load-FD $Config;$dir=Local-FD $o.State;[void][IO.Directory]::CreateDirectory($dir)
        $scope=Hash-FD ($Channel.ToLowerInvariant()+[char]10+$Capability.ToLowerInvariant())
        $statePath=Local-FD (Join-Path $dir ($scope+'.json'));$lockPath=Local-FD (Join-Path $dir ($scope+'.lock'))
        try{$gate=[IO.FileStream]::new($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch{Fail-FD 'state_busy'}
        $s=@{version=1;until=$null;tasks=@{}}
        if([IO.File]::Exists($statePath)){
            if(([IO.FileInfo]$statePath).Length -gt 1048576){Fail-FD 'state_invalid'}
            $script:FDCode='state_invalid';$raw=$script:FDJson.DeserializeObject([IO.File]::ReadAllText($statePath))
            if($raw -isnot [Collections.IDictionary]){Fail-FD 'state_invalid'}
            $s=StateMap-FD $raw
            if($s -isnot [Collections.IDictionary] -or $s.Count -ne 3 -or !$s.ContainsKey('until') -or $s.version -ne 1 -or $s.tasks -isnot [Collections.IDictionary]){Fail-FD 'state_invalid'}
            if($s.until){[void][DateTimeOffset]::Parse($s.until)}
            foreach($entry in $s.tasks.GetEnumerator()){
                $v=$entry.Value
                if($entry.Key -cnotmatch '^[a-f0-9]{64}$' -or $v -isnot [Collections.IDictionary] -or $v.Count -ne 3 -or $v.phase -notin @('ready','native','uploading','denied','cancelled','pending_confirmation','uncertain','closed') -or $v.pinned -isnot [bool] -or $v.transport -notin @('native','filedrop')){Fail-FD 'state_invalid'}
            }
            $script:FDCode=$null
        }
        $taskHash=Hash-FD $TaskId
        if(!$s.tasks.ContainsKey($taskHash)){$s.tasks[$taskHash]=@{phase='ready';pinned=$false;transport='native'}}
        $t=$s.tasks[$taskHash]
        if($t -isnot [Collections.IDictionary] -or $t.Count -ne 3 -or $t.phase -notin @('ready','native','uploading','denied','cancelled','pending_confirmation','uncertain','closed') -or $t.pinned -isnot [bool] -or $t.transport -notin @('native','filedrop')){Fail-FD 'state_invalid'}
        $retryPath=Local-FD (Join-Path $dir ($scope+'.retries.json'));$retryState=Load-FDRetries $retryPath
        $retry=$null;$retryKey=$null
        if($Path){$retryKey=Hash-FD ($TaskId+[char]10+(Local-FD $Path).ToLowerInvariant());if($retryState.files.ContainsKey($retryKey)){$retry=$retryState.files[$retryKey]}}
        if($Action -eq 'resume'){
            if($t.phase -eq 'native' -or ($t.phase -eq 'uncertain' -and $t.transport -eq 'native')){
                $result=Decision-FD $s $t;$result.success=$false;$result.requiresVerification=$true
            }elseif($t.transport -eq 'filedrop' -and $t.phase -in @('uncertain','uploading','ready')){
                if(!$retryKey){Fail-FD 'retry_path_required'}
                if(!$retry -and $t.phase -in @('uncertain','uploading')){
                    if(!$UserAuthorized){Fail-FD 'authorization_required'}
                    $retry=New-FDRetry;$retry.attempts=1;$retry.status='retryable';$retry.nextAt=$Now.AddMinutes(2).ToString('O')
                    $retry.history=@(@{at=$Now.ToString('O');outcome='uncertain';diagnostic='legacy_unknown_time';httpStatus=0})
                    $retryState.files[$retryKey]=$retry
                }
                if(!$retry){Fail-FD 'resume_not_allowed'}
                if($retry.status -in @('retryable','uploading') -and $retry.attempts -ge $retry.limit -and $UserAuthorized){$retry.limit=$retry.attempts+1}
                Save-FD $retryState $retryPath
                $result=FileDecision-FD $s $t $retry
            }elseif(!$UserAuthorized){Fail-FD 'authorization_required'}
            elseif($t.phase -notin @('denied','cancelled','pending_confirmation')){Fail-FD 'resume_not_allowed'}
            else{
                if($retry -and $retry.status -eq 'definitive'){
                    $retry.status='ready';if($retry.attempts -ge $retry.limit){$retry.limit=$retry.attempts+1};Save-FD $retryState $retryPath
                }
                $t.phase='ready';$result=Decision-FD $s $t
                if($retry -and $result.mayUpload){$result=FileDecision-FD $s $t $retry}
                if($result.mayTryNative){$t.phase='native';$t.transport='native'}
            }
        }elseif($Action -eq 'end'){
            if($t.phase -eq 'native' -or ($t.phase -in @('uncertain','uploading') -and !$retry)){
                $result=Decision-FD $s $t;$result.success=$false;$result.requiresVerification=$true
            }else{$t.phase='closed';$t.pinned=$false;$result=Decision-FD $s $t}
        }
        elseif($Action -eq 'record'){
            if($Outcome -notin @('technical_failure','delivered','denied','cancelled','pending_confirmation','uncertain','verified_not_delivered')){Fail-FD 'invalid_outcome'}
            if($t.phase -notin @('denied','cancelled','closed')){
                if($Outcome -eq 'delivered'){
                    if($retry){$retry.status='delivered';Save-FD $retryState $retryPath}
                    $t.phase='ready';$result=@{success=$true;action='delivered';code='delivery_confirmed'}
                }
                elseif($t.phase -in @('uncertain','uploading') -and $Outcome -ne 'verified_not_delivered'){}
                elseif($t.phase -eq 'pending_confirmation'){}
                elseif($Outcome -eq 'technical_failure' -and $t.phase -eq 'native'){$s.until=$Now.AddHours(24).ToString('O');$t.phase='ready';$t.pinned=$true}
                elseif($Outcome -eq 'verified_not_delivered' -and $t.phase -in @('uncertain','uploading')){
                    if($t.transport -eq 'native'){$s.until=$Now.AddHours(24).ToString('O')}
                    $t.phase='ready';$t.pinned=$true
                }elseif($Outcome -in @('denied','cancelled','pending_confirmation','uncertain')){
                    if(!($Outcome -eq 'uncertain' -and $t.transport -eq 'filedrop' -and $retry)){$t.phase=$Outcome}
                }
            }
            if(!$result){$result=Decision-FD $s $t}
        }elseif($Action -eq 'policy'){
            $result=FileDecision-FD $s $t $retry
            if($result.mayTryNative){$t.phase='native';$t.transport='native'}
        }elseif($Action -eq 'upload'){
            $decision=FileDecision-FD $s $t $retry
            if(!$decision.mayUpload){$result=$decision;$result.success=$false}
            else{
                $t.phase='uploading';$t.transport='filedrop';Save-FD $s $statePath
                $attempt=@{started=$false;ledger=$null}
                $beforePost={
                    param($UploadPath)
                    $uploadKey=Hash-FD ($TaskId+[char]10+$UploadPath.ToLowerInvariant())
                    $ledger=if($retryState.files.ContainsKey($uploadKey)){$retryState.files[$uploadKey]}else{New-FDRetry}
                    $check=RetryDecision-FD $ledger
                    if(!$check.mayUpload){Fail-FD $check.code}
                    $reserved=if($fixedClock){$Now}else{[DateTimeOffset]::UtcNow}
                    $ledger.attempts++;$ledger.status='uploading';$ledger.nextAt=$reserved.AddMinutes(6).ToString('O')
                    $ledger.history=@($ledger.history)+@(@{at=$reserved.ToString('O');outcome='uncertain';diagnostic='interrupted';httpStatus=0})
                    $retryState.files[$uploadKey]=$ledger
                    try{Save-FD $retryState $retryPath}catch{Fail-FD 'retry_state_save_failed'}
                    $attempt.ledger=$ledger;$attempt.started=$true
                }
                $result=Send-FD $o $Path $Client $beforePost
                $t.phase=if($result.phase -eq 'uncertain'){'ready'}else{$result.phase};[void]$result.Remove('phase')
                if($attempt.started){
                    $ledger=$attempt.ledger
                    $ledger.status=if($result.success){'delivered'}elseif($result.retryable){'retryable'}else{'definitive'}
                    $last=$ledger.history[$ledger.history.Count-1]
                    $last.outcome=if($result.success){'delivered'}elseif($result.requiresVerification){'uncertain'}else{'rejected'}
                    $last.diagnostic=$result.diagnostic;$last.httpStatus=if($result.httpStatus){[int]$result.httpStatus}else{0}
                    $finished=if($fixedClock){$Now}else{[DateTimeOffset]::UtcNow}
                    $ledger.nextAt=$finished.AddMinutes(2).ToString('O')
                    Save-FD $retryState $retryPath
                    $result.attempts=$ledger.attempts;$result.maxAttempts=$ledger.limit;$result.nextAttemptAt=$ledger.nextAt
                    $result.previousDeliveryUncertain=(@($ledger.history|Where-Object{$_.outcome -eq 'uncertain'}).Count -gt 0)
                }
            }
        }else{Fail-FD 'invalid_action'}
        Save-FD $s $statePath
        return $result
    }catch{
        if($result){
            $result.success=$false;$result.stateError='state_save_failed'
            if($result.ContainsKey('mayTryNative')){$result.mayTryNative=$false}
            if($result.ContainsKey('mayUpload')){$result.mayUpload=$false}
            if($result.ContainsKey('action')){$result.action='stop'}
            if($result.url){$result.code='state_save_failed';$result.requiresVerification=$true}
            elseif($result.code -notin @('permission_denied','cancelled')){$result.code='state_save_failed'}
            return $result
        }
        return @{success=$false;action='stop';code=$(if($script:FDCode){$script:FDCode}else{'local_error'});requiresVerification=$false}
    }finally{if($gate){$gate.Dispose()}}
}
if($MyInvocation.InvocationName -ne '.'){
    [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
    $result=Invoke-FileDrop -Action $Action -Channel $Channel -Capability $Capability -TaskId $TaskId -Outcome $Outcome -Path $Path -Config $Config -UserAuthorized:$UserAuthorized
    Write-FDJson $result
    if(!$result.success){exit 1}
}
