# Desenvolvimento e pacotes

Este guia é para quem vai compilar ou modificar o projeto. Para usar a skill pronta, siga o [README](README.md).

## API

Requisitos: Windows x64, SDK **10.0.401** ou patch compatível da linha 10.0.4xx conforme `global.json`, e runtime/ASP.NET Core **9 x64**. O alvo da API é `net9.0`. O `NuGet.Config` usa somente nuget.org; os lockfiles são versionados.

Na raiz do projeto:

```powershell
dotnet restore ScreenshotApi.sln --locked-mode
dotnet build ScreenshotApi.sln -c Release --no-restore -m:1 -nr:false -p:BuildInParallel=false
dotnet test --solution ScreenshotApi.sln -c Release --no-build --no-restore
```

Como alternativa ao runner integrado, o executável xUnit v3 compilado executa os mesmos testes:

```powershell
dotnet tests/ScreenshotApi.Tests/bin/Release/net9.0/ScreenshotApi.Tests.dll -noLogo -result-xml TestResults/api-tests.xml
```

Última validação: **70 testes aprovados**, build sem erros ou avisos. Os testes HTTP usam TestServer em processo, sem porta real. Cobrem contrato, limites, autenticação, criptografia, adulteração, retenção e HTTP opt-in.

.NET 9 tem suporte até **10/11/2026**; planeje atualizar o alvo e o Hosting Bundle. [Política oficial de suporte](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Skill

Não precisa compilar. Requer Windows PowerShell 5.1, .NET Framework 4.7.2+ e execução de scripts autorizada pela política local:

```powershell
powershell.exe -NoProfile -File tests/skill/Test-FileDrop.ps1
```

Última validação: **54 asserções aprovadas** com arquivos fictícios e transporte fake, sem API pública. Inclui cooldown de 24h, tarefa ativa, escopos independentes, HTTP opt-in, upload/GET e hash, erros sem reenvio, caminhos, limite, lock/estado e saída JSON do comando.

Esses testes foram executados com autorização específica somente para os processos de teste. Isso não habilitou scripts para uso normal nem alterou política permanente. Uma nova execução depende da política/autorização vigente; não contorne bloqueios. Rede real, IIS e criação de junction real não foram validados.

## Gerar pacotes

Para preparar a publicação da API, escolha uma pasta de saída nova, sem dados ou configuração real:

```powershell
dotnet publish src/ScreenshotApi/ScreenshotApi.csproj -c Release --no-restore -o artifacts/publish/api
```

Compacte o conteúdo publicado preservando o `web.config` gerado. A instalação IIS é manual conforme [SERVER.md](SERVER.md).

Os pacotes locais atuais são:

| Pacote | Conteúdo |
| --- | --- |
| `FileDropApi-IIS-net9-skill-v1.1.zip` | Binários da API e `web.config`, dependentes do Hosting Bundle 9 |
| `FileDrop-plugin-v1.1.0.zip` | Plugin para a tela **Novo plugin**: manifesto e skill com helper |
| `FileDrop-skill-v1.1.zip` | Pasta `filedrop`: instruções, configuração e helper |
| `FileDrop-api-skill-source-v1.1.zip` | Fontes, testes, scripts e documentação atuais |
| `FileDrop-api-skill-v1.1-SHA256.txt` | Hashes SHA-256 dos quatro ZIPs |

Para o pacote de plugin, compacte somente `plugin.json` e os três arquivos de `skills/filedrop`, preservando esses caminhos dentro de uma única pasta `filedrop` no ZIP. O manifesto usa Agent Plugins 1.0; o código do helper é o mesmo da skill manual. Não inclua servidor, configuração local, credenciais, estado ou dependências.

Para o pacote da skill, compacte somente `skills/filedrop` preservando a pasta `filedrop` na raiz do ZIP. Não inclua configuração de usuário, segredo ou estado.

Para o pacote de fontes, use uma lista explícita de arquivos revisados. Exclua `.git`, `bin`, `obj`, caches, `artifacts`, logs, resultados gerados, perfis de publicação, arquivos `.user`, configuração local, dados e credenciais. Os pacotes não são enviados automaticamente ao GitHub.

Compilar ou empacotar não instala a skill nem publica a API.
