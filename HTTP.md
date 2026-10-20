# HTTP externo explícito

HTTPS continua padrão. HTTP expõe chave de POST, arquivo e segredo do link no tráfego; a criptografia em disco não protege o transporte.

Na API publicada, configure:

    Files__PublicBaseUrl=http://files.example.invalid:4958
    Files__AllowInsecureHttp=true

Continue configurando Files__ApiKey e Files__StorageDirectory. A base não inclui /files. Recicle o pool depois. PublicBaseUrl só monta links; não cria binding/listener IIS.

No config.json da skill, preserve Roots e altere:

    "ApiEndpoint": "http://files.example.invalid:4958/files",
    "AllowInsecureHttp": true

Esses são campos do objeto existente. Substitua o domínio fictício pelo endereço escolhido. A próxima chamada lê a configuração; não há processo local a reiniciar. Se mudar uma variável de ambiente herdada, reinicie o Codex.

Sem opt-in, o cliente rejeita HTTP. A API preserva a exceção loopback antiga. Userinfo/query/fragmento continuam proibidos na configuração. Respostas devem manter esquema, host, porta e caminho configurados. Redirects seguem bloqueados e nenhuma validação de certificado HTTPS é desligada.

Para retornar a HTTPS, altere as duas URLs e desative AllowInsecureHttp. Links antigos não são reescritos. Veja README.md e skills/filedrop/setup.md.
