# Gerar pacotes e executar testes

Use Windows x64 e PowerShell 7.3+. Instale manualmente o SDK **10.0.401** ou patch 10.0.4xx posterior, respeitando global.json. Os projetos são net9.0. Para testes, instale também .NET Runtime 9 e ASP.NET Core Runtime 9 x64; para executar os pacotes, veja README.md.

Os comandos abaixo partem da raiz dos fontes. A restauração usa somente nuget.org e os lockfiles versionados. Não precisa de ferramenta dotnet global. A pasta artifacts é ignorada pelo Git.

## 1. Restaurar e compilar

    dotnet --version
    dotnet restore ScreenshotApi.sln --locked-mode
    dotnet build ScreenshotApi.sln -c Release --no-restore -m:1 -nr:false -p:BuildInParallel=false

Pare se qualquer comando falhar. Não publique pacotes de build incompleto.

## 2. Testar

    dotnet test --solution ScreenshotApi.sln -c Release --no-build --no-restore

Se o ambiente não permitir o IPC/named pipe do runner integrado, as mesmas suítes podem ser executadas diretamente, depois do build:

    New-Item -ItemType Directory -Path .\TestResults -Force | Out-Null
    dotnet .\tests\ScreenshotApi.Tests\bin\Release\net9.0\ScreenshotApi.Tests.dll -noLogo -result-xml .\TestResults\api-tests.xml
    dotnet .\tests\FileDropMcp.Tests\bin\Release\net9.0\FileDropMcp.Tests.dll -noLogo -result-xml .\TestResults\mcp-tests.xml

Esse runner direto é o executável de testes xUnit v3 compilado, não uma reimplementação dos testes. Ele dispensa a comunicação do comando dotnet test. Use pasta temporária gravável autorizada se o ambiente restringir o TEMP/TMP.

Validação local do opt-in HTTP: **70 testes API passaram; MCP 74 passaram e 1 foi ignorado**, sem falhas. O teste ignorado cria uma junction real e é opt-in via FILEDROP_TEST_JUNCTION=1 somente em ambiente já autorizado. A criação foi negada neste ambiente e não foi repetida. Os demais testes de caminhos passaram.

Cobertura: roundtrip binário, tipos/limites, ciphertext e AES-GCM, adulteração/truncamento, expiração/limpeza com relógio controlado; política de 24h/concorrência/negação/incerteza, HTTP mock, paths, stdio real e instalação em perfil fictício. O teste stdio inicia o processo por stdin/stdout, descobre e chama ferramentas. HTTP usa mocks/TestServer, sem depender de porta local.

Não foi validado IIS real, acesso HTTP pela rede ou abertura do Codex desktop. Houve uma negação local socket10013 antes do HTTP; a camada que a causou não foi identificada e a operação não foi repetida. O teste de instalador nunca usa o perfil Codex real.

## 3. Publicar os binários em pastas novas

Escolha pastas de saída novas ou vazias para evitar resíduos de compilações anteriores. Estes comandos geram binários localmente, sem iniciar API/MCP e sem instalar nada:

    dotnet publish .\src\ScreenshotApi\ScreenshotApi.csproj -c Release --no-restore -o .\artifacts\api-package
    dotnet publish .\src\FileDropMcp\FileDropMcp.csproj -c Release --no-restore -p:UseAppHost=true -o .\artifacts\mcp-package\server

Em Windows x64, o apphost do MCP gerado é FileDropMcp.exe. É **framework-dependent**: o runtime 9 não vem embutido. A API também requer Hosting Bundle 9 no IIS. Confira que os comandos terminaram com código zero antes de continuar.

## 4. Completar o pacote MCP e compactar

    Copy-Item -LiteralPath .\integration\Install-FileDropMcp.ps1 -Destination .\artifacts\mcp-package\
    Copy-Item -LiteralPath .\integration\Launch-CodexWithFileDrop.ps1 -Destination .\artifacts\mcp-package\
    Copy-Item -LiteralPath .\integration\skill -Destination .\artifacts\mcp-package\skill -Recurse
    Copy-Item -LiteralPath .\README.md,.\MCP.md,.\API.md,.\SERVER.md,.\BUILD.md,.\HTTP.md -Destination .\artifacts\mcp-package\
    Compress-Archive -Path .\artifacts\api-package\* -DestinationPath .\artifacts\FileDropApi-IIS-net9.zip
    Compress-Archive -Path .\artifacts\mcp-package\* -DestinationPath .\artifacts\FileDropMcp-win-x64-net9.zip

Os arquivos devem ficar diretamente na raiz do ZIP MCP: Install-FileDropMcp.ps1, Launch-CodexWithFileDrop.ps1, server e skill. Não inclua credenciais, configurações reais, estado, dados de usuários ou logs. Preserve o appsettings.json vazio de credenciais do pacote API.

Não rode estes comandos de empacotamento sobre diretórios de instalação reais. Se os ZIPs já existirem, preserve-os e escolha nomes novos em vez de sobrescrevê-los sem revisão.

A publicação manual está em [SERVER.md](SERVER.md). A instalação do MCP começa no passo 4 de [README.md](README.md). Não há release/tag/download de binários criado automaticamente no GitHub.
