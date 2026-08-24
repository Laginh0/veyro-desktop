# Veyro star topology and Nearby failover

## Implemented behavior

When one Desktop and two or more Android devices are available, the Desktop is the preferred
coordinator. Every Android maintains its own authenticated Wi-Fi Direct/TLS fast channel to the
Desktop. Application messages addressed from Android A to Android B are forwarded by the logical
router on the Desktop.

The Desktop receives only the signed transport envelope and opaque encrypted payload for an
Android-to-Android message. The payload is encrypted by the origin Android for the destination
Android identity key. The Desktop cannot decrypt it.

The Desktop publishes a targeted `GroupTopologyEvent` to each authenticated Android whenever a
member joins or leaves. That snapshot contains the active member identifiers, display names and
public identity keys required for end-to-end routing. Android exposes those members as routed
devices in the normal connected-device selector.

## Transport policy

- Android + Android, without a Desktop hub: Google Nearby Connections.
- Android + Desktop: authenticated Wi-Fi Direct, TLS and the Veyro fast channel.
- Two Androids + Desktop: Desktop-coordinated Wi-Fi star; Google Nearby is suspended.
- BLE is discovery, pairing and fast-channel bootstrap only. It never carries application data.
- A late Google Nearby callback is rejected if the Desktop star has already become active.
- Duplicate Nearby and Desktop application routes are never kept at the same time.

## Failover

When the Desktop fast channel or its Wi-Fi Direct group disappears, Android removes the direct
Desktop endpoint and every routed member from the active star. If the continuous ecosystem is
enabled, it immediately restarts Google Nearby advertising and discovery. Previously trusted
Android devices can then reconnect using the existing automatic reconnection rules.

If the Desktop returns later, its authenticated fast channel becomes preferred again and any
duplicate Nearby sessions are explicitly disconnected before the star is exposed to features.

## Validation completed

- Mobile unit tests and debug APK assembly.
- Desktop protocol, crypto, fast-channel and routing tests.
- Desktop WPF application build.
- Android-to-Android ciphertext can be decrypted by its Android recipient but not by the Desktop.
- The Desktop router forwards a targeted envelope only to the requested Android and preserves the
  ciphertext while decrementing the hop limit.
- The topology contract round-trips its epoch, coordinator and member identity keys.
- The transport policy enables Nearby only when no Desktop Wi-Fi session remains.

## Three-device hardware test

1. Start discovery explicitly on the Desktop and both Androids.
2. Pair Android A with the Desktop and confirm the PIN on both sides.
3. Pair Android B with the Desktop and confirm the PIN on both sides.
4. Confirm that each Android lists the Desktop and the other Android as connected through the star.
5. Confirm in logs that neither Android is advertising or discovering through Nearby.
6. Run ping A→B and B→A, clipboard in both directions, media state/control and notification sync.
7. Disconnect the Desktop fast channel without disabling the Android ecosystem.
8. Confirm that both Desktop/routed endpoints disappear and Nearby advertising/discovery restarts.
9. Confirm that A and B reconnect through Google Nearby and repeat ping in both directions.
10. Restore the Desktop, reconnect both Androids and confirm that the Nearby duplicate is removed.

## Current feature boundary

Protocol messages such as ping, clipboard, media, notifications, battery, connectivity, contacts,
presentation, commands and remote-file navigation use the routed application channel. The legacy
raw Nearby file-payload path is intentionally not sent to a synthetic routed endpoint; star-mode
file bytes require the chunked `FileTransferEvent` path already used by Desktop before that specific
feature can be declared complete across Android-to-Android Desktop mediation.
