# Habilitar HTTP explicitamente

HTTPS é o padrão. HTTP expõe a chave de POST, o arquivo e o segredo do link no tráfego. A criptografia em disco não protege o transporte.

## Servidor

No ambiente da API, configure:

```text
Files__PublicBaseUrl=http://files.example.invalid:4958
Files__AllowInsecureHttp=true
```

A base não inclui `/files`. Mantenha `Files__ApiKey` e `Files__StorageDirectory`. Recicle o pool após alterar a configuração. PublicBaseUrl só monta links; o binding/listener IIS precisa estar configurado manualmente.

## Cliente

Execute novamente o onboarding do plugin. Informe o endereço final com `/files` e marque **Permitir HTTP**. Deixe a chave vazia para manter a credencial existente.

Em instalação manual avançada, os campos equivalentes do JSON são `ApiEndpoint` com URL HTTP e `AllowInsecureHttp: true`. Preserve os outros campos e o estado. A próxima chamada lê a configuração.

Sem opt-in, o cliente rejeita HTTP. A API também aceita HTTP loopback sem essa opção. Credenciais na URL, query e fragmento continuam proibidos na configuração. A resposta precisa manter esquema, host, porta e caminho configurados; redirects permanecem bloqueados. Nenhuma validação de certificado HTTPS é desligada.

Para voltar a HTTPS, altere as duas URLs e desative o opt-in. Links anteriores não são reescritos. Veja [README](README.md) e [configuração do cliente](skills/filedrop/setup.md).
