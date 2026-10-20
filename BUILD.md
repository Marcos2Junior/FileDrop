# Desenvolvimento

Para instalar e usar, veja o [README](README.md). Os pacotes prontos estão em [packages](packages).

## API

O alvo é `net9.0`. Requer Windows x64, SDK **10.0.401** ou patch 10.0.4xx compatível com `global.json`, e runtime/ASP.NET Core **9 x64**. O NuGet usa somente nuget.org, com lockfiles versionados.

```powershell
dotnet restore ScreenshotApi.sln --locked-mode
dotnet build ScreenshotApi.sln -c Release --no-restore -m:1 -nr:false -p:BuildInParallel=false
dotnet test --solution ScreenshotApi.sln -c Release --no-build --no-restore
```

Alternativa: execute diretamente o runner xUnit v3 compilado, que contém os mesmos testes:

```powershell
dotnet tests/ScreenshotApi.Tests/bin/Release/net9.0/ScreenshotApi.Tests.dll -noLogo -result-xml TestResults/api-tests.xml
```

A API tem 70 testes aprovados na última execução, sem mudança de código nesta atualização do cliente. Os testes HTTP usam TestServer em processo, sem porta real.

.NET 9 tem suporte até **10/11/2026**; planeje atualizar o alvo e o Hosting Bundle. [Política oficial](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Cliente e onboarding

Requer Windows PowerShell 5.1, .NET Framework 4.7.2+ e execução de scripts autorizada:

```powershell
powershell.exe -NoProfile -File tests/skill/Test-FileDrop.ps1
powershell.exe -NoProfile -File tests/skill/Test-FileDropSetup.ps1
```

O primeiro cobre política de fallback e upload/GET com transporte fake. O segundo verifica gravação, chave DPAPI, preservação da configuração/estado, rotação, validação e concorrência. As fixtures têm somente credenciais fictícias e não abrem a janela nem acessam a rede.

Validação da versão 1.2.0: **56 asserções do helper e 42 do onboarding aprovadas** no Windows PowerShell 5.1, com fixtures isoladas, credenciais fictícias e transporte fake. A execução ocorreu fora do sandbox após autorização específica, com RemoteSigned somente nos processos de teste; nenhuma política permanente foi alterada. Sintaxe e conteúdo dos pacotes também foram conferidos. A janela instalada ainda exige verificação manual.

Não altere a política de execução para rodar testes sem autorização correspondente. Rede real, IIS, interface gráfica instalada e criação de junction real exigem validação separada. O onboarding nativo só fica verificado no host depois de instalar o pacote e executar sua configuração.

## Atualizar os dois pacotes distribuídos

Os nomes estáveis ficam versionados junto com os fontes:

| Arquivo | Conteúdo |
| --- | --- |
| [Plugin](packages/FileDrop-plugin.zip) | Manifesto, skills de entrega/setup e scripts |
| [API IIS](packages/FileDropApi-IIS.zip) | Publicação ASP.NET Core, dependente do Hosting Bundle 9 |
| [Hashes](packages/SHA256SUMS.txt) | SHA-256 dos dois ZIPs |

A versão do plugin fica em `plugin.json`. Ao lançar uma alteração, incremente-a e regenere o ZIP.

Para empacotar o plugin a partir da raiz, em uma sessão com execução autorizada:

```powershell
Compress-Archive -LiteralPath plugin.json,skills -DestinationPath packages/FileDrop-plugin.zip -Force
```

Esse ZIP contém somente o manifesto e os arquivos das skills. Não coloque configuração, chaves, estado, logs ou dependências em `skills`.

Se o código da API mudou, publique em uma pasta de saída nova, fora de dados/configuração reais:

```powershell
dotnet publish src/ScreenshotApi/ScreenshotApi.csproj -c Release --no-restore -o artifacts/publish/api
Compress-Archive -Path artifacts/publish/api/* -DestinationPath packages/FileDropApi-IIS.zip -Force
```

Preserve o `web.config` gerado. Atualizar somente o cliente não exige gerar/publicar outra API. Após alterar pacotes, atualize `packages/SHA256SUMS.txt` com `Get-FileHash -Algorithm SHA256` e confira o conteúdo dos ZIPs antes do commit.

Somente esses dois ZIPs de distribuição são permitidos no Git. Perfis de publicação, `.user`, caches, builds intermediários, arquivos recebidos e credenciais permanecem excluídos. Gerar pacotes não instala o plugin nem publica a API.
