# Inventory privacy

Stratum can hide private inventory contents from public entity and block-entity
packets. It is disabled by default. Enable it with:

```text
/stratum set hardening.inventoryGuards true
```

The setting takes effect immediately and persists across restarts. When disabled,
the server keeps the vanilla inventory visibility behavior. When enabled,
inventory contents are sent only to players with an open inventory and current
access; rejected moves receive an authoritative rollback.

The filter preserves public item appearance data while removing nested backpack
contents from entity updates. It also applies to attached and contained
inventories, subject to the normal range and claim checks. Firepit contents stay
public because they are rendered as part of the block's world display. Other
custom containers hide their contents unless they explicitly select display
slots.
