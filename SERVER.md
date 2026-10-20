# Publicar a API no IIS

Estes passos são manuais, para quem administra o servidor. Os scripts do projeto não alteram IIS, firewall, ACLs ou certificados.

Os exemplos usam caminhos/domínios fictícios. Nunca coloque uma chave real no Git, em um comando salvo, no pacote ou em um web.config versionado.

## 1. Prepare o servidor

Instale o IIS e depois o **Hosting Bundle .NET 9 x64 atualizado** para um Windows suportado. Se o Hosting Bundle já existia antes do IIS, repare-o. O módulo ASP.NET Core faz parte desse bundle; instalar só o SDK/Runtime não substitui o módulo do IIS. Reinicie os serviços conforme solicitado pelo instalador. [Orientação Microsoft](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-9.0).

## 2. Separe aplicação e dados

Use duas pastas dedicadas, por exemplo:

| Pasta | Conteúdo |
| --- | --- |
| C:\Sites\FileDropApi | Conteúdo do [pacote da API](packages/FileDropApi-IIS.zip), ou gerado conforme [BUILD.md](BUILD.md) |
| D:\FileDropData\files | Somente envelopes criptografados desta API |

A pasta de dados deve ficar fora da publicação, content root e **todos os webroots do IIS**. Não crie diretório virtual para ela. Não use symlink/junction e não misture dados preexistentes ou arquivos de outros sistemas. A limpeza exclui envelopes próprios expirados desse diretório.

## 3. Crie o site e HTTPS

No IIS Manager, crie um pool dedicado, por exemplo **FileDrop**, com **No Managed Code**, **Enable 32-bit Applications = False** e identidade **ApplicationPoolIdentity**. Crie o site/aplicação apontando para a pasta publicada. Preserve o web.config gerado pelo SDK.

Configure DNS e binding HTTPS para seu domínio com certificado válido e confiável. O cliente deve alcançar diretamente o endereço final, sem redirects. O cliente da skill exige HTTPS por padrão; HTTP externo requer opt-in nos dois lados, conforme [HTTP.md](HTTP.md).

