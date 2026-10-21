# Configurar FileDrop

Requisitos: API acessível, Windows x64, Windows PowerShell 5.1, .NET Framework 4.7.2+ e execução de scripts autorizada. A janela precisa de um desktop Windows interativo; não roda em um executor somente de terminal sem sessão gráfica.

## Instalação e primeira configuração

Instale o ZIP de plugin pela tela **Plugins > Adicionar > Novo plugin**, sem extrair. Execute **Setup/Configurar** ou peça, em uma conversa com FileDrop selecionado: `Execute o onboarding do FileDrop.`

O configurador incluído é:

```powershell
powershell.exe -NoProfile -STA -File "<plugin>/skills/filedrop/scripts/Configure-FileDrop.ps1"
```

`<plugin>` é a pasta dos arquivos instalados, não um caminho literal. O agente deve localizar o arquivo empacotado. O usuário digita a chave diretamente no campo mascarado; não deve enviá-la na conversa ou em argumentos.

Na janela, informe o endereço final com `/files` e a chave de POST. Se o endereço for HTTP, marque a opção explícita e habilite também `Files__AllowInsecureHttp=true` na API. HTTP expõe chave, conteúdo e segredo do link no tráfego. Não há cadastro de pastas: o agente deve enviar somente o arquivo explicitamente solicitado e autorizado pelo usuário.

**Salvar** valida e grava a configuração, sem testar rede ou enviar arquivos. **Cancelar** não grava. O configurador não instala runtime, altera políticas, permissões ou variáveis globais.

## Execução no Windows

O agente consulta `Get-ExecutionPolicy -List` no Windows PowerShell 5.1 antes da primeira execução, reutilizando uma consulta recente da mesma tarefa. Se scripts já puderem executar, usa o comando normal.

Quando a política efetiva for `Restricted`, com `MachinePolicy` e `UserPolicy` indefinidas, pode usar `-ExecutionPolicy RemoteSigned` somente no processo que executa `Configure-FileDrop.ps1` ou `filedrop.ps1`, desde que o usuário tenha autorizado essa liberação. A autorização vale para configuração e uso do FileDrop na conversa; não é necessário pedir novamente a cada comando. Se ainda não houver autorização, peça uma vez, explicando o alcance limitado.

