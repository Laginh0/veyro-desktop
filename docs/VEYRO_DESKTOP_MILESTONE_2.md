# Veyro Desktop — Marco 2

## Resultado

O lado Windows do Marco 2 implementa:

- anúncio de presença e varredura BLE reais;
- identidade efêmera no anúncio, sem expor o ID persistente;
- serviço e cliente GATT para o canal de controle;
- negociação de capacidades;
- pareamento com PIN de seis dígitos derivado nos dois aparelhos;
- confirmação explícita e assinada nos dois lados;
- chave de identidade persistente por instalação;
- Trust Hub protegido por DPAPI, com revogação;
- desafio assinado para autenticar a reconexão de aparelhos conhecidos;
- interface de dispositivos próximos e aparelhos confiáveis.

O Desktop não usa internet, nuvem, roteador ou LAN para a descoberta. O ADB por Wi-Fi usado durante o desenvolvimento é apenas uma ferramenta de diagnóstico e não integra o transporte do produto.

## BLE

Identificadores do serviço:

- serviço GATT: `68d0925e-266d-4ca5-9588-9c804c6cd8ff`;
- característica de controle: `886c164a-9f9f-465f-9428-8fb7ee8cd15a`;
- característica: `Write`, `WriteWithoutResponse` e `Notify`;
- limite defensivo de pacote de controle: 512 bytes.

O anúncio usa Service Data com UUID de 128 bits (`AD type 0x21`). O UUID segue a ordem de bytes little-endian definida pelo Bluetooth. Depois dos 16 bytes do UUID do serviço, o payload compacto contém:

| Campo | Tamanho | Descrição |
| --- | ---: | --- |
| Versão major | 1 byte | versão do protocolo BLE |
| Capacidades | 1 byte | máscara de capacidades disponíveis |
| ID efêmero | 6 bytes | valor aleatório renovado a cada execução |

Observações são mantidas por 20 segundos, atualizadas por ID efêmero e ordenadas pela intensidade do sinal. O endereço Bluetooth é usado somente durante a sessão de descoberta e não é persistido como identidade.

## Pareamento

A implementação usa primitivas consolidadas da plataforma:

- ECDSA P-256 para a identidade persistente da instalação;
- ECDH P-256 efêmero para o segredo da sessão de pareamento;
- SHA-256/HMAC-SHA-256 para o transcript e o PIN de verificação;
- DPAPI no escopo do usuário Windows para a chave privada e o Trust Hub.

Cada `PairingHello` contém ID da sessão, identidade declarada, capacidades, timestamp, nonce, chave pública de identidade, chave ECDH efêmera e assinatura. A janela aceita é de dois minutos. O PIN não atravessa o rádio: ele é calculado como os seis dígitos obtidos do HMAC do segredo ECDH sobre o transcript canônico ordenado por ID de dispositivo.

O transcript assinado é independente de plataforma. Campos variáveis são UTF-8 ou bytes e recebem prefixo de comprimento `uint32` big-endian. Inteiros de 64 bits são big-endian. Os domínios são `Veyro.PairingHello.v1`, `Veyro.PairingConfirmation.v1` e `Veyro.PairingVerification.v1`.

Somente depois de ambos assinarem a aceitação do mesmo digest de verificação a chave pública remota entra no Trust Hub. Recusa, assinatura inválida, sessão divergente, mensagem antiga ou PIN não confirmado não criam confiança.

## Reconexão

Um aparelho conhecido precisa provar posse da chave privada correspondente ao registro ativo do Trust Hub. O desafiante envia 32 bytes aleatórios; o par assina o domínio `Veyro.ReconnectChallenge.v1`, seu ID e o desafio. Registros revogados nunca autenticam.

## Contrato Android

As mensagens BLE estão em `protocol/veyro_transport.proto`: `BleControlPacket`, `PairingHello`, `PairingConfirmation`, `ReconnectChallenge` e `ReconnectProof`.

O aparelho conectado por ADB durante este marco executa Veyro `0.1.8-alpha`. Essa versão usa o transporte móvel anterior e não implementa os UUIDs nem as mensagens do Marco 2. Portanto:

- APIs e permissões necessárias no aparelho foram confirmadas;
- o executável Windows foi iniciado com BLE e Wi-Fi Direct disponíveis;
- o handshake criptográfico foi validado entre dois pares automatizados;
- descoberta e pareamento Windows ↔ Android permanecem como teste de aceitação pendente até uma versão móvel compatível ser instalada.

Não se considera a ausência desse contrato no Android uma falha do rádio ou do Desktop.

## Testes

Os testes automatizados cobrem codec do anúncio, expiração de descobertas, persistência da chave, PIN idêntico nos dois pares, rejeição de mensagem adulterada, confirmação bilateral, serialização Protobuf, Trust Hub protegido, revogação e prova de reconexão.

O Marco 3 só deve iniciar depois de instalar uma versão Android compatível e executar o pareamento bilateral em hardware real.
