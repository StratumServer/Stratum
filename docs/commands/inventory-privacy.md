# Inventory privacy

Stratum can hide private inventory contents from public entity and block-entity
packets. Enable it with:

```text
/stratum set hardening.inventoryGuards true
```

The setting applies after a reload or restart. When disabled, the server keeps
the vanilla inventory visibility behavior. When enabled, inventory contents are
sent only to players with an open inventory and current access; rejected moves
receive an authoritative rollback.

The filter preserves public item appearance data while removing nested backpack
contents from entity updates. It also applies to attached and contained
inventories, subject to the normal range and claim checks.
