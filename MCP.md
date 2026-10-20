# FileDrop MCP

Servidor local **stdio**, C# **net9.0**, SDK oficial **ModelContextProtocol 2.2.0**. Reutiliza a API /files. Não captura screenshots, procura arquivos, observa ou intercepta ferramentas nativas.

## Fluxo para agentes

1. Para uma entrega explicitamente solicitada, consultar get_delivery_policy(channel, capability, task_id), com identificadores opacos e estáveis.
2. mayTryNative=true reserva **uma tentativa nativa**. Registrar o resultado em record_delivery_outcome.
3. Primeira falha técnica clara: fallback naquele canal/capacidade por **24h e durante a tarefa ativa**. Outros canais/capacidades não mudam. Depois do cooldown uma tarefa nova pode reservar um probe; se falhar, renova 24h.
4. denied, cancelled e pending_confirmation **não ativam fallback**. uncertain exige verificar a entrega antes de republicar/trocar canal. Não mudar IDs para escapar do bloqueio.
5. mayPublish=true permite publish_file(path, channel, capability, task_id) para um único arquivo existente autorizado.
6. end_delivery_task só quando a tarefa realmente terminar. Pinagem de tarefa ativa continua além de 24h; o agente precisa encerrá-la. Não há observação automática de ciclo de vida.

Se a falha já foi observada, não repetir o nativo para comprová-la: consultar e registrar o resultado observado quando a tentativa for reservada, ou usar fallback já ativo.

Resultados: technical_failure, delivered, denied, cancelled, pending_confirmation, uncertain, verified_not_delivered. O último exige confirmação de não entrega. Negação registrada não é apagada por technical_failure/delivered. Consulta repetida sem resultado retorna verify_result; lease de cinco minutos impede probes concorrentes entre tarefas. O MCP depende de relatos verdadeiros do agente.

## Instalação pelo usuário

Comece pelo passo a passo em [README.md](README.md). Para gerar um pacote a partir dos fontes, veja [BUILD.md](BUILD.md). Os comandos de instalação abaixo são executados dentro da raiz do pacote MCP extraído, não na pasta integration dos fontes.

Extraia o pacote **FileDropMcp-win-x64-net9.zip** (ou o ZIP versionado recebido separadamente). Precisa de Windows x64, .NET 9 Runtime e PowerShell 7.3+. Nenhum runtime ou dotnet tool global é instalado.

    ./Install-FileDropMcp.ps1 -ApiEndpoint 'https://files.example.invalid/files' -Roots @('C:\Capturas','C:\DocumentosParaEntrega')

O padrão é **preview sem gravação**. Revise os caminhos/bloco TOML; repita com -Apply quando decidir instalar. -Apply -WhatIf também não grava.

O script copia servidor/dependências para LocalAppData\FileDropMcp, configura dados não secretos, faz backup de config.toml e AGENTS.md antes de acrescentar servidor/instrução, e instala a skill filedrop-delivery. Preserva conteúdo existente e recusa entrada/skill/instalação já existente ou TOML inline ambíguo. Não faz atualização/desinstalação automática.

Opções: -InstallDirectory, -CodexHomePath, -PackageRoot; -SkipGlobalInstruction deixa de acrescentar AGENTS.md, mantendo skill e server instructions. Sem instrução global, a escolha automática da skill depende mais do cliente. Só instalar MCP não garante que o agente sempre o consulte.

**Nada foi instalado no perfil real nesta entrega.** O teste de -Apply usa perfil/payload inertes próprios em Temp, sem executar cliente/servidor instalado.

## Credencial

O instalador nunca lê/grava FILE_API_KEY. O TOML só encaminha o nome da variável. O MCP lê a chave uma vez na inicialização, mantém em memória e não a aceita em argumento de ferramenta. Mudança exige reiniciar o MCP.

    ./Launch-CodexWithFileDrop.ps1

O launcher pede chave com entrada mascarada e inicia o Codex CLI com chave só no ambiente do processo. Restaura o ambiente do chamador ao sair. -CodexCommand aceita outro executável do cliente. Feche instâncias antigas e reinicie pelo launcher para herdarem o ambiente. Não use transcrição/depuração que registre ambiente/resultados. Se política/permissão negar, pare; não use bypass.

