#requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $FilePath,
    [Parameter(Mandatory)][uri] $Endpoint,
    [switch] $AllowInsecureHttp
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$failureCode = 'invalid_input'
$failureMessage = 'Confira arquivo, endpoint e a variavel FILE_API_KEY.'
$handler = $null
$client = $null
$request = $null
$response = $null
$verification = $null
$get = $null
try {
    $key = $env:FILE_API_KEY
    if ([string]::IsNullOrWhiteSpace($key) -or $key.Length -lt 32 -or $key.Length -gt 256 -or $key -cmatch '[^!-~]') { throw 'Invalid key configuration.' }
    if (-not $Endpoint.IsAbsoluteUri -or ($Endpoint.Scheme -ne 'https' -and -not ($Endpoint.Scheme -eq 'http' -and ($Endpoint.IsLoopback -or $AllowInsecureHttp))) -or $Endpoint.UserInfo -or $Endpoint.Query -or $Endpoint.Fragment -or -not $Endpoint.AbsolutePath.EndsWith('/files')) { throw 'Invalid endpoint; external HTTP requires -AllowInsecureHttp.' }
    $file = Get-Item -LiteralPath $FilePath
    if ($file.PSIsContainer -or $file.Length -eq 0) { throw 'Invalid file.' }

    # Redirects are disabled so X-Api-Key cannot be forwarded to another host.
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    if ($Endpoint.IsLoopback) { $handler.UseProxy = $false }
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(2)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $Endpoint)
    $request.Headers.Add('X-Api-Key', $key)
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $request.Content = $form
    $part = [System.Net.Http.StreamContent]::new([System.IO.File]::OpenRead($file.FullName))
    $part.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
    $form.Add($part, 'file', $file.Name)
    $failureCode = 'upload_failed'
    $failureMessage = 'Upload falhou. Verifique o status HTTP; repetir pode criar outro arquivo.'
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    if ([int]$response.StatusCode -ne 201) {
        [ordered]@{ error = [ordered]@{ code = $failureCode; message = $failureMessage; httpStatus = [int]$response.StatusCode } } | ConvertTo-Json -Compress
        exit 1
    }
    $result = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    $failureCode = 'invalid_response'
    $failureMessage = 'Resposta inesperada. Confira se o endpoint usa a URL publica canonica configurada.'
    $url = [uri]$result.url
    $allowedTypes = @('image/png', 'image/jpeg', 'image/gif', 'image/webp', 'video/mp4', 'video/webm', 'video/ogg', 'audio/mp4', 'audio/wav', 'audio/ogg', 'audio/mpeg', 'application/pdf', 'application/octet-stream')
    if ($result.id -cnotmatch '^[0-9a-f]{64}$' -or $result.contentType -notin $allowedTypes -or -not $url.IsAbsoluteUri -or $url.UserInfo -or $url.Query -cnotmatch '^\?key=[A-Za-z0-9_-]{43}$' -or $url.Fragment -or $url.Scheme -ne $Endpoint.Scheme -or $url.Authority -ne $Endpoint.Authority -or $url.AbsolutePath -cne ($Endpoint.AbsolutePath + '/' + $result.id)) { throw 'Invalid response.' }

    $failureCode = 'verification_failed'
    $failureMessage = 'O upload retornou sucesso, mas o GET da URL falhou. Nao repita o POST automaticamente.'
    $get = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $url)
    # Public capability URL: GET deliberately has no API key.
    $verification = $client.SendAsync($get, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    if ([int]$verification.StatusCode -ne 200 -or $verification.Content.Headers.ContentType.MediaType -ne $result.contentType -or -not $verification.Headers.Contains('X-Content-Type-Options') -or ($verification.Headers.GetValues('X-Content-Type-Options') -join ',') -ne 'nosniff') { throw 'Verification failed.' }
    $body = $verification.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    if ($body.ReadByte() -lt 0) { throw 'Empty file.' }
    [ordered]@{ id = $result.id; url = $result.url; contentType = $result.contentType } | ConvertTo-Json -Compress
}
catch {
    # Never print exceptions, headers, environment values or server response bodies.
    [ordered]@{ error = [ordered]@{ code = $failureCode; message = $failureMessage } } | ConvertTo-Json -Compress
    exit 1
}
finally {
    if ($verification) { $verification.Dispose() }
    if ($get) { $get.Dispose() }
    if ($response) { $response.Dispose() }
    if ($request) { $request.Dispose() }
    if ($client) { $client.Dispose() }
    elseif ($handler) { $handler.Dispose() }
}
