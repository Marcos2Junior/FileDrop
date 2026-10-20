# Instruções para agentes

Projeto independente: API/testes net9.0. Não misture com Hefyt/outros projetos. Leia README.md. Preserve estrutura pequena; sem UI/banco/usuários/gestão de chaves.

## Consumir

- POST /files: X-Api-Key fornecida pelo usuário/ambiente, multipart/form-data, apenas um arquivo não vazio no campo file. Até 100 MiB por padrão, qualquer formato.
- Use scripts/Upload-File.ps1 com FilePath/Endpoint completos; chave só de FILE_API_KEY. Não gere/persista chave real nem use argumento, arquivo, URL, histórico, log ou Git para a chave.
- JSON 201: id, url, contentType. GET na URL completa sem X-Api-Key. O script já verifica. Não construa URL pelo nome original.
- URL contém segredo aleatório do arquivo. Quem tem link pode ler/repassar; perder URL impede recuperar. X-Api-Key não recupera conteúdo.
- Não persista URL/segredo em logs, fixtures, transcrição, histórico ou Git. Saída deliberada ao consumidor autorizado é permitida. Não registre POST response/Location.
- Tipos desconhecidos/ativos (HTML/SVG/JS/executáveis/Office/ZIP) são attachment. A API não é antivírus e não executa/converte arquivos.
- Sete dias fixos desde upload UTC: GET recusa depois; serviço limpa envelopes reconhecidos ao iniciar/a cada hora. Acesso não renova.
- Cada POST cria novo ID/chave; retry pode duplicar. Após 201 com GET falho, investigue sem reupload automático.
- 400/401/413/415 exigem correção; 404 pode ser segredo errado, ausência, adulteração ou expiração; 429 respeita Retry-After com tentativas limitadas; rede/500 pode ocorrer após gravar.
- GET integral, sem Range/busca temporal garantida. Envie somente conteúdo/destino autorizados; pode haver dados sensíveis.
- HTTPS continua padrão. HTTP externo exige opt-in explícito Files:AllowInsecureHttp na API, AllowInsecureHttp no MCP e -AllowInsecureHttp nos scripts. Leia HTTP.md. Não habilite espontaneamente nem relaxe redirects/origem/validação TLS.

## Desenvolver/publicar

Restaure ScreenshotApi.sln com --locked-mode e rode dotnet test --solution ScreenshotApi.sln -c Release --no-restore. Use só credenciais fictícias e fixtures isoladas. Não execute limpeza sobre dados existentes da máquina.

Não faça push ou deployment sem autorização explícita. O usuário autorizou nesta tarefa somente um novo repositório privado FileDrop na conta marcos2junior, commit e push inicial normal, sem force, releases ou workflows. A publicação no IIS continua pelo usuário; não configure IIS/firewall/ACLs sensíveis. Build local em artifacts. Armazenamento dedicado fora da publicação/webroots.

Não persista segredo de arquivo, crie master key ou faça spill multipart para disco. Logs externos IIS/proxy/APM precisam omitir query/URL secreta, X-Api-Key e resposta POST/Location; documente sem configurar infraestrutura. Criptografia de pasta não protege de administrador monitorando memória/processo, dumps ou vazamento do cliente.

## Complemento MCP aprovado

Leia MCP.md e integration/skill/filedrop-delivery/SKILL.md para entregas explicitamente solicitadas. Consulte get_delivery_policy; nativo primeiro, primeira falha técnica clara ativa fallback pelo escopo por 24h e tarefa ativa. Não repita falha observada para comprovar erro. Negado/cancelado/pendente não vira falha técnica; incerto exige verificar antes de reenviar ou trocar canal. Não altere IDs para escapar de bloqueios.

SDK oficial ModelContextProtocol 2.2.0 em net9.0. Inclusão/restauração dos projetos MCP foi explicitamente autorizada após o escopo original da API. Preserve ZIPs anteriores. Sem features além desse complemento.

Não execute instalador contra perfil real nesta tarefa nem altere config.toml/AGENTS.md/skills globais reais. Teste só perfil fictício em Temp. Não configure credenciais persistentes/reais. publish_file apenas paths nos roots; endpoint/chave não são argumentos. Sem captura/varredura/upload espontâneo/retry automático. Não repetir loopback10013 ou criação de junction negada; teste junction opt-in só se ambiente já permitir, sem elevação.

Suítes API/MCP distintas. No sandbox use runners diretos após build e TEMP/TMP na raiz permitida. Mantenha testes de estado, HTTP mock, stdio e preview/backup. Estado não guarda links, caminhos, nomes, conteúdo, chaves ou erros livres.
