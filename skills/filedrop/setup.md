# Configurar FileDrop

Requisitos: API acessível, Windows x64, Windows PowerShell 5.1, .NET Framework 4.7.2+ e execução de scripts autorizada. A janela precisa de um desktop Windows interativo; não roda em um executor somente de terminal sem sessão gráfica.

## Instalação e primeira configuração

Instale o ZIP de plugin pela tela **Plugins > Adicionar > Novo plugin**, sem extrair. Execute **Setup/Configurar** ou peça, em uma conversa com FileDrop selecionado: `Execute o onboarding do FileDrop.`

O configurador incluído é:

```powershell
powershell.exe -NoProfile -STA -File "<plugin>/skills/filedrop/scripts/Configure-FileDrop.ps1"
```

`<plugin>` é a pasta dos arquivos instalados, não um caminho literal. O agente deve localizar o arquivo empacotado. O usuário digita a chave diretamente no campo mascarado; não deve enviá-la na conversa ou em argumentos.

Na janela, informe o endereço final com `/files`, a chave de POST e uma pasta existente por linha. Use **Adicionar pasta** para escolher. Se o endereço for HTTP, marque a opção explícita e habilite também `Files__AllowInsecureHttp=true` na API. HTTP expõe chave, conteúdo e segredo do link no tráfego.

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
| `config.json` | Endereço, pastas permitidas e referência ao arquivo de chave |
| `api-key-*.dpapi` | Chave cifrada com DPAPI para o usuário atual do Windows |
| `state` | Hashes, horários e estados de entrega, sem links, chaves, caminhos ou conteúdo |
| `setup.lock` | Coordenação de gravações do configurador |

A chave protegida depende do perfil do Windows; não basta copiar seu arquivo para outro usuário ou computador. Refaça o onboarding nesse caso. A proteção não impede acesso por processos do mesmo usuário ou por administradores que monitorem memória.

## Trocar URL, chave ou pastas

Execute novamente o onboarding. A URL e as pastas atuais aparecem; a chave nunca é exibida. Deixe o campo de chave vazio para manter a credencial existente. Digite uma nova chave para trocá-la.

A próxima chamada usa a nova configuração. O estado de fallback permanece. A troca de chave cria outro arquivo protegido e preserva o anterior; revise arquivos antigos antes de removê-los manualmente. Se a gravação da configuração falhar depois da proteção, pode restar um arquivo cifrado sem referência.

A variável `FILE_API_KEY`, quando herdada pelo Codex, tem prioridade. Se você a utiliza, mudar apenas o campo da janela não substitui essa variável; altere-a pelo mecanismo autorizado e reinicie o Codex para herdar o novo ambiente.

Alterar a URL afeta novos uploads. Links já entregues continuam apontando ao endereço antigo. Trocar a chave de POST não muda os segredos individuais dos links.

## Opções avançadas e compatibilidade

A configuração é criada automaticamente. Para instalações manuais anteriores, os nomes exatos continuam:

```json
{
  "ApiEndpoint": "https://files.example.invalid/files",
  "Roots": ["C:\\Agente\\Entregas"],
  "AllowInsecureHttp": false
}
```

`Roots` contém de 1 a 32 pastas locais existentes. Caminhos de rede, ADS, symlinks e junctions são rejeitados. A URL não aceita usuário, senha, query, fragmento ou redirecionamento.

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

O helper confere caminho final, limite de 100 MiB, origem da resposta, expiração e SHA-256 do download integral. Usa streaming no cliente e timeout total de quatro minutos. A API precisa retornar `X-FileDrop-Expires-At`. A saída é JSON; falha retorna código 1. Não há redirects nem retry automático.

Estado ocupado/corrompido ou entrega incerta bloqueia nova ação; investigue sem apagar o estado para forçar reenvio. Para `Restricted`, siga a liberação temporária autorizada descrita acima. Outros bloqueios de execução exigem resolver a causa, sem novas tentativas equivalentes.

## Remover

Encerre as tarefas e desinstale FileDrop pelo menu de plugins; na instalação manual, remova a pasta da skill. Após revisão, você pode remover sua configuração, credenciais e estado locais. Isso não altera a API nem apaga arquivos do servidor.
