# Veyro Desktop — Marco 5

## Escopo entregue

- seleção de um ou vários dispositivos conectados na interface;
- payload de aplicação cifrado individualmente para cada destinatário com ECDH P-256, HKDF-SHA-256 e AES-256-GCM;
- encaminhamento pelo coordenador sem acesso ao conteúdo da aplicação;
- transferência bidirecional de arquivos por oferta, aceite, blocos ordenados de 128 KiB e conclusão;
- limite de 4 GiB por arquivo, nome de destino sanitizado e verificação SHA-256 antes da promoção do arquivo temporário;
- clipboard textual manual, limitado a 64 KiB;
- compartilhamento de links restrito a HTTP e HTTPS;
- envio e recepção de bateria e conectividade;
- ping de aplicação com medição de ida e volta;
- estados de recursos e dispositivos apresentados na interface.

## Contrato

`FileTransferEvent` foi acrescentado ao contrato de mensagens com ações de oferta, aceite, rejeição, bloco, conclusão e cancelamento. O contrato de transporte passou a carregar `EncryptedApplicationPayload`, que contém uma chave pública efêmera e uma cópia autenticada do payload para cada identidade destinatária.

Transferências não usam BLE nem a rede local. Metadados e blocos seguem pelo canal rápido TLS sobre Wi-Fi Direct e permanecem cifrados na camada de aplicação enquanto são encaminhados.

## Matriz de validação

1. arquivo vazio, pequeno, maior que um bloco e transferências concorrentes;
2. recusa, cancelamento, queda de sessão e bloco fora de ordem;
3. adulteração de ciphertext, tag, hash final e identidade destinatária;
4. envio individual, múltiplos destinos e encaminhamento entre dois celulares;
5. nomes duplicados, nomes com caminho e arquivo acima do limite;
6. clipboard no limite e links com esquemas rejeitados;
7. estados de bateria/conectividade e ping em ambos os sentidos.
