---
name: filedrop
description: Entregar arquivos, screenshots ou documentos solicitados usando o envio nativo primeiro e FileDrop como fallback após uma falha técnica clara. Não captura imagens nem procura arquivos.
---

Use apenas para uma entrega solicitada e autorizada. Execute scripts/filedrop.ps1 com Windows PowerShell 5.1, a partir desta skill. Leia [setup.md](setup.md) se faltar configuração; não configure credenciais/permissões por conta própria.

Se a política efetiva for `Restricted`, sem bloqueio de GPO, a autorização do usuário para liberar os processos do FileDrop permite acrescentar `-ExecutionPolicy RemoteSigned` antes de `-File` em todas as chamadas deste helper. Reutilize a autorização já dada na conversa; não pergunte a cada ação. Se faltar essa autorização, peça uma vez. A liberação termina com o processo e não altera a política permanente. Veja [execução no Windows](setup.md#execução-no-windows) para os limites.

Mantenha os mesmos Channel, Capability e TaskId opacos durante a tarefa; não use nomes de arquivo, links ou erros nesses IDs. Separe capacidades quando os mecanismos nativos forem diferentes. Os padrões são codex-chat/file-delivery.

1. Consulte uma vez: powershell.exe -NoProfile -File "<skill>/scripts/filedrop.ps1" -Action policy -TaskId "<id-da-tarefa>".
2. Exija success=true antes de agir. mayTryNative=true reserva uma tentativa nativa. Registre o resultado com -Action record -Outcome technical_failure|delivered|denied|cancelled|pending_confirmation|uncertain. Se a falha técnica já foi observada, registre-a sem repetir o nativo para comprovar.
3. mayUpload=true permite um arquivo existente solicitado: -Action upload -TaskId "<mesmo-id>" -Path "<caminho-absoluto>". O helper envia diretamente à API, verifica GET integral e imprime JSON. Apresente ao usuário o link e o resultado reais.
4. Primeira falha técnica: FileDrop por 24h naquele canal/capacidade e durante a tarefa ativa. Ao concluir de verdade, use -Action end. Após o cooldown, uma tarefa nova pode tentar o nativo uma vez.
5. denied, cancelled ou pending_confirmation não são falhas técnicas nem autorização para trocar de canal. Pare/aguarde. Não mude IDs, roots, credenciais, permissões ou estado para escapar.
6. uncertain, result_pending ou requiresVerification=true exigem verificar a entrega anterior antes de reenviar. Não repita POST nem troque de canal automaticamente. Só registre delivered após confirmar entrega; verified_not_delivered após confirmar ausência. Uma URL conhecida retornada pode ser verificada sem reupload.
7. A URL é a credencial de leitura: quem tem o link lê/repassa; perder impede recuperação. Retenção é sete dias desde upload. Não salve links/chaves/conteúdo/resultados em arquivos, logs ou transcrição; a resposta deliberada ao consumidor é permitida.
8. O helper não observa ferramentas nativas, captura screenshots ou faz uploads espontâneos. Sua configuração autoriza caminhos tecnicamente; não autoriza conteúdo/destino por si só. A liberação temporária descrita acima vale somente para os scripts do FileDrop; não supera negações de acesso, assinatura ou regras de organização.
