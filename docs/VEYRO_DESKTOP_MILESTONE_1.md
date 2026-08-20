# Veyro Desktop — Marco 1

## Resultado

O aplicativo Windows está isolado em um repositório autônomo:

```text
veyro-desktop/
├── protocol/     # contratos Protobuf necessários ao Desktop
├── src/          # Windows/C#/.NET/WPF
├── tests/
└── docs/
```

O Desktop é uma fundação executável. Ele cria uma identidade local persistente, verifica a presença das APIs Windows planejadas, apresenta o estado em uma interface mínima e permanece acessível pela bandeja do sistema.

## Decisão de UI

O probe local compilou e executou as projeções WinRT para:

- `BluetoothLEAdvertisementWatcher`;
- `WiFiDirectDevice.GetDeviceSelector`.

WPF sobre .NET 10 foi escolhido para a fundação porque oferece integração direta com WinRT e bandeja do sistema com poucas dependências. O núcleo não depende de WPF; portanto, uma limitação encontrada nos testes físicos pode motivar outra camada de UI sem descartar identidade, logs, framing ou contratos.

## Contratos

`protocol/veyro_message.proto` preserva o contrato de aplicação compatível com o cliente móvel. `protocol/veyro_transport.proto` introduz separadamente metadados de transporte e mensagens de negociação:

- versão major/minor;
- ID imutável da mensagem;
- origem e destinos;
- broadcast explicitamente autorizado;
- tipo do payload;
- janela de validade;
- limite de encaminhamento;
- sequência e confirmação;
- autenticação de origem opaca;
- payload cifrado opaco ao coordenador.

O Desktop mantém cópias versionadas dos contratos de que precisa, sem depender de arquivos externos ao repositório. A sincronização futura com o cliente móvel deve ser deliberada e validada por compatibilidade.

## Framing preliminar

O framing de controle é deliberadamente pequeno e será revisado antes do canal definitivo:

| Campo | Tamanho | Descrição |
| --- | ---: | --- |
| Magic | 4 bytes | ASCII `VYRO` |
| Versão | 1 byte | versão do framing, inicialmente `1` |
| Flags | 1 byte | reservado para semântica documentada posteriormente |
| Reservado | 2 bytes | deve ser zero |
| Comprimento | 4 bytes | inteiro sem sinal, big-endian |
| Payload | variável | no máximo 1 MiB no canal de controle |

O leitor suporta streams fragmentados e rejeita magic/versão inválidos, campos reservados não nulos, tamanho excessivo e frames truncados antes de entregar dados às camadas superiores. Arquivos grandes não usam este limite como um único frame; streaming, multiplexação e controle de fluxo serão especificados no Marco 3.

## Identidade e segurança

O Marco 1 cria um ID aleatório de 16 caracteres hexadecimais, compatível com o formato atual de identidade Android. O registro é serializado e protegido pelo DPAPI no escopo do usuário Windows; a gravação usa substituição atômica.

A chave criptográfica de pareamento ainda não foi escolhida. Algoritmos, TLS/Noise, armazenamento de chave não exportável, rotação e revogação dependem da revisão de segurança anterior ao Marco 2. O campo de autenticação no envelope permanece opaco para evitar fixar prematuramente uma primitiva.

## Logs

Os logs são estruturados em JSON Lines. Nomes relacionados a clipboard, SMS, telefone, contatos, notificações, PIN, tokens, segredos, chaves, autenticação, conteúdo e payload são censurados. Identificadores recebem hash truncado; quebras de linha são removidas para impedir injeção de registros.

## Fora do Marco 1

- anúncio ou busca BLE real;
- pareamento e Trust Hub;
- grupo Wi-Fi Direct e sockets;
- criptografia de sessão;
- conexão com o Android;
- inicialização automática e instalador.

Esses itens não são simulados pela interface. O próximo marco começa pela descoberta BLE e pelo pareamento bilateral em hardware real.
