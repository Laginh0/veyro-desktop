# Veyro Desktop

Aplicativo Windows do ecossistema Veyro. A fundação usa C#/.NET 10 e WPF, com acesso às APIs WinRT de Bluetooth Low Energy e Wi-Fi Direct. O transporte de produção ainda não está habilitado: descoberta e pareamento pertencem ao Marco 2, e o canal Wi-Fi Direct ao Marco 3.

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
- logs: `%LOCALAPPDATA%\Veyro\logs`, em JSON Lines e com sanitização de propriedades sensíveis.

Não registrar conteúdo de clipboard, SMS, contatos, notificações, PINs, chaves ou payloads.
