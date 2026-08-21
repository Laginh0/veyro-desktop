# Veyro Desktop — Marco 6

## Escopo entregue

- notificações recebidas exibidas pela integração nativa da bandeja do Windows;
- sincronização manual das notificações atuais do Windows após a autorização do sistema;
- comandos de reprodução, pausa, próxima faixa, faixa anterior e volume aplicados à sessão de mídia do Windows;
- envio de controles de mídia aos dispositivos selecionados;
- ações seguras limitadas a bloqueio da estação e páginas `ms-settings:`; shell arbitrário e intents nativas são recusados;
- controle de início, encerramento e blackout de apresentação;
- regras persistentes e independentes por dispositivo para arquivos, clipboard, links, notificações, mídia, comandos e apresentação;
- políticas `Bloquear`, `Perguntar` e `Permitir`, com confirmação contextual na interface;
- revogação de confiança também remove as permissões do dispositivo.

## Segurança e privacidade

Comandos seguros usam uma lista permitida e nunca executam texto como PowerShell, `cmd.exe` ou shell. A política inicial de comandos é `Bloquear`; as demais funções começam em `Perguntar`. O acesso às notificações do Windows é solicitado somente quando o usuário aciona a sincronização.

Conteúdo de arquivo, clipboard e notificação não é escrito nos logs. As permissões ficam protegidas por DPAPI para o usuário atual.

## Matriz de validação

1. cada combinação de política por dispositivo;
2. revogação durante uma solicitação e durante uma transferência;
3. notificação sem título, longa, removida e acesso negado pelo Windows;
4. mídia sem sessão ativa e cada comando suportado;
5. tentativa de shell, URI não permitida e ação segura autorizada;
6. apresentação com e sem aplicativo de slides em foco;
7. múltiplos dispositivos com políticas diferentes para a mesma função.
