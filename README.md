# Veyro Desktop

Versão atual: **0.1.0-alpha**.

Aplicativo Windows do ecossistema Veyro. A base usa C#/.NET 10 e WPF, com APIs WinRT de Bluetooth Low Energy e Wi-Fi Direct. O Desktop já anuncia presença, procura pares Veyro e oferece pareamento protegido por PIN e Trust Hub. O canal de dados Wi-Fi Direct pertence ao Marco 3.

O produto é projetado para comunicação direta entre os rádios dos dispositivos. Internet, nuvem, roteador e associação prévia à mesma rede local não fazem parte do caminho principal.

## Estrutura

```text
veyro-desktop/
├── protocol/              # contratos Protobuf mantidos pelo Desktop
├── src/
│   ├── Veyro.Desktop/       # UI WPF, bandeja e integrações Windows
│   └── Veyro.Desktop.Core/  # identidade, logs, framing e contratos
├── tests/
│   └── Veyro.Desktop.Core.Tests/
└── docs/
```

Este repositório é autônomo: todos os arquivos necessários para compilar e testar o aplicativo estão dentro dele. O aplicativo móvel permanece em outro projeto e não faz parte deste repositório.

## Requisitos

- Windows 10/11 de 64 bits;
- SDK .NET 10.0.302 ou patch compatível;
- Bluetooth Low Energy e Wi-Fi Direct para os marcos de transporte.

## Compilar e testar

Na raiz do repositório:

```powershell
dotnet build Veyro.Desktop.slnx
dotnet test Veyro.Desktop.slnx
```

O executável de desenvolvimento é gerado em `src/Veyro.Desktop/bin/Debug/net10.0-windows10.0.19041.0/Veyro.exe`.

## Dados locais

- identidade: `%LOCALAPPDATA%\Veyro\identity.dat`, protegida para o usuário atual pelo DPAPI;
- chave de identidade: `%LOCALAPPDATA%\Veyro\identity-key.dat`, protegida pelo DPAPI;
- Trust Hub: `%LOCALAPPDATA%\Veyro\trusted-devices.dat`, protegido pelo DPAPI;
- logs: `%LOCALAPPDATA%\Veyro\logs`, em JSON Lines e com sanitização de propriedades sensíveis.

Não registrar conteúdo de clipboard, SMS, contatos, notificações, PINs, chaves ou payloads.

## Estado de integração

- Marco 1: concluído;
- Marco 2 no Windows: implementado e coberto por testes automatizados;
- interoperabilidade física com Android: requer que o cliente móvel implemente o contrato BLE descrito em `docs/VEYRO_DESKTOP_MILESTONE_2.md`;
- Marco 3, Wi-Fi Direct e sockets seguros: não iniciado.

Enquanto o produto estiver em alpha, cada atualização publicada incrementa o patch: `0.1.0-alpha`, `0.1.1-alpha`, `0.1.2-alpha` e assim por diante.
