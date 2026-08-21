# Veyro Desktop — Milestone 3

## Outcome

The Windows side of Milestone 3 implements:

- Wi-Fi Direct group publication and acceptance through WinRT APIs;
- programmatic Wi-Fi Direct peer discovery;
- retrieval of link-exclusive addresses;
- role, port, ALPN, and resume-token negotiation over GATT;
- signing of the fast-channel offer with the persistent identity;
- a TCP socket explicitly bound to the Wi-Fi Direct address;
- mutual TLS 1.2/1.3 with ALPN `veyro/1`;
- certificate pinning to the public key already confirmed in the Trust Hub;
- a post-TLS version and identity hello;
- keepalive every 5 seconds with a 15-second timeout;
- authenticated resumption for up to 5 minutes;
- fast-channel state exposed in the interface.

## Infrastructure independence

The IP address is not advertised over BLE and cannot be supplied by external configuration. After group formation, Desktop obtains `LocalHostName` and `RemoteHostName` directly from `WiFiDirectDevice.GetConnectionEndpointPairs()`.

The TCP server listens only on the local address from that endpoint pair. The client binds to the same address before connecting to the remote direct-link address. Ethernet, infrastructure Wi-Fi, loopback, or LAN addresses are therefore never selected as a silent fallback.

Desktop publishes an autonomous group and accepts requests through `WiFiDirectConnectionListener`. Group-owner preference is negotiated with the peer; multi-member topology remains part of Milestone 4.

## BLE negotiation

`protocol/veyro_transport.proto` adds `FastChannelOffer` and `FastChannelAnswer` to `BleControlPacket`.

The offer contains:

- session ID;
- offering-device ID;
- group role;
- TCP port;
- ALPN;
- random resume token;
- ECDSA signature of the canonical offer.

The offer never includes an IP address. It uses the `Veyro.FastChannelOffer.v1` signature domain and big-endian `uint32` length prefixes for variable fields. Offers from devices that are absent from, or revoked in, the Trust Hub are rejected before socket creation.

## TLS and identity

Desktop creates an in-memory self-signed X.509 certificate with the installation's persistent ECDSA P-256 key. On Windows, the key is temporarily imported into the CNG store required by Schannel.

The certificate's public chain is not used as an authority. Validation compares the certificate's public SPKI in constant time against the Trust Hub key and requires the expected device ID in the `CN`. Both peers present a certificate, so an untrusted member of the Wi-Fi Direct group cannot open a Veyro session.

After TLS, both peers send `FastChannelHello` with the session, identity, and major/minor version. A session, identity, or major-version mismatch closes the socket.

## Keepalive and resumption

`FastChannelPacket` multiplexes hello, keepalive, acknowledgement, resumption, and `TransportEnvelope` data inside the Milestone 1 `VYRO` framing. All framing travels inside `SslStream`.

No incoming packet for more than 15 seconds closes the session. The 32-byte resume token is bound to the peer ID and session, compared in constant time, and expires after 5 minutes. The acknowledged sequence can never move backwards.

After reconstructing the Wi-Fi Direct link, the coordinator reuses still-valid state, repeats TLS and the hello, and negotiates `ResumeRequest`/`ResumeResponse` before releasing the session.

## Completed validation

- WinRT Wi-Fi Direct manager compiled for the Windows 10/11 target;
- executable initialized stably with the Wi-Fi Direct API available;
- real loopback TCP socket between two automated peers;
- mutual TLS 1.3 authentication through Schannel;
- wrong certificate rejected by Trust Hub pinning;
- session and version hello in both directions;
- Protobuf packet transported through framing inside TLS;
- signed BLE offer with tamper rejection;
- resume token, expiration, and sequence validation.

Loopback validates the socket protocol and security but does not replace radio testing. If Bluetooth is disabled, the application reports that state without attempting to start GATT.

## Physical validation

Veyro Android implements the Milestone 2 and 3 contracts: BLE/GATT, identity and Trust Hub, `WifiP2pManager`, signed-offer validation, mutual TLS, framing, hello, keepalive, resumption, and `TransportEnvelope`.

The physical validation procedure covers:

1. installing the Android `0.1.9-alpha` development APK;
2. enabling Bluetooth and Wi-Fi on both devices;
3. pairing through the PIN;
4. forming the group without internet or router membership;
5. verifying TLS, keepalive, link loss, and group reconstruction;
6. repeating an Android ↔ Android test to confirm that Nearby did not regress.

The detailed checklist is maintained with the interoperability documentation.
