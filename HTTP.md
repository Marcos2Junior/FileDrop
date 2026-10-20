# HTTP externo com opt-in

HTTPS continua sendo o padrão. HTTP externo só é aceito após habilitar **AllowInsecureHttp** explicitamente. Não desativa validação de certificados HTTPS nem permite redirects ou troca de origem.

Em HTTP, chave de POST, bytes do arquivo e segredo da URL trafegam sem proteção. A criptografia em disco não protege o tráfego. Use este modo somente quando essa exposição tiver sido aceita.

Os exemplos usam domínio fictício: substitua pelo host/porta definidos por você.

## API no IIS

Publique os **novos binários da API** preservando configuração externa e a pasta de dados. Configure no ambiente do pool/aplicação:

| Nome | Valor |
| --- | --- |
| Files__PublicBaseUrl | http://files.example.invalid:4958 |
| Files__AllowInsecureHttp | true |

A base não inclui /files. Continue configurando Files__ApiKey e Files__StorageDirectory normalmente. Recicle o pool após alterar o ambiente.

Alternativamente, na configuração local não versionada do servidor, mescle **"AllowInsecureHttp": true** dentro de **"Files"**. Não use esse exemplo para substituir o objeto Files inteiro ou apagar os outros campos.

PublicBaseUrl só define os links retornados; não cria binding/listener. Configure o endereço de escuta/binding manualmente no IIS conforme sua publicação. Nenhuma configuração de infraestrutura é feita pelo projeto.

Sem a opção, HTTP externo falha na inicialização. A exceção HTTP loopback preexistente da API é mantida. Userinfo, query e fragmento continuam proibidos na base.

## MCP já instalado

1. Feche Codex e o processo MCP. Faça backup local da instalação/configuração, preservando o estado.
2. Extraia o novo pacote MCP. Atualize **somente o conteúdo de server** da instalação a partir da pasta server do novo pacote. Não execute o instalador novamente sobre instalação existente.
3. Edite o **mcp-config.json** já instalado, normalmente em %LOCALAPPDATA%\FileDropMcp. Mude/adicione somente:

       "ApiEndpoint": "http://files.example.invalid:4958/files",
       "AllowInsecureHttp": true

   São campos dentro do objeto JSON existente. Preserve Roots e StateDirectory exatamente como estão e mantenha JSON válido. Não coloque a chave de POST nesse arquivo.
4. Reinicie Codex/MCP pelo launcher e informe a chave mascarada da sessão normalmente.

Os binários antigos não conhecem a nova opção; atualizar apenas o JSON não basta. O servidor MCP lê o arquivo apontado por FILEDROP_CONFIG ao iniciar.

## Nova instalação MCP

Na raiz do novo pacote extraído, com root já existente:

    .\Install-FileDropMcp.ps1 -ApiEndpoint 'http://files.example.invalid:4958/files' -Roots @('C:\Agente\Entregas') -AllowInsecureHttp

O comando acima é preview. Depois de revisar, instale:

    .\Install-FileDropMcp.ps1 -ApiEndpoint 'http://files.example.invalid:4958/files' -Roots @('C:\Agente\Entregas') -AllowInsecureHttp -Apply

O instalador grava o booleano AllowInsecureHttp no mcp-config.json, sem ler/gravar a chave de POST. O default é false. O restante da instalação está em README.md.

## Script de upload avulso

Use a versão atualizada do script, com FILE_API_KEY fornecida de forma segura:

    .\scripts\Upload-File.ps1 -FilePath 'C:\Agente\Entregas\arquivo.pdf' -Endpoint 'http://files.example.invalid:4958/files' -AllowInsecureHttp

Sem o switch, HTTP externo é rejeitado antes de abrir o arquivo ou construir o cliente HTTP. A exceção loopback preexistente é mantida.

## Origem e retorno

A URL retornada deve manter **esquema, host, porta e caminho** do endpoint configurado, acrescentando somente ID e segredo do arquivo. Mesmo com opt-in, uma resposta que troque HTTP/HTTPS, host ou porta não é seguida pelo MCP/script. Redirects continuam desabilitados; X-Api-Key só acompanha o POST ao endpoint fixo.

Para voltar a HTTPS, configure PublicBaseUrl e ApiEndpoint com a URL HTTPS final e desative AllowInsecureHttp. Reinicie os processos. Links antigos continuam apontando ao esquema/host originais; alterar configuração não reescreve links já entregues.
