# Contrato e segurança da API

ASP.NET Core net9.0, sem banco, UI, usuários ou gestão de chaves. Configuração e publicação: [SERVER.md](SERVER.md). Build: [BUILD.md](BUILD.md).

## HTTP

**POST /files** recebe X-Api-Key e multipart/form-data com exatamente um arquivo não vazio no campo **file**, sem outros campos. Qualquer formato é aceito até 104857600 bytes (100 MiB), ou limite menor configurado. A autenticação ocorre antes de ler o corpo. Nome/extensão/MIME fornecidos pelo cliente não autorizam exibição inline.

Retorna **201** e JSON com três campos estáveis: **id**, **url**, **contentType**. Location contém a mesma URL. **X-FileDrop-Expires-At** informa a expiração UTC exata em formato roundtrip ISO 8601; a skill exige esse header. O JSON não inclui expiresAt.

O ID é aleatório, 64 caracteres hexadecimais. O link segue o formato:

    https://files.example.invalid/files/{id}?key={segredo-base64url-de-43-caracteres}

Esse formato usa placeholders, não um link válido. A base vem exclusivamente de Files:PublicBaseUrl; Host e X-Forwarded-Host não são usados. HTTPS é o padrão; HTTP externo exige Files:AllowInsecureHttp=true. A exceção loopback preexistente permanece. Veja [HTTP.md](HTTP.md).

**GET /files/{id}?key={segredo}**, sem X-Api-Key, devolve bytes originais após autenticar a criptografia inteira. Ausente/errado/adulterado/expirado retorna **404 JSON genérico** sem conteúdo em claro. Não há listagem nem recuperação de links.

| HTTP | error.code | Ação |
| --- | --- | --- |
| 400 | file_required, empty_file, invalid_multipart, invalid_request | Corrigir corpo |
| 401 | unauthorized | Corrigir credencial |
| 404 | not_found | Conferir link/expiração; não há recuperação |
| 405 | method_not_allowed | Corrigir método/rota |
| 413 | upload_too_large | Reduzir arquivo |
| 415 | multipart_required | Usar multipart |
| 429 | upload_busy, transfer_busy | Respeitar Retry-After com tentativas limitadas |
| 500 | internal_error | Rever servidor; repetir pode duplicar |

Erros têm **error.code** e **error.message**. Componentes anteriores à API, como IIS/proxy, podem produzir resposta diferente; configure PassThrough no IIS.

Cada POST bem-sucedido cria novo ID/chave, mesmo para bytes iguais. Não há idempotência ou deduplicação. Se a resposta se perder após persistir, o resultado é incerto; verifique antes de repetir. Após 201, uma falha no GET não autoriza reupload automático.

## Tipos e download

Assinaturas conservadoras identificam PNG/JPEG/GIF/WebP, MP4/WebM/Ogg, WAV/MP3/M4A e PDF para Content-Disposition inline. Outros conteúdos, inclusive HTML/SVG/JS/executáveis/Office/ZIP, são **application/octet-stream** com **attachment**. A API aceita conteúdo desconhecido: assinatura não valida estrutura/integridade completa. Não há execução, conversão ou antivírus. A exibição inline depende do navegador.

Todas as respostas de arquivo usam nosniff, Cache-Control no-store, Referrer-Policy no-referrer e CSP sandbox/default-src none/base-uri none/frame-ancestors none. Não há Range: GET transfere integralmente, sem busca temporal garantida no player.

## Disco e criptografia

ID e chave são independentes, gerados por RandomNumberGenerator. Cada arquivo usa **AES-256-GCM**, chave de 32 bytes, nonce aleatório de 12 bytes e tag de 16 bytes.

Disco: **{id}.bin**, envelope v1 com magic/version, timestamp UTC de upload em milissegundos, nonce/tag e ciphertext. Metadados (nome sanitizado e Content-Type) e bytes ficam dentro da criptografia. Cabeçalho/timestamp e ID são autenticados como AAD. **Não se salva o segredo no arquivo, nome, metadados, configuração ou logs.** Não há master key fixa; X-Api-Key só autentica POST.

Multipart é lido manualmente em memória com limite, sem IFormFile/ReadFormAsync/spill. Validação e criptografia ocorrem em memória; o temporário .tmp já é cifrado. A gravação termina com rename atômico sem sobrescrever. Erro/cancelamento remove apenas o temporário daquela operação; queda abrupta pode deixar .tmp cifrado inacessível, preservado pela limpeza.

AES-GCM verifica a tag antes de enviar qualquer plaintext no GET. Uma transferência POST ou GET por processo usa o mesmo semáforo, mantido até terminar a resposta; demais recebem 429. O envelope lido também tem limite. Reserve algumas centenas de MiB por processo para arquivos máximos. Não há quota total de disco.

**A URL é a credencial de leitura. Quem a possui pode ler/repassar; perdê-la impede recuperar.** Acesso à pasta/ID não basta. Não protege contra administrador monitorando memória/processo, dumps, cliente comprometido, histórico ou logs externos. A API não registra o link; configure IIS/proxy/APM conforme SERVER.md.

## Retenção fixa de sete dias

O horário é o UTC do upload, sem renovação por acesso e sem opção de retenção em appsettings. GET bloqueia ao completar sete dias. BackgroundService varre no início e a cada hora enquanto a aplicação roda.

Só considera envelopes de nome/estrutura/versão/tamanho/timestamp reconhecidos no diretório dedicado, sem recursão ou links/reparse points. Arbitrários, .tmp e envelopes inválidos são preservados; falha isolada não para a varredura. Sem o segredo, a limpeza não consegue validar a tag GCM: por isso o diretório exclusivo é obrigatório. GET sempre autentica a tag antes de liberar conteúdo.

A limpeza depende da aplicação IIS ativa e retoma na inicialização. Não garante remoção no segundo exato, apagamento forense ou eliminação de backups.

## Cliente da skill

Use a skill e o helper Windows PowerShell5.1 conforme [README.md](README.md) e [setup.md](skills/filedrop/setup.md). Endpoint/roots ficam em arquivo local fora de Git; a chave vem de FILE_API_KEY ou arquivo separado fornecido pelo usuário. O helper coordena a política de fallback, envia uma vez, bloqueia redirects e verifica bytes/hash do GET sem a chave de POST.

A saída JSON contém o link deliberadamente para o consumidor. Não faça transcrição nem persista essa saída. Resultado incerto exige verificar antes de qualquer novo POST.
