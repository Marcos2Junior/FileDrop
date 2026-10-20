# FileDrop

Entregue arquivos pelo Codex usando uma **API no servidor** e uma **skill no computador do agente**.

A API recebe arquivos de até **100 MiB**, salva somente conteúdo criptografado e devolve um link válido por **sete dias**. Cada arquivo tem uma chave aleatória, presente apenas no link. **Quem tem o link pode abrir e compartilhar o arquivo; perder o link impede recuperá-lo.**

A skill tenta a entrega nativa primeiro. Na primeira falha técnica clara, passa a usar o FileDrop por **24 horas e até encerrar a tarefa ativa**. Esse intervalo controla a escolha da entrega; não altera os sete dias de retenção dos arquivos.

## Comece pela skill: três passos

A API precisa estar publicada e acessível. Se você também administra o servidor, siga primeiro [Publicar a API no IIS](SERVER.md).

O computador do agente precisa de **Windows x64, Windows PowerShell 5.1, .NET Framework 4.7.2 ou superior e execução de scripts autorizada**. Não é necessário compilar a skill nem instalar SDK ou PowerShell 7. Se a política bloquear scripts, obtenha autorização conforme as regras do computador; a skill não altera essa política.

### 1. Adicione o plugin no Codex

Se recebeu `FileDrop-plugin-v1.1.0.zip`, abra **Plugins > Adicionar > Novo plugin**, selecione esse ZIP e conclua a instalação. Ele contém a skill e seu helper; não há processo adicional para iniciar. Use o ZIP inteiro, sem extrair, nessa tela.

`FileDrop-skill-v1.1.zip` é para cópia manual e não tem o manifesto de plugin. Não use esse pacote na tela **Novo plugin**. O [formato oficial de plugin](https://developers.openai.com/plugins/build/plugins) exige `plugin.json` e a skill dentro de `skills/`.

Alternativa manual, se preferir não instalar como plugin:

Baixe os fontes pelo GitHub (**Code > Download ZIP**) e extraia. Copie `skills\filedrop` para:

```text
%USERPROFILE%\.agents\skills\filedrop
```

Se recebeu o pacote `FileDrop-skill-v1.1.zip`, copie a pasta `filedrop` que está dentro dele. Se já existir uma instalação, compare antes de substituir.

Confira se a pasta final contém `SKILL.md`, `setup.md` e `scripts\filedrop.ps1`. Esse é o [diretório pessoal de skills do Codex](https://learn.chatgpt.com/docs/build-skills). Escolha uma das formas de instalação para evitar duas cópias da mesma skill.

### 2. Informe endereço, pastas e chave

Crie `%LOCALAPPDATA%\FileDropSkill\config.json` com este conteúdo, substituindo o endereço e a pasta pelos seus:

```json
{
  "ApiEndpoint": "https://files.example.invalid/files",
  "Roots": ["C:\\Agente\\Entregas"],
  "AllowInsecureHttp": false
}
```

O endereço deve terminar em `/files`. A pasta em `Roots` deve existir: escolha somente as pastas locais cujos arquivos o agente poderá entregar. Links de pasta, como junctions e symlinks, não são aceitos.

Em um editor local, coloque a chave de POST fornecida pelo administrador em `%LOCALAPPDATA%\FileDropSkill\api-key.txt`, em uma única linha. Mantenha esse arquivo acessível somente ao seu usuário e fora do Git ou de pastas sincronizadas. **Não cole a chave no chat, em comandos ou no `config.json`.** A chave deve ser igual à configurada na API.

Se `FILE_API_KEY` já estiver no ambiente herdado pelo Codex, ela tem precedência sobre o arquivo. A configuração não contém chave pronta nem instala um cofre. [Opções e troca de configuração](skills/filedrop/setup.md).

### 3. Abra o Codex e peça uma entrega

Abra uma conversa com o plugin **FileDrop** selecionado. Na instalação manual, invoque `$filedrop` quando necessário. Peça a entrega seguindo a preferência pelo envio nativo. Se não aparecer, reinicie o Codex e confira a instalação do passo 1.

A skill orienta o agente; ela não captura screenshots nem observa as ferramentas nativas sozinha. Quando o FileDrop for usado, o helper envia o arquivo, confere o download e retorna o link e a expiração. Somente arquivos solicitados podem ser enviados.

## Servidor e desenvolvimento

| Preciso de… | Documento |
| --- | --- |
| Publicar/configurar a API no IIS | [SERVER.md](SERVER.md) |
| Instalar, atualizar ou remover a skill | [setup.md](skills/filedrop/setup.md) |
| Entender POST, GET e erros | [API.md](API.md) |
| Habilitar HTTP explicitamente | [HTTP.md](HTTP.md) |
| Compilar, testar ou gerar pacotes | [BUILD.md](BUILD.md) |

Os fontes estão prontos para copiar a skill. Quem precisa gerar o pacote da API deve seguir o guia de build; os ZIPs locais não são publicados automaticamente como downloads no GitHub.

## Limites importantes

- Negação de permissão, cancelamento e confirmação pendente não ativam fallback. Se uma entrega ficar incerta, verifique o resultado antes de repetir; não há retry automático de upload.
- **HTTPS é o padrão.** HTTP exige opt-in no servidor e no cliente e expõe chave, arquivos e segredo dos links no tráfego. Veja [HTTP.md](HTTP.md).
- Arquivos expiram sete dias após o upload. A exclusão ocorre ao iniciar a API e em varreduras enquanto ela roda; o download já recusa expirados entre varreduras.
- Não há antivírus, busca de arquivos, galeria nem reprodução por intervalos de bytes. Tipos ativos ou desconhecidos são oferecidos como download. A API atende uma transferência por vez.
- Criptografia em disco não protege contra acesso à memória do processo ou logs externos. Na publicação, omita segredos e query dos logs conforme [SERVER.md](SERVER.md).

## Se algo não funcionar

| Sintoma | O que conferir |
| --- | --- |
| Scripts desabilitados | Execução autorizada pela política local; não contorne o bloqueio |
| 401 | Chave local igual a `Files__ApiKey` no servidor |
| 413 | Arquivo de até 100 MiB e limite IIS/proxy de pelo menos 104923136 bytes |
| Certificado ou redirect | Endereço final direto e certificado confiável |
| `path_not_allowed` | Arquivo em uma pasta de `Roots`, existente e sem links |
| Link 404 | Link completo, arquivo presente e ainda dentro dos sete dias |
| `delivery_uncertain` | Verifique a entrega anterior antes de enviar novamente |
| FileDrop continua após 24h | A tarefa ativa mantém a escolha até ser encerrada |
