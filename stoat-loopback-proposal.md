# Two Wanderers outside Stoat

The November 2026 proposal asked whether a friend could direct a character in a host's campaign while keeping their own camera. The first playable milestone passed on Windows MeitouClient 0.20.0 with two loopback clients, one test squad and one guest-controlled Scout. The host moved the Guide while the guest panned away, returned, selected the Scout at her destination and gave her a second ground order. [Replay and measured results](https://github.com/mattresspad/MeitouP2P-Coop/blob/main/docs/stoat-results.md)

The original two-squad campaign remains a future milestone. It requires stable identities for characters sharing a template, a save/reload test with a returning guest, and a trial on separate computers. The current fixture assigns its Scout per connection and per world. A successful smoke test for world replacement demonstrates grant invalidation. Campaign persistence needs a separate test.

MeitouClient's maintainer confirmed that a remote-character-state hook is still absent. The preview uses a version-specific Co-op adapter. [Martin's answer](https://github.com/VlcekM/MeitouClient/issues/749#issuecomment-6898903424)

nhoral's allied squads and zeroit789's start provided the gameplay reference. [KenshiCoop](https://github.com/nhoral/KenshiCoop) · [Earlier save report](https://github.com/nhoral/KenshiCoop/issues/74)