No Explorador/segurança da pasta, conceda manualmente à identidade **IIS AppPool\FileDrop** apenas o necessário: leitura/execução na publicação; leitura, criação, gravação e exclusão na pasta de dados (Modify nessa pasta dedicada atende). Não conceda Full Control nem troque o pool para administrador. A pasta de configuração/credencial também deve ter acesso restrito. [Identidades de pool](https://learn.microsoft.com/en-us/iis/manage/configuring-security/application-pool-identities).

## 4. Configure as variáveis do processo

Configure o ambiente **do pool/aplicação**, por mecanismo administrativo seguro do servidor. Não basta definir variável em um PowerShell: o worker do IIS não herda automaticamente aquele terminal. No IIS Configuration Editor, quando disponível para o administrador, selecione **system.applicationHost/applicationPools**, abra a coleção do pool e sua coleção **environmentVariables**. Configure os nomes abaixo no pool dedicado; a disponibilidade e delegação variam por versão/política.

| Nome exato | Valor a fornecer |
| --- | --- |
| Files__ApiKey | Segredo imprevisível de 32 a 256 caracteres ASCII sem espaços, fornecido por você |
| Files__PublicBaseUrl | Base pública canônica, como https://files.example.invalid, **sem /files** |
| Files__AllowInsecureHttp | Opcional; false por padrão. true permite base HTTP externa, com exposição do tráfego |
| Files__StorageDirectory | Caminho absoluto dedicado, como D:\FileDropData\files |
| Files__MaxUploadBytes | Opcional; padrão e máximo 104857600; pode reduzir |

Não há chave pronta. Use um gerenciador de senhas/segredos autorizado para gerar e fornecer uma chave forte, sem registrá-la em instruções, prints ou histórico. O exemplo appsettings.json mantém os campos sensíveis vazios. Sem configuração válida, a API não inicia. A validação de formato não comprova que a chave tem boa entropia.

Se publicada em subaplicação, por exemplo **https://files.example.invalid/agents**, esse é o PublicBaseUrl; o endpoint do cliente da skill será **https://files.example.invalid/agents/files**. Não use Host ou cabeçalhos de proxy para definir a base.

Recicle o pool após configurar. A chave de POST configurada na skill deve ser igual a Files__ApiKey. Rotacionar essa chave não altera chaves individuais dos links. [Configuração de pools](https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/applicationpools/).

## 5. Ajuste limite e respostas

No site, abra **Request Filtering > Edit Feature Settings**. Configure **Maximum allowed content length (Bytes)** como pelo menos **104923136**: 100 MiB mais 64 KiB de margem multipart. O limite da API continua 100 MiB de arquivo. Confirme o limite do proxy, se houver. IIS pode rejeitar antes de a API produzir JSON.

No **Configuration Editor > system.webServer/httpErrors**, defina **existingResponse = PassThrough** para preservar o JSON da aplicação. Preserve as demais configurações e o web.config gerado; não substitua-o por um exemplo incompleto.

Referências: [requestLimits](https://learn.microsoft.com/en-us/iis/configuration/system.webserver/security/requestfiltering/requestlimits/), [httpErrors](https://learn.microsoft.com/en-us/iis/configuration/system.webserver/httperrors/).

## 6. Oculte o segredo dos logs antes de usar dados reais

No site, em **Logging > W3C > Select Fields**, desmarque **URI Query (cs-uri-query / UriQuery)**. UriStem pode permanecer: a chave do arquivo fica na query. Revise campos personalizados, Failed Request Tracing, ferramentas de diagnóstico e captura de corpo: também podem guardar URLs/cabeçalhos.

No proxy/APM, omita query/URL completa, X-Api-Key, corpo da resposta POST e Location. Não registre resultados da skill que contêm links. A aplicação desliga Microsoft.AspNetCore.Hosting.Diagnostics e fixa logs próprios em Warning; isso não controla componentes externos.

Quem acessa o link pode ler o arquivo. O histórico do navegador, cliente e conversa também podem armazená-lo. [Campos W3C do IIS](https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/sites/site/logfile/).

## 7. Mantenha o processo ativo para limpeza

Se precisa de varredura sem visitas ao site, configure manualmente **Start Mode = AlwaysRunning** no pool e **Idle Time-out = 0**. Habilite o recurso IIS **Application Initialization** e **Preload Enabled = True** na aplicação conforme sua versão/ambiente. Isso não impede paradas, reinícios ou reciclagens.

A API varre ao iniciar e a cada hora enquanto estiver rodando; retoma ao reiniciar. GET recusa expirados ao completar sete dias, mesmo antes da próxima varredura. A retenção usa o horário UTC do upload; acesso não renova. Não há garantia de remoção no segundo exato nem de apagar backups.

Referências: [processModel/idleTimeout](https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/applicationpools/add/processmodel/), [Application Initialization](https://learn.microsoft.com/en-us/iis/get-started/whats-new-in-iis-8/iis-80-application-initialization).

## 8. Confira com um arquivo de teste

Configure a skill conforme README.md e use um arquivo de teste sem dados reais. O helper verifica POST/GET e o header X-FileDrop-Expires-At quando a entrega autorizada usa FileDrop. Não grave a URL/Location ou os resultados em logs.

Se a entrega nativa funcionar, ela permanece preferida; não declare falha fictícia só para forçar um teste. Para validação de implementação sem rede pública, execute os testes TestServer da API em BUILD.md.

Não há página inicial/painel: GET / retornar404 não demonstra que a API está quebrada. A API só implementa o contrato POST/GET de arquivos.
## Atualizar ou mudar o servidor

Publique novos binários manualmente em uma parada planejada. Preserve configuração externa e a pasta de dados. Nunca substitua a pasta de dados por conteúdo de pacote.

Para preservar links, mantenha domínio/caminho público e copie os envelopes cifrados mantendo nomes/conteúdo. Atualize a configuração da nova máquina e as permissões mínimas. Não é necessária master key: o segredo só existe no link. Os sete dias originais continuam valendo. Sem links não há recuperação; backups apenas dos envelopes não bastam para descriptografar.

Mudando o domínio, os links antigos não mudam: mantenha o endereço anterior atendendo enquanto necessário. Não dependa de redirect para o cliente da skill. Para novos uploads, atualize PublicBaseUrl e ApiEndpoint do cliente e reinicie os processos.

## Capacidade

Uma transferência de cada vez por processo; concorrentes recebem 429. Reserve aproximadamente 300–500 MiB de memória além do custo normal do runtime para arquivos máximos, conforme GC e carga. Isso é estimativa, não teto de consumo. AES-GCM usa memória integral e não há Range; arquivos grandes demoram para aparecer no cliente.

Não há quota total de disco nem antivírus. Acompanhe espaço livre. Formatos ativos/desconhecidos são attachment, mas o download continua sendo conteúdo não confiável. Consulte [API.md](API.md) e o troubleshooting em [README.md](README.md).
