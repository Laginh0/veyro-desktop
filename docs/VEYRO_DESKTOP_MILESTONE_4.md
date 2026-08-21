# Veyro Desktop — Marco 4

Estado: **publicado em `0.1.2-alpha`; testes pendentes**.

## Escopo entregue

- até três canais rápidos simultâneos, indexados pela identidade autenticada do dispositivo;
- listener TCP persistente para aceitar mais de um membro do grupo Wi-Fi Direct;
- envelopes assinados com destino individual, múltiplos destinos ou broadcast autorizado;
- encaminhamento todos-para-todos por um coordenador sem alterar o payload da aplicação;
- validade, limite de saltos, deduplicação com limite de memória e rejeição de origens não confiáveis;
- estado de membros com sincronização autenticada pelo canal rápido;
- coordenador inicial alinhado ao proprietário do grupo Wi-Fi Direct;
- eleição determinística e mudança de época quando o coordenador fica indisponível;
- publicação de um estado `CoordinatorCommitted` pelo novo coordenador;
- reinicialização do grupo Wi-Fi Direct quando este notebook é eleito coordenador;
- diagnóstico mínimo na interface: sessões, membros, coordenador e época.

O controle de grupo é serializado em JSON versionado dentro do payload de controle do `TransportEnvelope`. Isso preserva o contrato Protobuf compartilhado existente e mantém toda a alteração desta etapa dentro de `desktop/`.

## Critérios de aceitação pendentes

Os testes não foram executados nesta etapa por solicitação do proprietário. Na próxima etapa, validar:

1. testes unitários do assinador, deduplicador, roteador, codec de grupo e eleição;
2. duas sessões simuladas enviando mensagens concorrentes sem cruzar identidades;
3. notebook e dois Androids conectados simultaneamente sem roteador nem internet;
4. mensagem Android A → Android B encaminhada pelo notebook uma única vez;
5. destinos múltiplos e broadcast autorizado;
6. rejeição de envelope expirado, duplicado, adulterado ou com origem revogada;
7. desligamento do coordenador, eleição, reconstrução do grupo Wi-Fi Direct e retomada das sessões;
8. verificação de que um coordenador intermediário não recebe payload de aplicação em texto aberto;
9. repetição com notebook na bateria e ligado à energia para conferir a preferência eleitoral;
10. observação prolongada do keepalive, pois a queda anterior antes de 45 segundos continua pendente do Marco 3.

## Publicação

O proprietário autorizou explicitamente a publicação antes dos testes como `0.1.2-alpha`. O repositório `veyro-desktop` deve receber somente o conteúdo desta pasta `desktop/`; nenhum arquivo de `mobile/`, do `protocol/` externo ou da raiz do workspace pode ser incluído. A cópia autônoma `desktop/protocol/` faz parte do projeto Desktop.
