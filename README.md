# Veyro Desktop

Current version: **0.1.2-alpha**.

Windows application for the Veyro ecosystem. The codebase uses C#/.NET 10 and WPF with WinRT Bluetooth Low Energy and Wi-Fi Direct APIs. Desktop advertises its presence, searches for Veyro peers, provides PIN-protected pairing and a Trust Hub, and establishes an authenticated fast channel over Wi-Fi Direct.

The product is designed for direct communication between device radios. Internet access, cloud services, a router, and prior membership in the same local network are not part of its primary path.

## Structure

```text
veyro-desktop/
├── docs/
├── protocol/                  # Desktop copy of the Protobuf contracts
├── src/
│   ├── Veyro.Desktop/           # WPF UI, tray, and Windows integrations
│   └── Veyro.Desktop.Core/      # identity, logs, framing, and contracts
└── tests/
    └── Veyro.Desktop.Core.Tests/
```

Desktop and Mobile have independent repositories and builds. The compatible Desktop contracts are kept in this repository under `protocol/` so this project never depends on files outside its root.

## Requirements

- 64-bit Windows 10/11;
- .NET SDK 10.0.302 or a compatible patch;
- Bluetooth Low Energy and Wi-Fi Direct for transport milestones.

## Build and test

From `desktop/`:

```powershell
dotnet build Veyro.Desktop.slnx
dotnet test Veyro.Desktop.slnx
```

The development executable is generated at `src/Veyro.Desktop/bin/Debug/net10.0-windows10.0.19041.0/Veyro.exe`.

## Local data

- identity: `%LOCALAPPDATA%\Veyro\identity.dat`, protected for the current user with DPAPI;
- identity key: `%LOCALAPPDATA%\Veyro\identity-key.dat`, protected with DPAPI;
- Trust Hub: `%LOCALAPPDATA%\Veyro\trusted-devices.dat`, protected with DPAPI;
- logs: `%LOCALAPPDATA%\Veyro\logs`, stored as JSON Lines with sensitive-property sanitization.

Never log clipboard content, SMS messages, contacts, notifications, PINs, keys, or payloads.

## Integration status

- Milestone 1: complete;
- Milestone 2 on Windows: implemented and covered by automated tests;
- Milestone 3 on Windows: Wi-Fi Direct group, BLE negotiation, mutual TLS, keepalive, and resumption implemented;
- Milestone 4 on Windows: simultaneous secure sessions, signed logical addressing, forwarding, bounded deduplication, group membership, and deterministic coordinator election implemented;
- Android `0.1.9-alpha`: BLE contract, pairing, Wi-Fi Direct, and TLS channel implemented and covered by local tests;
- physical Windows ↔ Android interoperability: ready for the joint acceptance test in `../mobile/docs/DESKTOP_INTEROPERABILITY.md`;
- file delivery and other user-facing features: part of Milestone 5.

While the product remains in alpha, each published update increments the patch: `0.1.0-alpha`, `0.1.1-alpha`, `0.1.2-alpha`, and so on.
