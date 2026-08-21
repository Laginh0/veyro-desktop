# Veyro Desktop — Milestone 1

## Outcome

The Windows application is isolated by platform inside the Veyro workspace:

```text
Veyro/
├── mobile/       # Android project
├── desktop/      # Windows/C#/.NET/WPF project
└── protocol/     # shared Protobuf contracts
```

Desktop is an executable foundation. It creates a persistent local identity, checks the availability of the planned Windows APIs, exposes status through a minimal interface, and remains accessible from the system tray.

## UI decision

The local probe compiled and ran the WinRT projections for:

- `BluetoothLEAdvertisementWatcher`;
- `WiFiDirectDevice.GetDeviceSelector`.

WPF on .NET 10 was selected because it provides direct WinRT and system-tray integration with few dependencies. The core does not depend on WPF, so a limitation found during physical testing can motivate another UI layer without discarding identity, logs, framing, or contracts.

## Contracts

`protocol/veyro_message.proto` preserves the application contract shared with Mobile. `protocol/veyro_transport.proto` separately defines transport metadata and negotiation messages:

- major/minor version;
- immutable message ID;
- source and destinations;
- explicitly authorized broadcast;
- payload type;
- validity window;
- forwarding limit;
- sequence and acknowledgement;
- opaque origin authentication;
- payload opaque to the coordinator.

The root `protocol/` folder is now the single source of truth consumed by Desktop and Mobile. Contract changes must be deliberate and compatibility-tested.

## Initial framing

The control framing is deliberately small:

| Field | Size | Description |
| --- | ---: | --- |
| Magic | 4 bytes | ASCII `VYRO` |
| Version | 1 byte | framing version, initially `1` |
| Flags | 1 byte | reserved for documented semantics |
| Reserved | 2 bytes | must be zero |
| Length | 4 bytes | unsigned, big-endian integer |
| Payload | variable | at most 1 MiB on the control channel |

The reader supports fragmented streams and rejects invalid magic/version values, nonzero reserved fields, oversized payloads, and truncated frames before data reaches higher layers. Large files do not use one control frame; they require dedicated streaming and flow control.

## Identity and security

Milestone 1 creates a random 16-character hexadecimal ID compatible with the Android identity format. The record is serialized and protected with user-scoped Windows DPAPI, using atomic replacement for writes.

Later milestones selected persistent ECDSA P-256 identity, ephemeral ECDH pairing, bilateral PIN confirmation, Trust Hub revocation, and mutual TLS. Authentication fields remain versioned protocol data rather than implicit platform behavior.

## Logs

Logs use JSON Lines. Property names related to clipboard data, SMS, phone calls, contacts, notifications, PINs, tokens, secrets, keys, authentication, content, and payloads are redacted. Identifiers receive a truncated hash, and line breaks are removed to prevent log injection.

## Original Milestone 1 boundary

The following items were intentionally outside Milestone 1 and were implemented in later milestones:

- real BLE advertising and scanning;
- pairing and Trust Hub;
- Wi-Fi Direct group and sockets;
- session encryption;
- Android connection;
- automatic startup and installer.

The initial interface did not simulate these capabilities. Milestone 2 began with real BLE discovery and bilateral pairing.