Também pode fornecer a variável por gerenciador de segredos/processo autorizado. Não há cofre, credencial persistente configurada nem proteção contra administrador/processos capazes de ler memória/ambiente. Chave não vai em argumento, histórico, arquivo ou config Git.

mcp-config.json só contém ApiEndpoint, Roots e StateDirectory. Endpoint fixo HTTPS /files, sem credenciais/query/fragmento. Roots locais, absolutos e existentes. Não há ferramenta para alterar esses parâmetros.

## Upload e resultado

Um arquivo solicitado por chamada, máximo **100 MiB**. Rejeita traversal, UNC/device/ADS, symlink/junction; confere o caminho do handle aberto no Windows. Não há varredura/upload espontâneo. Hash, upload e GET usam buffers pequenos e streaming, sem temporário plaintext.

POST uma vez, sem retry e sem redirects, inclusive mesma origem. X-Api-Key só no POST. Confere origem/caminho/segredo da URL, faz GET sem chave de POST, valida status/MIME/nosniff, tamanho e SHA-256 integral. Timeout total de quatro minutos; sem Range.

Sucesso JSON: success, code=delivered, deliveryStatus, requiresVerification=false, url, name, contentType e expiresAt. A URL só retorna ao consumidor; não é salva pelo servidor MCP. Falha após 201 mantém a URL conhecida e pede verificação, sem reupload. Perda de transporte após Send é conservadoramente incerta. StateError sinaliza falha ao gravar estado sem ocultar negação já conhecida.

A API deste pacote acrescenta **X-FileDrop-Expires-At** com expiração exata, mantendo os três campos JSON anteriores. Publique manualmente o novo pacote API. API antiga sem o header pode receber o arquivo, mas MCP retorna expiry_metadata_missing com URL conhecida e sem repetir POST.

URL é credencial bearer: quem tem o link lê/repassa; perder impede recuperar. Retenção API: sete dias desde upload. Logs externos IIS/proxy/APM e do cliente devem omitir query, chave, POST response/Location e resultados MCP. A conversa recebe o link deliberadamente; sua política de armazenamento também importa.

## Estado e limites

policy.json guarda versão, hashes dos IDs, horários, flags e códigos fixos, sem URL/path/nome/chave/conteúdo/hash de conteúdo/erro livre. Lock de arquivo e rename atômico coordenam processos. Corrupção falha fechado, sem apagar/resetar. Limite 1 MiB; tarefas fechadas sem bloqueios são podadas após 24h. Bloqueios não são apagados para reativar tentativas. Diretório dedicado, sem links; não compartilhar entre máquinas/perfis.

Root autorizado é limite técnico, não autorização automática sobre conteúdo/destino. MCP não substitui consentimento/permissões ou antivírus. Captura/geração continua nas ferramentas existentes.

## Código e testes

SDK instalado 10.0.401; todos os projetos net9.0:

    dotnet restore ScreenshotApi.sln --locked-mode
    dotnet build ScreenshotApi.sln -c Release --no-restore
    dotnet test --solution ScreenshotApi.sln -c Release --no-build --no-restore

Neste sandbox o runner integrado não consegue seu named pipe. Mesmas suítes pelo runner direto, sem esse IPC:

    dotnet tests/FileDropMcp.Tests/bin/Release/net9.0/FileDropMcp.Tests.dll -noLogo -result-xml TestResults/mcp-tests.xml
    dotnet tests/ScreenshotApi.Tests/bin/Release/net9.0/ScreenshotApi.Tests.dll -noLogo -result-xml TestResults/api-mcp-final.xml

Stdio real: handshake/discovery/calls por stdin/stdout, sem rede. HTTP é mock; instalação testada em perfil fictício. Junction de fixture foi negada e não repetida: teste opt-in FILEDROP_TEST_JUNCTION=1 só em ambiente já autorizado, sem elevar permissões. Loopback socket10013 anterior não foi repetido.

.NET 9 foi solicitado explicitamente; suporte termina em 10/11/2026. API continua publicação manual. Build e instalação não configuram IIS, firewall, ACLs ou certificados.

Referências: [SDK C# oficial](https://github.com/modelcontextprotocol/csharp-sdk), [MCP 2.2.0](https://www.nuget.org/packages/ModelContextProtocol/2.2.0), [MCP no Codex](https://learn.chatgpt.com/docs/extend/mcp?surface=cli).
