---
name: setup
description: Configurar ou alterar o FileDrop no Windows usando uma janela local para endereço da API, chave de POST e pastas permitidas.
---

Use para a configuração inicial do FileDrop ou quando o usuário quiser mudar endereço, chave ou pastas. Leia [as instruções locais](../filedrop/setup.md). A configuração não faz upload nem muda a API.

Abra o configurador incluído no plugin, no computador Windows onde o agente executará as entregas:

    powershell.exe -NoProfile -STA -File "<plugin>/skills/filedrop/scripts/Configure-FileDrop.ps1"

Resolva `<plugin>` a partir dos arquivos instalados deste plugin; não invente um caminho. A janela precisa de sessão gráfica interativa. Se os arquivos estiverem disponíveis somente como recursos, copie os scripts empacotados `Configure-FileDrop.ps1` e `filedrop.ps1` para a mesma pasta temporária isolada no computador autorizado, preservando o conteúdo, e execute o configurador dessa pasta. Não gere uma versão alternativa do código.

O usuário preenche a chave diretamente no campo mascarado. Não solicite a chave na conversa, por argumento, por variável criada pelo agente ou por screenshot. Não leia arquivos de segredo nem automatize o preenchimento do campo. Não configure valores reais por conta própria.

O configurador salva a configuração fora do Git, protege novas chaves com DPAPI para o usuário atual do Windows e preserva o estado de fallback. HTTP requer opt-in explícito e expõe o tráfego. Não altere políticas de execução, permissões ou infraestrutura para fazê-lo funcionar. Se a execução ou a sessão gráfica for negada/indisponível, pare e explique o bloqueio.

Relate somente o resultado JSON sanitizado: `configured` confirma gravação; `cancelled` encerra sem novas tentativas. Nenhum resultado autoriza testar upload real ou registrar falha nativa fictícia.
