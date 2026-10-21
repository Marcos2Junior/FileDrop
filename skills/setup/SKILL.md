---
name: setup
description: Configurar ou alterar o FileDrop no Windows usando uma janela local para endereço da API e chave de POST.
---

Use para a configuração inicial do FileDrop ou quando o usuário quiser mudar endereço ou chave. Leia [as instruções locais](../filedrop/setup.md). A configuração não faz upload nem muda a API.

Abra o configurador incluído no plugin, no computador Windows onde o agente executará as entregas:

    powershell.exe -NoProfile -STA -File "<plugin>/skills/filedrop/scripts/Configure-FileDrop.ps1"

Consulte a política efetiva no Windows PowerShell 5.1. Se for `Restricted`, sem bloqueio de GPO, e houver autorização do usuário para liberar os processos do FileDrop, use:

    powershell.exe -NoProfile -STA -ExecutionPolicy RemoteSigned -File "<plugin>/skills/filedrop/scripts/Configure-FileDrop.ps1"

Reutilize a autorização já dada na conversa, inclusive para chamadas posteriores do helper. Não peça confirmação novamente a cada uso. Se ela ainda não existir, peça uma vez para essa liberação limitada. Siga [execução no Windows](../filedrop/setup.md#execução-no-windows); não execute o script antes de decidir os argumentos, nem abra outra instância enquanto o resultado anterior estiver pendente.

Resolva `<plugin>` a partir dos arquivos instalados deste plugin; não invente um caminho. A janela precisa de sessão gráfica interativa. Se os arquivos estiverem disponíveis somente como recursos, copie os scripts empacotados `Configure-FileDrop.ps1` e `filedrop.ps1` para a mesma pasta temporária isolada no computador autorizado, preservando o conteúdo, e execute o configurador dessa pasta. Não gere uma versão alternativa do código.

O usuário preenche a chave diretamente no campo mascarado. Não solicite a chave na conversa, por argumento, por variável criada pelo agente ou por screenshot. Não leia arquivos de segredo nem automatize o preenchimento do campo. Não configure valores reais por conta própria.

O configurador salva a configuração fora do Git, protege novas chaves com DPAPI para o usuário atual do Windows e preserva o estado de fallback. HTTP requer opt-in explícito e expõe o tráfego. A opção `RemoteSigned` autorizada vale somente para o processo do FileDrop. Não altere políticas permanentes, GPO, permissões ou infraestrutura. Se a execução continuar bloqueada ou a sessão gráfica estiver indisponível, explique o bloqueio e pare.

Relate somente o resultado JSON sanitizado: `configured` confirma gravação; `cancelled` encerra sem novas tentativas. Nenhum resultado autoriza testar upload real ou registrar falha nativa fictícia.
