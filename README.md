# FileDrop

Uma **API no servidor** guarda os arquivos; um **plugin no Codex do agente** entrega os links.

A API aceita qualquer arquivo de até **100 MiB**, salva somente conteúdo criptografado e expira o acesso em **sete dias**. Cada arquivo tem uma chave aleatória, presente apenas no link. **Quem tem o link pode ler e compartilhar o arquivo. Sem o link, não há recuperação.**

O agente prefere o envio nativo. Na primeira falha técnica clara, usa FileDrop por **24 horas e durante a tarefa ativa**, sem insistir no método que falhou. Esse intervalo não altera os sete dias de retenção.

## Instalar em três passos

Antes de começar, a API deve estar publicada. Quem administra o servidor segue [SERVER.md](SERVER.md).

O cliente precisa de **Windows x64, Windows PowerShell 5.1, .NET Framework 4.7.2+ e execução de scripts autorizada**. Não precisa compilar nem instalar SDK ou PowerShell 7.

### 1. Baixe e instale o plugin

Baixe [o pacote do plugin](packages/FileDrop-plugin.zip). No GitHub, abra o arquivo e use **Download raw**; outra opção é **Code > Download ZIP** e procurar a pasta `packages`.

No Codex, abra **Plugins > Adicionar > Novo plugin**, escolha o ZIP do plugin inteiro, sem extrair, e conclua. Os [pacotes prontos](packages) fazem parte do repositório; não é necessário procurar arquivos de build na sua máquina.

### 2. Execute a configuração inicial

Abra a configuração inicial do plugin (**Setup/Configurar**, quando exibida). Se não aparecer, selecione o plugin FileDrop em uma conversa e peça: `Execute o onboarding do FileDrop.`

O agente abre uma janela local. Preencha:

- **Endereço da API:** URL final terminando em `/files`.
- **Chave de POST:** a mesma configurada no servidor, digitada diretamente no campo mascarado.
- **Pastas permitidas:** pastas locais existentes de onde o agente poderá entregar arquivos.
- **Permitir HTTP:** marque apenas se o servidor usar HTTP e também estiver configurado para isso.

Clique em **Salvar**. O configurador grava os arquivos necessários automaticamente fora do Git e protege a chave para seu usuário do Windows com DPAPI. Não cole a chave na conversa. Nenhum arquivo é enviado durante o onboarding.

É necessário um desktop Windows interativo. Se o PowerShell estiver `Restricted`, o agente pode usar `RemoteSigned` somente nos processos do FileDrop com sua autorização, sem pedir de novo a cada uso. Isso não altera a política permanente nem supera regras de organização. [Configuração e atualização](skills/filedrop/setup.md).

### 3. Peça uma entrega

Use uma conversa com FileDrop selecionado e peça a entrega do arquivo desejado. O agente tenta o nativo primeiro e, quando o fallback for autorizado pela política, o helper envia o arquivo, confere o download e devolve o link e a expiração.

A skill orienta o agente; ela não captura screenshots nem observa ferramentas nativas sozinha. Somente arquivos solicitados podem ser enviados.

## Pacotes disponíveis

| Para quem | Download |
| --- | --- |
| Usa o Codex | [Plugin com onboarding](packages/FileDrop-plugin.zip) |
| Publica a API no IIS | [API pronta para publicação](packages/FileDropApi-IIS.zip) |
| Confere integridade | [Hashes SHA-256](packages/SHA256SUMS.txt) |

O pacote da API depende do Hosting Bundle .NET 9 x64. Publicação e configuração do IIS são manuais. Se a API já está na versão com HTTP opt-in e header `X-FileDrop-Expires-At`, a atualização do plugin não exige republicá-la.

## Mais informações

- [Servidor IIS](SERVER.md): publicação, variáveis, limite e logs.
- [Configuração do cliente](skills/filedrop/setup.md): trocar URL/chave, instalação manual e opções avançadas.
- [Contrato da API](API.md): POST, GET, criptografia e erros.
- [HTTP explícito](HTTP.md): habilitar nos dois lados.
- [Desenvolvimento](BUILD.md): compilar, testar e atualizar os pacotes.

## Limites e problemas comuns

- Negação de permissão, cancelamento e confirmação pendente não ativam fallback. Entrega incerta precisa ser verificada antes de repetir. Não há retry automático de upload.
- HTTPS é o padrão. HTTP expõe chave, arquivos e segredo dos links no tráfego.
- A limpeza roda ao iniciar a API e periodicamente enquanto ela estiver ativa; o download recusa expirados mesmo entre varreduras.
- Não há antivírus, galeria ou reprodução por intervalos de bytes. A API atende uma transferência por vez; formatos ativos/desconhecidos são oferecidos como download.
- DPAPI protege a chave local em disco; não protege contra processos do mesmo usuário ou administrador monitorando a máquina. No servidor, omita query e segredos dos logs conforme [SERVER.md](SERVER.md).

| Sintoma | Confira |
| --- | --- |
| Scripts desabilitados | Autorizar `RemoteSigned` somente nos processos do FileDrop; GPO/assinatura continuam valendo |
| 401 | Chave do cliente igual a `Files__ApiKey` do servidor |
| 413 | Arquivo até 100 MiB; limite IIS/proxy de pelo menos 104923136 bytes |
| Certificado/redirect | URL final direta e certificado confiável |
| `path_not_allowed` | Arquivo em pasta permitida, existente e sem links de pasta |
| Link 404 | Link completo e arquivo ainda dentro dos sete dias |
| `delivery_uncertain` | Verificar a entrega anterior antes de reenviar |
| Fallback após 24h | A tarefa ativa mantém a escolha até seu encerramento |
