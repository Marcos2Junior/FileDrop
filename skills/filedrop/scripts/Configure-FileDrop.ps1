#requires -Version 5.1
[CmdletBinding()]
param([string]$Config=$(if($env:FILEDROP_SKILL_CONFIG){$env:FILEDROP_SKILL_CONFIG}else{Join-Path $env:LOCALAPPDATA 'FileDropSkill\config.json'}))
$ErrorActionPreference='Stop'
$setupConfigPath=$Config
. (Join-Path $PSScriptRoot 'filedrop.ps1')
$Config=$setupConfigPath

function Stamp-FDSetup([string]$File){
    $full=Local-FD $File
    if(![IO.File]::Exists($full)){return 'absent'}
    if(([IO.FileInfo]$full).Length -gt 65536){Fail-FD 'configuration_invalid'}
    return Hash-FD ([IO.File]::ReadAllText($full))
}
function Read-FDSetup([string]$File){
    $stamp=Stamp-FDSetup $File
    $values=@{ApiEndpoint='';Roots=@();AllowInsecureHttp=$false}
    if($stamp -ne 'absent'){
        try{
            $raw=$script:FDJson.DeserializeObject([IO.File]::ReadAllText((Local-FD $File)))
            if($raw -isnot [Collections.IDictionary]){Fail-FD 'configuration_invalid'}
            $values=StateMap-FD $raw
        }catch{Fail-FD 'configuration_invalid'}
        foreach($field in $values.Keys){if($field -notin @('ApiEndpoint','Roots','AllowInsecureHttp','SecretFile','SecretProtection')){Fail-FD 'configuration_invalid'}}
    }
    return @{Stamp=$stamp;Values=$values}
}
function Save-FDSetup([string]$File,[string]$Endpoint,[string[]]$Roots,[bool]$AllowHttp,[string]$ApiKey,[string]$ExpectedStamp){
    $script:FDCode=$null;$gate=$null;$clear=$null
    try{
        $full=Local-FD $File;$dir=[IO.Path]::GetDirectoryName($full)
        $package=$PSScriptRoot
        $possibleRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
        if([IO.File]::Exists((Join-Path $possibleRoot 'plugin.json'))){$package=$possibleRoot}
        if(Inside-FD $full $package){Fail-FD 'configuration_location_invalid'}
        for($item=[IO.DirectoryInfo]::new($dir);$item;$item=$item.Parent){
            if([IO.Directory]::Exists((Join-Path $item.FullName '.git'))){Fail-FD 'configuration_location_invalid'}
        }
        $current=Read-FDSetup $full
        $candidate=$current.Values
        $candidate.ApiEndpoint=$Endpoint.Trim();$candidate.Roots=@($Roots);$candidate.AllowInsecureHttp=$AllowHttp
        $newSecret=$null
        if($ApiKey){
            if($ApiKey.Length -lt 32 -or $ApiKey.Length -gt 256 -or $ApiKey -cmatch '[^!-~]'){Fail-FD 'credentials_unavailable'}
            $newSecret=Local-FD (Join-Path $dir ('api-key-'+[guid]::NewGuid().ToString('N')+'.dpapi'))
            $candidate.SecretFile=$newSecret;$candidate.SecretProtection='CurrentUserDpapi'
        }
        $options=Options-FD $candidate $full
        if(!$ApiKey){$existing=Read-FDKey $options;$existing=$null}
        if([Text.Encoding]::UTF8.GetByteCount((Write-FDJson $candidate)) -gt 65536){Fail-FD 'configuration_invalid'}
        [void][IO.Directory]::CreateDirectory($dir)
        try{$gate=[IO.FileStream]::new((Local-FD (Join-Path $dir 'setup.lock')),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch{Fail-FD 'configuration_busy'}
        if((Stamp-FDSetup $full) -ne $ExpectedStamp){Fail-FD 'configuration_changed'}
        if($newSecret){
            Add-Type -AssemblyName System.Security
            $clear=[Text.Encoding]::UTF8.GetBytes($ApiKey)
            $encrypted=[Security.Cryptography.ProtectedData]::Protect($clear,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
            $stream=[IO.FileStream]::new($newSecret,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try{$stream.Write($encrypted,0,$encrypted.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        }
        Save-FD $candidate $full
        return @{success=$true;code='configured'}
    }finally{
        if($gate){$gate.Dispose()}
        if($clear){[Array]::Clear($clear,0,$clear.Length)}
        $ApiKey=$null
    }
}
function Show-FDSetup([string]$File){
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    if(![Environment]::UserInteractive -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){Fail-FD 'interactive_desktop_required'}
    $initial=Read-FDSetup $File
    [Windows.Forms.Application]::EnableVisualStyles()
    $form=New-Object Windows.Forms.Form
    $form.Text='Configurar FileDrop';$form.ClientSize=New-Object Drawing.Size(690,485)
    $form.StartPosition='CenterScreen';$form.FormBorderStyle='FixedDialog';$form.MaximizeBox=$false
    $form.Font=New-Object Drawing.Font('Segoe UI',10)
    function Label-FDSetup([string]$Text,[int]$Top,[int]$Height=25){
        $label=New-Object Windows.Forms.Label;$label.Text=$Text;$label.SetBounds(20,$Top,650,$Height);$form.Controls.Add($label)
    }
    Label-FDSetup 'Endereco da API (inclua /files no final)' 18
    $endpoint=New-Object Windows.Forms.TextBox;$endpoint.SetBounds(20,46,650,28);$endpoint.MaxLength=2048
    $endpoint.Text=[string]$initial.Values.ApiEndpoint;$form.Controls.Add($endpoint)
    $http=New-Object Windows.Forms.CheckBox;$http.Text='Permitir HTTP (exige opt-in no servidor)';$http.SetBounds(20,81,650,27)
    $http.Checked=($initial.Values.AllowInsecureHttp -eq $true);$form.Controls.Add($http)
    Label-FDSetup 'HTTP expoe chave, arquivos e segredo dos links durante o transporte.' 112
    Label-FDSetup 'Chave de POST (32 a 256 caracteres; oculta)' 145
    $key=New-Object Windows.Forms.TextBox;$key.SetBounds(20,174,650,28);$key.UseSystemPasswordChar=$true;$key.MaxLength=256;$form.Controls.Add($key)
    Label-FDSetup 'Deixe em branco para manter a chave existente. FILE_API_KEY tem prioridade.' 207
    Label-FDSetup 'Pastas permitidas: uma pasta local existente por linha' 240
    $roots=New-Object Windows.Forms.TextBox;$roots.SetBounds(20,270,495,95);$roots.Multiline=$true;$roots.ScrollBars='Vertical'
    $roots.Text=@($initial.Values.Roots) -join [Environment]::NewLine;$form.Controls.Add($roots)
    $browse=New-Object Windows.Forms.Button;$browse.Text='Adicionar pasta';$browse.SetBounds(525,270,145,34);$form.Controls.Add($browse)
    $browse.Add_Click({
        $dialog=New-Object Windows.Forms.FolderBrowserDialog
        try{if($dialog.ShowDialog($form) -eq 'OK'){$roots.AppendText($(if($roots.Text){[Environment]::NewLine}else{''})+$dialog.SelectedPath)}}finally{$dialog.Dispose()}
    })
    $notice=New-Object Windows.Forms.Label;$notice.SetBounds(20,378,650,45);$notice.Text='A chave sera protegida para seu usuario do Windows. Nenhum upload sera feito.';$form.Controls.Add($notice)
    $save=New-Object Windows.Forms.Button;$save.Text='Salvar';$save.SetBounds(450,436,105,32);$form.Controls.Add($save)
    $cancel=New-Object Windows.Forms.Button;$cancel.Text='Cancelar';$cancel.SetBounds(565,436,105,32);$cancel.DialogResult='Cancel';$form.Controls.Add($cancel)
    $form.AcceptButton=$save;$form.CancelButton=$cancel
    $save.Add_Click({
        try{
            $selected=@($roots.Lines | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            $form.Tag=Save-FDSetup -File $File -Endpoint $endpoint.Text -Roots $selected -AllowHttp $http.Checked -ApiKey $key.Text -ExpectedStamp $initial.Stamp
            $form.DialogResult='OK';$form.Close()
        }catch{
            $messages=@{
                configuration_invalid='Confira endereco e pastas existentes (sem links de pasta).'
                credentials_unavailable='Informe uma chave valida, ou mantenha uma credencial existente valida.'
                configuration_changed='A configuracao mudou enquanto a janela estava aberta. Reabra o configurador.'
                configuration_busy='Outra configuracao esta em andamento. Aguarde.'
                configuration_location_invalid='A configuracao deve ficar fora do plugin e de repositorios Git.'
                path_not_allowed='Use caminhos locais absolutos, sem symlinks, junctions ou caminhos de rede.'
            }
            $notice.Text=if($messages.ContainsKey([string]$script:FDCode)){$messages[$script:FDCode]}else{'Nao foi possivel salvar. Confira acesso a pasta de configuracao.'}
            $notice.ForeColor=[Drawing.Color]::DarkRed;$key.Clear()
        }
    })
    try{
        if($form.ShowDialog() -eq 'OK'){return $form.Tag}
        return @{success=$false;code='cancelled'}
    }finally{$key.Clear();$form.Dispose()}
}
if($MyInvocation.InvocationName -ne '.'){
    [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
    try{$result=Show-FDSetup $Config}catch{$result=@{success=$false;code=$(if($script:FDCode){$script:FDCode}else{'configuration_failed'})}}
    Write-FDJson $result
    if(!$result.success){exit 1}
}
