# Inventory privacy

Stratum hides private inventory contents from public entity and block-entity
packets by default. Existing servers can disable it with:

```text
/stratum set hardening.inventoryGuards false
```

The setting takes effect immediately and persists across restarts. When disabled,
the server keeps the vanilla inventory visibility behavior. When enabled,
inventory contents are sent only to players with an open inventory and current
access; rejected moves receive an authoritative rollback.

The filter preserves public item appearance data while removing nested backpack
contents from entity updates. It also applies to attached and contained
inventories. Bags attached to an entity use the live entity's interaction range
without a claim test; bags contained in a block use the block position, range,
and claim checks; and a player's own inventories are checked by ownership.
Firepit contents stay public because they are rendered as part of the block's
world display. Other custom containers hide their contents unless they explicitly
select display slots.
