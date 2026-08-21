# Veyro Desktop — Marco 3

## Resultado

O lado Windows do Marco 3 implementa:

- publicação e aceitação de grupo Wi-Fi Direct pelas APIs WinRT;
- descoberta programática de pares Wi-Fi Direct;
- obtenção dos endereços exclusivos do enlace direto;
- negociação de função, porta, ALPN e token de retomada pelo GATT;
- assinatura da oferta do canal rápido com a identidade persistente;
- socket TCP vinculado explicitamente ao endereço Wi-Fi Direct;
- TLS mútuo 1.2/1.3 com ALPN `veyro/1`;
- certificado preso à chave pública já confirmada no Trust Hub;
- hello de versão e identidade depois do TLS;
- keepalive a cada 5 segundos e timeout em 15 segundos;
- retomada autenticada por token por até 5 minutos;
- estado do canal rápido exibido na interface.

## Independência de infraestrutura

O endereço IP não é transportado no anúncio BLE nem aceito de configuração externa. Depois da formação do grupo, o Desktop obtém `LocalHostName` e `RemoteHostName` diretamente de `WiFiDirectDevice.GetConnectionEndpointPairs()`.

O servidor TCP escuta somente no endereço local desse par. O cliente faz `Bind` no mesmo endereço antes de conectar ao endereço remoto do enlace. Dessa forma, um endereço de Ethernet, Wi-Fi de infraestrutura, loopback ou LAN não é escolhido silenciosamente como fallback.

O Desktop publica um grupo autônomo e aceita solicitações pela `WiFiDirectConnectionListener`. A preferência por group owner é negociada com o par; topologia com vários membros permanece para o Marco 4.

## Negociação BLE

`protocol/veyro_transport.proto` adiciona `FastChannelOffer` e `FastChannelAnswer` ao `BleControlPacket`.

A oferta contém:

- ID da sessão;
- ID do dispositivo ofertante;
- função no grupo;
- porta TCP;
- ALPN;
- token aleatório de retomada;
- assinatura ECDSA da oferta canônica.

Não há endereço IP na oferta. A assinatura usa o domínio `Veyro.FastChannelOffer.v1`; campos variáveis possuem comprimento `uint32` big-endian. Ofertas de aparelhos ausentes ou revogados no Trust Hub são rejeitadas antes do socket.

## TLS e identidade

O Desktop cria em memória um certificado X.509 autoassinado usando a chave ECDSA P-256 persistente da instalação. No Windows, a chave é importada temporariamente para o armazenamento CNG exigido pelo Schannel.

A cadeia pública do certificado não é usada como autoridade. A validação compara em tempo constante a chave pública SPKI do certificado com a chave armazenada no Trust Hub e exige que o `CN` seja o ID esperado. Os dois lados apresentam certificado, portanto um membro do grupo Wi-Fi Direct sem confiança não abre sessão Veyro.

Depois do TLS, ambos enviam `FastChannelHello` com sessão, identidade e versão major/minor. Divergência de sessão, identidade ou versão major encerra o socket.

## Keepalive e retomada

`FastChannelPacket` multiplexa hello, keepalive, confirmação, retomada e `TransportEnvelope` dentro do framing `VYRO` do Marco 1. Todo o framing passa dentro do `SslStream`.

Ausência de qualquer pacote por mais de 15 segundos encerra a sessão. O token de retomada possui 32 bytes, é vinculado ao ID do aparelho e à sessão, comparado em tempo constante e expira após 5 minutos. A sequência confirmada nunca pode retroceder.

Após reconstruir o enlace Wi-Fi Direct, o coordenador reutiliza o estado ainda válido, executa novamente TLS e o hello e negocia `ResumeRequest`/`ResumeResponse` antes de liberar a sessão.

## Validação realizada

- compilação do gerenciador WinRT de Wi-Fi Direct no alvo Windows 10/11;
- inicialização estável do executável com a API Wi-Fi Direct disponível;
- socket TCP real em loopback entre dois pares;
- autenticação mútua TLS 1.3 pelo Schannel;
- certificado incorreto rejeitado pelo pinning do Trust Hub;
- hello de sessão e versão em ambas as direções;
- pacote Protobuf transportado pelo framing dentro do TLS;
- oferta BLE assinada e adulteração rejeitada;
- token de retomada, expiração e sequência testados.

O loopback valida o protocolo e a segurança do socket, mas não substitui o rádio. O Bluetooth estava desligado durante o smoke test final e o aplicativo agora apresenta esse estado sem tentar iniciar GATT. O gerenciador Wi-Fi Direct iniciou sem derrubar o processo.

## Pendência de aceitação física

O Veyro Android `0.1.8-alpha` instalado no aparelho de teste ainda não implementa os contratos dos Marcos 2 e 3. Por isso, não houve formação real de grupo nem socket Windows ↔ Android.

Para aceitar fisicamente o Marco 3 será necessário:

1. implementar no Android os UUIDs GATT e mensagens de pareamento;
2. implementar `WifiP2pManager` e a negociação `FastChannelOffer`/`Answer`;
3. ligar Bluetooth e Wi-Fi nos dois aparelhos;
4. parear pelo PIN;
5. formar o grupo sem internet e sem associação a roteador;
6. verificar TLS, keepalive, queda e reconstrução do grupo.

O código Windows está preparado para esse teste, mas o resultado não deve ser anunciado como interoperabilidade física concluída antes dessa execução.