Exemplo de abertura nesse caso:

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy RemoteSigned -File "<plugin>/skills/filedrop/scripts/Configure-FileDrop.ps1"
```

Nas chamadas de entrega, acrescente a mesma opção antes de `-File`. A política temporária termina com o processo; não use `Set-ExecutionPolicy` em `CurrentUser`/`LocalMachine`, `Bypass`, `Unrestricted` ou mudanças de GPO. Ela não autoriza executar scripts externos ao FileDrop, mudar permissões, desativar controles de segurança ou substituir uma recusa de operação. Não desbloqueie arquivos baixados automaticamente. Se GPO, assinatura ou acesso impedirem a execução, relate o motivo e pare; não tente outra forma de contornar o bloqueio.

O botão **Configurar agora** do Codex inicia a skill numa conversa; o formulário local abre quando o agente executa o comando. Se o clique não iniciar a conversa, selecione FileDrop e peça `Execute o onboarding do FileDrop.` Não presuma que esse problema do botão foi causado pelo PowerShell. Uma instância em andamento deve concluir antes de outra tentativa.

## Onde os dados ficam

Por padrão, em `%LOCALAPPDATA%\FileDropSkill`:

| Arquivo/pasta | Conteúdo |
| --- | --- |
| `config.json` | Endereço e referência ao arquivo de chave |
| `api-key-*.dpapi` | Chave cifrada com DPAPI para o usuário atual do Windows |
| `state` | Hashes, horários e estados de entrega; `*.retries.json` guarda contadores e histórico por arquivo, sem links, chaves, caminhos ou conteúdo |
| `setup.lock` | Coordenação de gravações do configurador |

A chave protegida depende do perfil do Windows; não basta copiar seu arquivo para outro usuário ou computador. Refaça o onboarding nesse caso. A proteção não impede acesso por processos do mesmo usuário ou por administradores que monitorem memória.

## Trocar URL ou chave

Execute novamente o onboarding. A URL atual aparece; a chave nunca é exibida. Deixe o campo de chave vazio para manter a credencial existente. Digite uma nova chave para trocá-la.

A próxima chamada usa a nova configuração. O estado de fallback permanece. A troca de chave cria outro arquivo protegido e preserva o anterior; revise arquivos antigos antes de removê-los manualmente. Se a gravação da configuração falhar depois da proteção, pode restar um arquivo cifrado sem referência.

A variável `FILE_API_KEY`, quando herdada pelo Codex, tem prioridade. Se você a utiliza, mudar apenas o campo da janela não substitui essa variável; altere-a pelo mecanismo autorizado e reinicie o Codex para herdar o novo ambiente.

Alterar a URL afeta novos uploads. Links já entregues continuam apontando ao endereço antigo. Trocar a chave de POST não muda os segredos individuais dos links.

## Opções avançadas e compatibilidade

A configuração é criada automaticamente. Para instalações manuais anteriores, os nomes exatos continuam:

```json
{
  "ApiEndpoint": "https://files.example.invalid/files",
  "AllowInsecureHttp": false
}
```

Configurações antigas com `Roots` continuam funcionando: o campo é ignorado, não validado nem apagado ao usar/editar a configuração. Novas configurações não o incluem. Não é necessário recriar credenciais ou apagar estado para atualizar.

O arquivo enviado deve existir, ser regular, ter de 1 byte a 100 MiB e usar caminho local absoluto, sem ADS, symlinks ou junctions. Não há allowlist de pastas. A leitura permitida pelo sistema não autoriza transmissão: essa decisão é do usuário e do agente, para cada entrega solicitada. A URL não aceita usuário, senha, query, fragmento ou redirecionamento.

O onboarding acrescenta `SecretFile` com o caminho do arquivo protegido e `SecretProtection: "CurrentUserDpapi"`. Não edite o arquivo cifrado nem troque o modo para tentar decodificá-lo.

Instalações antigas com chave de uma linha em `api-key.txt` continuam funcionando, assim como `SecretFile` apontando para outro arquivo de texto local. A proteção padrão dessas instalações é `None`; digitar uma chave no onboarding migra o uso para DPAPI e preserva o arquivo antigo. Mantenha qualquer arquivo de segredo fora do Git/sincronização e com acesso restrito.

`FILEDROP_SKILL_CONFIG` pode indicar outro caminho absoluto de configuração. O configurador não salva dentro de plugin ou repositório Git. Se essa variável mudar, reinicie o Codex. Não coloque segredos no JSON.

Para instalar somente a skill manualmente, copie a pasta `skills/filedrop` dos fontes para `%USERPROFILE%\.agents\skills\filedrop` e execute o mesmo configurador pelo caminho copiado. Não mantenha essa cópia junto com o plugin instalado.

## Verificação e erros

Para conferir apenas a sintaxe do JSON:

```powershell
Get-Content "$env:LOCALAPPDATA\FileDropSkill\config.json" -Raw | ConvertFrom-Json | Out-Null
```

Isso não testa credencial, rede ou entrega. O onboarding também não faz upload. Não invente falha nativa para testar FileDrop.

O helper confere caminho final, limite de 100 MiB, origem da resposta, expiração e SHA-256 do download integral. Usa streaming no cliente e timeout total de quatro minutos por tentativa. A API precisa retornar `X-FileDrop-Expires-At`. A saída é JSON; falha retorna código 1. Não há redirects. Cada comando faz no máximo um POST; a skill pode repetir uma falha técnica dentro da política abaixo.

Estado ocupado/corrompido ou falha de gravação bloqueia nova ação; investigue sem apagar o estado para forçar reenvio. Para `Restricted`, siga a liberação temporária autorizada descrita acima. Outros bloqueios de execução exigem resolver a causa, sem novas tentativas equivalentes.

## Tentativas e retomada

Uma falha técnica permite até **três tentativas totais por arquivo na mesma tarefa**, incluindo a inicial. Cada repetição aguarda no mínimo **dois minutos após o término da tentativa anterior**. O helper retorna `attempts`, `maxAttempts` e `nextAttemptAt`; não dorme nem abre um loop de POST. A skill espera o prazo e chama novamente. Um arquivo com erro não consome o limite dos outros arquivos nem bloqueia a tarefa inteira.

Os contadores são gravados atomicamente **antes do POST**, sob o mesmo lock de execução. Reiniciar o helper não zera o limite. O histórico guarda hashes do TaskId/caminho, horários, resultado incerto ou confirmado, diagnóstico enumerado e status HTTP. Não contém nomes, links, chaves nem mensagens livres de exceção. Não altere TaskId/caminho nem apague estado para conseguir novas tentativas.

São técnicos: falha de DNS/conexão, timeout e HTTP 408/429/5xx transitório. Recusa de acesso, cancelamento, credencial/arquivo inválido, falha de certificado, redirect, erro de contrato e conteúdo divergente não recebem retry automático. Os campos `stage`, `diagnostic` e `httpStatus` ajudam a identificar a causa sem revelar segredos. O diagnóstico não autoriza alterar infraestrutura ou permissões.

Para consultar uma repetição, mantenha Channel/Capability/TaskId e o caminho original:

```powershell
powershell.exe -NoProfile -File "<skill>/scripts/filedrop.ps1" -Action resume -TaskId "<mesmo-id>" -Path "<mesmo-caminho-absoluto>"
```

`retry_wait` manda aguardar `nextAttemptAt`. `retry_authorization_required` exige nova autorização explícita do usuário. Após essa aprovação, acrescente `-UserAuthorized` ao comando: ele aumenta o limite em **uma tentativa**, sem zerar o contador ou diminuir o intervalo. Repetir a autorização antes de usar essa tentativa não acumula créditos. A permissão de instalar o plugin ou usar `RemoteSigned` não substitui essa nova aprovação de envio. A operação `resume` não envia o arquivo; siga a decisão retornada e use `upload` explicitamente.

Se houver uma URL conhecida de uma tentativa incerta, verifique por GET antes de repetir. Uma falha técnica do GET não comprova ausência. Se não for possível confirmar a entrega, duplicação de arquivos temporários é aceita dentro desta política, e os resultados anteriores permanecem `uncertain`, mesmo após uma tentativa posterior dar certo. Nunca marque `verified_not_delivered` sem prova. `already_delivered` impede outro POST de um envio já verificado. A URL continua somente na saída/contexto do agente, sem ser salva para recuperação.

Se o processo parar durante um envio, a reserva persistida é incerta. Por segurança, o próximo horário permitido fica seis minutos após o início reservado (timeout máximo de quatro minutos mais dois de intervalo). Em tarefas antigas da versão 1.3.0 sem registro de tentativas, o agente só pode adotar o arquivo pendente quando o contexto confiável identificar esse arquivo e o usuário autorizar: use `resume` com `-Path` e `-UserAuthorized`. O helper registra uma tentativa anterior incerta com `legacy_unknown_time` e aguarda dois minutos a partir dessa observação; não inventa o horário original nem uma confirmação de ausência.

Após `denied`, `cancelled` ou `pending_confirmation`, só retome quando houver nova autorização explícita do usuário na conversa para aquela entrega. O agente mantém os mesmos Channel, Capability e TaskId e executa:

```powershell
powershell.exe -NoProfile -File "<skill>/scripts/filedrop.ps1" -Action resume -TaskId "<mesmo-id>" -Path "<mesmo-caminho-absoluto>" -UserAuthorized
```

Passe também `-Channel` e `-Capability` se usou valores diferentes dos padrões. A flag declara que o agente conferiu a autorização no contexto confiável; o helper não verifica mensagens nem recebe evidência textual como argumento. Sem ela, a negação/cancelamento/confirmação pendente continua bloqueando. A causa da recusa ainda precisa ser resolvida. A retomada não envia arquivo nem altera o prazo de fallback de 24 horas. Incerteza nativa continua exigindo verificação; o histórico de incertezas FileDrop é preservado ao retomar ou encerrar a tarefa.

## Remover

Encerre as tarefas e desinstale FileDrop pelo menu de plugins; na instalação manual, remova a pasta da skill. Após revisão, você pode remover sua configuração, credenciais e estado locais. Isso não altera a API nem apaga arquivos do servidor.
