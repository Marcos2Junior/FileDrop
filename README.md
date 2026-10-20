# FileDrop: arquivos para agentes

O FileDrop entrega links de arquivos que o agente já criou. São duas partes:

| Onde | O que instalar | Função |
| --- | --- | --- |
| Servidor Windows com IIS | API ASP.NET Core | Recebe o arquivo por HTTPS, criptografa e disponibiliza o link por sete dias |
| Computador onde o agente trabalha | MCP local e instrução para o Codex | Usa a entrega nativa primeiro; na primeira falha técnica clara, passa ao FileDrop naquele canal/capacidade |

A API aceita qualquer formato, até **100 MiB por arquivo**. Cada arquivo tem uma chave aleatória própria, presente apenas na URL devolvida. **Quem possui o link pode abrir e repassar o arquivo. Sem o link não há recuperação.** A chave de POST é outra credencial, usada apenas para enviar.

O MCP não captura screenshots nem observa ferramentas nativas sozinho. A instrução instalada orienta o agente a consultar e registrar os resultados. A primeira falha técnica ativa o fallback por 24 horas e durante a tarefa ativa. Negação de permissão, cancelamento, confirmação pendente ou entrega incerta não autorizam trocar de canal.

## 1. Confira os pré-requisitos

| Máquina | Requisitos |
| --- | --- |
| Para gerar pacotes dos fontes | Windows x64, PowerShell 7.3+, SDK .NET **10.0.401** ou patch posterior da faixa 10.0.4xx, conforme global.json; acesso ao nuget.org |
| Servidor da API | Windows compatível com .NET 9, IIS e **Hosting Bundle .NET 9 x64 atualizado**, instalado após o IIS |
| Cliente MCP | Windows x64, **.NET Runtime 9 x64**, PowerShell 7.3+ e Codex com suporte a MCP stdio |

Os projetos têm alvo **net9.0**. O SDK 10 compila esse alvo; os pacotes dependem do runtime 9 e não incluem o runtime. Para executar os testes também é necessário o ASP.NET Core Runtime 9 x64, incluído no Hosting Bundle 9.

