# Instalar e configurar a skill

A API deve estar publicada e acessível antes de usar a skill. O cliente requer **Windows x64, Windows PowerShell 5.1, .NET Framework 4.7.2 ou superior e execução de scripts autorizada**. Não precisa compilar nem instalar SDK ou PowerShell 7.

## 1. Copie a pasta

Copie a pasta `filedrop` deste pacote para:

```text
%USERPROFILE%\.agents\skills\filedrop
```

Se estiver usando os fontes, a pasta de origem é `skills\filedrop`. Confira os três arquivos: `SKILL.md`, `setup.md` e `scripts\filedrop.ps1`. Compare antes de substituir uma instalação existente. A skill usa o [diretório pessoal suportado pelo Codex](https://learn.chatgpt.com/docs/build-skills), sem editar `config.toml` ou instruções globais.

## 2. Configure fora do Git

Crie `%LOCALAPPDATA%\FileDropSkill\config.json`, substituindo os valores fictícios:

```json
{
  "ApiEndpoint": "https://files.example.invalid/files",
  "Roots": ["C:\\Agente\\Entregas"],
  "AllowInsecureHttp": false
}
```

- `ApiEndpoint`: endereço final da API, incluindo `/files`. Não pode conter usuário, senha, query ou fragmento e não deve redirecionar.
- `Roots`: pastas locais existentes e estritamente necessárias para as entregas. Arquivos fora delas, caminhos de rede e symlinks/junctions não são aceitos.
- `AllowInsecureHttp`: mantenha `false` para HTTPS. HTTP externo requer `true` aqui e `Files__AllowInsecureHttp=true` no servidor. HTTP expõe chave, arquivo e segredo do link no tráfego.

Para conferir apenas a sintaxe do JSON, use:

```powershell
Get-Content "$env:LOCALAPPDATA\FileDropSkill\config.json" -Raw | ConvertFrom-Json | Out-Null
```

### Chave de POST

Em um editor local, salve a chave fornecida pelo administrador em `%LOCALAPPDATA%\FileDropSkill\api-key.txt`, somente a chave em uma linha. Ela deve corresponder a `Files__ApiKey` na API. Restrinja o acesso ao seu usuário e mantenha o arquivo fora do Git, sincronização e logs. Não coloque a chave no prompt, em argumentos ou no `config.json`.

O arquivo evita digitar a chave a cada sessão, mas é um segredo local em disco, sem cofre embutido. A skill não cria credenciais nem altera permissões. Se `FILE_API_KEY` já estiver no ambiente herdado pelo Codex, ela tem precedência sobre o arquivo.

Opções para quem precisa: `SecretFile` no JSON pode indicar outro arquivo absoluto de segredo; a variável `FILEDROP_SKILL_CONFIG` pode indicar outro `config.json` absoluto. Não é necessário usar essas opções no caso comum.

## 3. Abra o Codex normalmente

Peça uma entrega usando `$filedrop`. Se a skill não aparecer, reinicie o Codex e confira a pasta instalada. A descoberta da skill não garante que todo agente sempre siga a instrução: ela orienta o fluxo e o helper registra os resultados explicitamente.

O nativo é a primeira opção. Uma falha técnica clara ativa FileDrop por 24h naquele canal/capacidade e durante a tarefa ativa. Negação, cancelamento ou confirmação pendente não autorizam trocar de método. Entrega incerta exige verificar antes de repetir. O helper não captura imagens nem envia arquivos espontaneamente.

Se a política do computador bloquear scripts, pare e obtenha execução autorizada conforme as regras locais. A skill não altera política nem tenta contornar esse bloqueio.

## Atualizar endereço ou chave

Edite `ApiEndpoint` no JSON; a próxima chamada lê novamente. Preserve `Roots` e a pasta `state` ao lado da configuração. Esse estado guarda somente hashes, horários e estados de entrega, sem links, chaves, caminhos ou conteúdo.

Para trocar a chave, altere o arquivo separado ou a variável herdada. Reinicie o Codex se mudar o ambiente de processo. Não é necessário reiniciá-lo por uma mudança no JSON ou no arquivo de chave.

Alterar o endereço só afeta novos uploads. Links já entregues continuam apontando ao endereço antigo. Os arquivos da API expiram sete dias após o upload, independentemente do cooldown de 24h.

## O que o helper confere

Ele confere caminho final, limite de 100 MiB, endereço/origem da resposta, data de expiração e SHA-256 do download integral. Usa streaming no cliente, sem temporário de conteúdo, e timeout total de quatro minutos. A API deve retornar `X-FileDrop-Expires-At`.

A saída é JSON; falha retorna código de saída 1. Não há redirects nem retry automático. Estado ocupado/corrompido ou entrega incerta bloqueia nova ação: investigue sem apagar o estado para forçar reenvio. O link é a credencial de leitura e deve ser apresentado apenas ao consumidor autorizado, sem logs.

## Remover

Encerre as tarefas e remova a pasta instalada da skill. Após revisar, remova também sua configuração e arquivo de segredo local se não forem mais necessários. Isso não altera a API nem apaga os arquivos do servidor.
