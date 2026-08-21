# Veyro Desktop — Milestone 2

## Outcome

The Windows side of Milestone 2 implements:

- real BLE presence advertising and scanning;
- ephemeral identity in advertisements, without exposing the persistent ID;
- GATT server and client roles for the control channel;
- capability negotiation;
- pairing with a six-digit PIN derived independently by both devices;
- explicit, signed confirmation on both sides;
- a persistent per-installation identity key;
- a DPAPI-protected Trust Hub with revocation;
- a signed challenge to authenticate known-device reconnections;
- nearby-device and trusted-device interfaces.

Desktop does not use the internet, cloud, router, or LAN for discovery. Wi-Fi ADB used during development is only a diagnostic tool and is not part of the product transport.

## BLE

Service identifiers:

- GATT service: `68d0925e-266d-4ca5-9588-9c804c6cd8ff`;
- control characteristic: `886c164a-9f9f-465f-9428-8fb7ee8cd15a`;
- characteristic properties: `Write`, `WriteWithoutResponse`, and `Notify`;
- defensive control-packet limit: 512 bytes.

The advertisement uses Service Data with a 128-bit UUID (`AD type 0x21`). The UUID follows the Bluetooth-defined little-endian byte order. After the service UUID, the compact payload contains:

| Field | Size | Description |
| --- | ---: | --- |
| Major version | 1 byte | BLE protocol version |
| Capabilities | 1 byte | available-capability mask |
| Ephemeral ID | 6 bytes | random value renewed on each run |

Observations remain valid for 20 seconds, are updated by ephemeral ID, and are ordered by signal strength. The Bluetooth address is used only during discovery and is never persisted as identity.

## Pairing

The implementation uses established platform primitives:

- ECDSA P-256 for the persistent installation identity;
- ephemeral ECDH P-256 for the pairing-session secret;
- SHA-256/HMAC-SHA-256 for the transcript and verification PIN;
- user-scoped Windows DPAPI for the private key and Trust Hub.

Each `PairingHello` contains the session ID, claimed identity, capabilities, timestamp, nonce, identity public key, ephemeral ECDH public key, and signature. The accepted time window is two minutes. The PIN never travels over the radio: it is the six-digit value obtained from an HMAC of the ECDH secret over the canonical transcript ordered by device ID.

The signed transcript is platform-independent. Variable fields are UTF-8 or raw bytes with a big-endian `uint32` length prefix. 64-bit integers are big-endian. Domains are `Veyro.PairingHello.v1`, `Veyro.PairingConfirmation.v1`, and `Veyro.PairingVerification.v1`. Interoperability fixes `SHA-256(raw ECDH secret)` as the KDF and 64-byte ECDSA P1363 as the signature format.

The remote public key enters the Trust Hub only after both peers sign acceptance of the same verification digest. Rejection, invalid signatures, mismatched sessions, stale messages, or unconfirmed PINs never create trust.

## Reconnection

A known device must prove possession of the private key matching its active Trust Hub record. The challenger sends 32 random bytes; the peer signs the `Veyro.ReconnectChallenge.v1` domain, its device ID, and the challenge. Revoked records can never authenticate.

## Android contract

BLE messages are defined in `protocol/veyro_transport.proto`: `BleControlPacket`, `PairingHello`, `PairingConfirmation`, `ReconnectChallenge`, and `ReconnectProof`.

Veyro Mobile `0.1.9-alpha` implements the same UUIDs, advertisement layout, Protobuf messages, transcript, KDF, and signatures as Desktop. Android can act as advertiser/GATT server and scanner/GATT client while preserving the Nearby transport used between phones.

Local tests on both platforms validate packet sizing, matching PIN derivation between peers, bilateral confirmation, P1363 signatures, tamper rejection, and framing. Windows ↔ Android discovery and pairing await only the physical test with both radios active.

## Tests

Automated tests cover the advertisement codec, discovery expiration, key persistence, matching peer PINs, tampered-message rejection, bilateral confirmation, Protobuf serialization, protected Trust Hub storage, revocation, and reconnection proof.

The isolated Milestone 3 implementation can be exercised with sockets and automated peers. Physical acceptance still requires installing the `0.1.9-alpha` APK and completing bilateral pairing on real hardware.
