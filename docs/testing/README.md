# Testing procedures

These are operator procedures, not independent progress trackers. Record current
results once in the [master roadmap](../ROADMAP.md#state-and-evidence), with exact
versions/environment and a linked evidence record. Follow kernel-first capability
ordering; do not install example historical versions merely because a guide names them.

- [Core CLI / processes / filesystem](CLI-VM-TEST.md)
- [Text and traversal](CLI-TEXT-VM-TEST.md)
- [Network clients / TLS](NETWORK-VM-TEST.md)
- [Protected graphics](GRAPHICAL-VM-TEST.md)
- [Appearance settings](SETTINGS-VM-TEST.md)

[Build/native qualification commands](../DEVELOPMENT.md). Mandatory gates are
selected by the shared validation controller; targeted procedures do not replace
full clean-source Release qualification before a new kernel tag.
