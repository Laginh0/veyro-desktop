# Veyro Desktop — Marco 7

## Escopo entregue

- mouse relativo, clique simples, clique duplo e rolagem por `SendInput`;
- teclado Unicode limitado e validado;
- entrada de caneta com coordenadas normalizadas, pressão, inclinação, contato e cancelamento;
- quadro Veyro que preserva visualmente a pressão dos traços;
- movimento absoluto da caneta refletido no ponteiro do Windows;
- limite de 240 eventos de entrada por segundo e permissão inicial `Bloquear` por dispositivo;
- seleção persistente de pastas locais compartilhadas;
- listagem remota de raízes, subpastas e até 500 itens por diretório;
- identificadores de documento opacos protegidos por DPAPI, sem envio do caminho real;
- bloqueio de travessia de diretório e de pontos de nova análise, links simbólicos e junções;
- download de arquivo remoto reutilizando a transferência cifrada e verificada do Marco 5;
- sessões retomáveis por 24 horas, persistidas com DPAPI e renovadas após retomada autenticada;
- revogação que encerra o canal atual e apaga tokens de retomada e permissões;
- reconstrução do grupo após retomada do Windows;
- execução contínua na bandeja, argumento `--background` e início opcional pelo registro do usuário.

## Mesa digitalizadora sem empacotamento

A API de injeção nativa de caneta do Windows exige a capacidade restrita `inputInjectionBrokered` em um pacote MSIX. Como o instalador e o empacotamento foram excluídos deste marco, o Desktop usa `SendInput` para mover o ponteiro global e mantém pressão/inclinação no quadro interno do Veyro. Nenhuma capacidade restrita é declarada ou simulada.

## Pastas compartilhadas

Somente raízes escolhidas manualmente aparecem para aparelhos autorizados. Cada solicitação continua sujeita à política individual `Bloquear`, `Perguntar` ou `Permitir`. O token de documento é descriptografado apenas neste usuário do Windows, resolvido novamente sob a raiz e validado antes de qualquer enumeração ou leitura.

## Continuidade

Tokens de retomada têm 32 bytes, permanecem associados à identidade confiável e nunca são registrados em logs. O estado persistido é renovado por no máximo 24 horas e limitado a sete dias por construção. Suspensão e retomada do Windows acionam limpeza de estado expirado e reconstrução do grupo Wi-Fi Direct.

## Matriz de validação

1. mouse, rolagem, Unicode, taxa excessiva e permissão revogada;
2. caneta com pressão zero/máxima, coordenadas inválidas e cancelamento;
3. pasta removida, renomeada, inacessível e com junção ou link simbólico;
4. token adulterado, travessia de diretório e listagem acima de 500 itens;
5. navegação e download em ambos os sentidos;
6. suspensão, hibernação, reinício do aplicativo e retomada dentro/fora da janela;
7. revogação durante sessão ativa e limpeza do token persistido;
8. fechamento da janela, operação na bandeja e início com `--background`.

## Fora do escopo

- instalador;
- pacote MSIX;
- assinatura de código;
- capacidade restrita de injeção nativa de caneta.
