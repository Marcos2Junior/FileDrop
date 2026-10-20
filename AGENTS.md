# Instruções para agentes

Projeto independente: API ASP.NET Core net9.0 e skill Windows para entrega de arquivos. Leia README.md, API.md e skills/filedrop/SKILL.md. Preserve a estrutura pequena e a autorização do usuário.

- Sem painel, banco, usuários, captura de screenshots, gestão de chaves ou novos serviços.
- API: POST /files com X-Api-Key; GET pelo link com chave por arquivo. Preserve AES-GCM, segredo apenas no link, nenhum plaintext temporário, limite de 100 MiB e retenção fixa de sete dias.
- HTTPS é padrão; HTTP externo exige opt-in explícito nos dois lados. Sem redirects, mudança de origem ou bypass TLS.
- Cliente: skills/filedrop/scripts/filedrop.ps1 no Windows PowerShell 5.1, sem SDK, pip ou PowerShell 7. Endpoint/roots ficam em configuração local fora Git; chave via FILE_API_KEY ou arquivo separado fornecido pelo usuário. Não configure credenciais reais.
- Skill: nativo primeiro; uma falha técnica clara ativa fallback por 24h no canal/capacidade e durante a tarefa ativa. Negado/cancelado/pendente não autoriza fallback; incerto exige verificar antes de reenviar. Não mude IDs nem apague estado para escapar.
- Entregue somente o arquivo solicitado dentro de roots aprovados. Não registre links, chaves, corpos de resposta, nomes/conteúdo ou erros livres no estado/logs. A saída deliberada ao consumidor é permitida.
- Testes somente com fixtures isoladas e transporte fake/TestServer. Não acessar produção/IP real. A tentativa anterior de loopback retornou socket 10013 e a de criar junction foi negada: não repetir sem nova evidência material e autorização aplicável.
- Não contorne negação de operação ou política de execução. A autorização anterior de exceção de política valeu somente para os processos de teste já encerrados; não é liberação permanente. Não altere CurrentUser, LocalMachine ou GPO.
- Não instalar no perfil real, alterar AGENTS global/configuração de cliente, IIS, firewall, ACLs, certificados ou credenciais persistentes. Sem push/deploy nesta tarefa. Preserve .git, configurações e perfis de publicação do usuário.
- Pacotes atuais contêm somente API e skill. Não misture dados, configurações reais, estado, logs ou artefatos de versões anteriores. Antes de qualquer limpeza futura, confira escopo, caminhos absolutos e reparse points; não remova dados do usuário por inferência.