Baixe pelos sites oficiais: [.NET 9 / Hosting Bundle / Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0), [SDK .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [PowerShell](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell-on-windows).

Na consulta de 20/10/2026, o patch .NET 9 atual é 9.0.21 e o suporte termina em **10/11/2026**. Planeje a atualização do alvo antes dessa data. [Política oficial de suporte](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## 2. Gere os pacotes

Este repositório contém **fontes, documentação, scripts e testes**. Não contém executáveis, credenciais ou releases para baixar. Se recebeu um ZIP pronto por outro meio, pode usar esse pacote e pular a compilação.

Baixe o código pelo botão **Code > Download ZIP** do GitHub e extraia. No PowerShell 7, entre na pasta que contém ScreenshotApi.sln e siga [BUILD.md](BUILD.md). Você obterá:

- **FileDropApi-IIS-net9.zip**: arquivos a copiar para o site IIS.
- **FileDropMcp-win-x64-net9.zip**: MCP, instalador, launcher e instrução.

O build não publica no IIS, instala o MCP ou configura credenciais.

## 3. Publique a API no servidor

Siga [SERVER.md](SERVER.md) na ordem: Hosting Bundle, pastas separadas, site/pool, HTTPS, configuração externa da chave/URL, limites e logs. A publicação é manual, feita por você ou pelo administrador.

Antes de instalar o MCP, a API precisa estar acessível pelo cliente em um endereço final como:

    https://files.example.invalid/files

Substitua o domínio de exemplo pelo seu. O certificado deve ser válido e confiável. Use o destino HTTPS definitivo: o MCP não segue redirects. Para a exceção explícita de HTTP externo, consulte [HTTP.md](HTTP.md); habilite a opção tanto no servidor quanto no cliente.

## 4. Instale o MCP no computador do agente

1. Confira no **PowerShell 7**:

       $PSVersionTable.PSVersion
       dotnet --list-runtimes

   Deve aparecer Microsoft.NETCore.App 9.x x64. O SDK só é necessário para compilar; o pacote pronto usa o runtime.

2. Extraia **FileDropMcp-win-x64-net9.zip** em uma pasta definitiva, por exemplo **C:\Tools\FileDropPackage**, fora de Temp/Downloads.

3. Escolha uma pasta **já existente** onde o agente gera os arquivos que você quer entregar. Use a menor pasta necessária, sem junction/symlink. Substitua C:\Agente\Entregas nos comandos pela sua pasta; não autorize o perfil inteiro ou o disco todo.

4. Dentro da pasta extraída, execute o preview, substituindo também o domínio:

       Set-Location 'C:\Tools\FileDropPackage'
       .\Install-FileDropMcp.ps1 -ApiEndpoint 'https://files.example.invalid/files' -Roots @('C:\Agente\Entregas')

   O preview não grava nada. Confira a URL, os roots e os caminhos mostrados.

5. Instale com os mesmos parâmetros e **-Apply**:

       .\Install-FileDropMcp.ps1 -ApiEndpoint 'https://files.example.invalid/files' -Roots @('C:\Agente\Entregas') -Apply

   O padrão instala em **%LOCALAPPDATA%\FileDropMcp**. Acrescenta o MCP ao **config.toml**, instala a skill e acrescenta uma instrução ao **AGENTS.md** do perfil Codex. Preserva o conteúdo e cria backups .bak-* dos arquivos existentes antes de acrescentar. Se CODEX_HOME estiver definido, ele será o perfil usado; caso contrário, %USERPROFILE%\.codex. Outros parâmetros estão em [MCP.md](MCP.md).

   O script recusa instalação/configuração FileDrop já existente. Não é atualizador nem desinstalador automático. Não desative políticas de execução/permissões para contornar uma negativa.

6. Feche as instâncias do Codex que usarão o MCP e inicie pelo launcher:

       & "$env:LOCALAPPDATA\FileDropMcp\Launch-CodexWithFileDrop.ps1"

   Digite a chave de POST quando solicitada, com entrada mascarada: **a mesma chave configurada no servidor**. Ela fica no ambiente do processo, não no TOML nem em argumento. **Repita a entrada em cada nova sessão iniciada pelo launcher.** Não há armazenamento permanente de credencial.

   O padrão inicia o **Codex CLI** e exige codex instalado no PATH. Para um executável de cliente em outro local:

       & "$env:LOCALAPPDATA\FileDropMcp\Launch-CodexWithFileDrop.ps1" -CodexCommand 'C:\CaminhoDoCliente\Codex.exe'

   Esse é apenas um exemplo de caminho: o executável deve existir e aceitar ser iniciado assim. **A abertura do desktop não foi validada.** O launcher não resolve aplicativos da Microsoft Store, atalhos ou AUMIDs automaticamente. Instâncias antigas podem não herdar a chave. O launcher restaura o ambiente do chamador ao terminar.

7. No Codex CLI, use **/mcp** para conferir filedrop e suas quatro ferramentas. Em outros clientes, consulte a lista de MCP do próprio cliente. Em uma entrega solicitada, o agente deve consultar get_delivery_policy e seguir a instrução instalada.

Instalar o MCP não garante que todo agente sempre cumpra a instrução. [MCP.md](MCP.md) explica o contrato das ferramentas e os estados.

## 5. Mude URL, servidor ou chave

No cliente instalado, edite somente os campos necessários de:

    %LOCALAPPDATA%\FileDropMcp\mcp-config.json

Exemplo **sem credenciais**:

    {
      "ApiEndpoint": "https://files.example.invalid/files",
      "Roots": ["C:\\Agente\\Entregas"],
      "StateDirectory": "C:\\CaminhoDaInstalacao\\state"
    }

ApiEndpoint inclui /files. Preserve Roots e StateDirectory ao trocar só o servidor. Feche/reinicie o Codex e o MCP: configuração e chave são lidas ao iniciar. Não rode o instalador novamente para mudar a URL.

No servidor, atualize **Files__PublicBaseUrl**; no cliente, **ApiEndpoint**. A base do servidor não inclui /files. Para trocar a chave de POST, altere **Files__ApiKey** no servidor, recicle o pool e informe a nova chave no launcher. Não salve a chave em mcp-config.json.

Mantendo **domínio/caminho público e os arquivos cifrados**, uma mudança de máquina pode preservar os links pelo restante dos sete dias. Não exponha a pasta de dados como conteúdo estático. Se mudar domínio/caminho, os links já entregues continuam apontando ao endereço antigo: mantenha esse endereço atendendo enquanto necessário. O MCP não segue redirects. Trocar só a chave de POST não invalida links existentes, pois usam chaves individuais.

Para habilitar HTTP externo ou atualizar um MCP já instalado para esse modo, siga [HTTP.md](HTTP.md). Os binários novos também são necessários; o instalador não sobrescreve uma instalação existente.

## 6. Remova a instalação, se necessário

Não há desinstalador automático. Feche Codex/MCP e faça backup antes de editar:

1. No config.toml do perfil usado, remova somente **[mcp_servers.filedrop]** e **[mcp_servers.filedrop.env]**, com seus campos.
2. No AGENTS.md, remova somente o trecho entre **FILEDROP-DELIVERY-BEGIN** e **FILEDROP-DELIVERY-END**.
3. Remova a skill **skills\filedrop-delivery** e a pasta de instalação escolhida, após revisar se há dados que quer preservar.
4. Os backups **config.toml.bak-*** e **AGENTS.md.bak-*** permitem comparar/restaurar o estado anterior. Restaurar um backup inteiro pode apagar alterações posteriores: compare e mescle se necessário.

Excluir o estado não é procedimento para escapar de negativas ou entregas incertas. Desinstalar o MCP não apaga arquivos do servidor; a retenção da API continua.

## Se algo não funcionar

| Sintoma | O que conferir |
| --- | --- |
| 401 | A chave do launcher deve coincidir com Files__ApiKey. Reinicie MCP/recicle o pool após trocar |
| 413, 404.13 ou bloqueio antes do JSON | Máximo 100 MiB por arquivo; IIS/proxy devem permitir corpo de pelo menos 104923136 bytes |
| Certificado/redirect | Use HTTPS final com certificado válido para o domínio; corrija cadeia/DNS. Não desative validação TLS |
| API/MCP não inicia ou IIS 500.30/500.31 | Runtime 9 x64/Hosting Bundle 9 e campos obrigatórios; mantenha patch atualizado. Sem chave válida a API não inicia |
| Acesso negado à pasta | Root existente sem links no cliente; no servidor, permissões mínimas do pool na pasta dedicada. Não aumente privilégios do processo |
| Link retorna 404 | Link errado, adulterado, ausente ou expirado. Não há recuperação sem o segredo da URL |
| Fallback após 24h | A tarefa ativa permanece fixada até end_delivery_task. Depois, uma tarefa nova pode tentar o nativo uma vez. Não apague o estado |
| Entrega incerta | Verifique se o envio anterior chegou antes de repetir ou trocar de canal |
| Preview acusa instalação existente | Edite a configuração existente ou siga a remoção manual; não sobrescreva perfil/estado |

## Limites e documentação técnica

Uma transferência por processo da API; concorrentes recebem 429. Reserve algumas centenas de MiB de memória para arquivos máximos e acompanhe o disco. Sem Range, antivírus, painel, banco, usuários ou deduplicação. Arquivos ativos/desconhecidos são baixados como attachment.

A exclusão de sete dias depende da API rodando; GET bloqueia expirados mesmo entre varreduras. Não elimina backups nem faz apagamento forense. A criptografia não protege contra administrador monitorando memória/processo, dumps ou vazamento no cliente. Logs IIS/proxy/APM e conversas podem expor links se armazenados; siga SERVER.md.

- [API.md](API.md): contrato HTTP, criptografia, erros e retenção.
- [MCP.md](MCP.md): ferramentas, política e parâmetros do instalador.
- [BUILD.md](BUILD.md): build dos dois pacotes e testes.
- [AGENTS.md](AGENTS.md): orientação para agentes neste repositório.
