# Inventory privacy

Stratum hides private inventory contents from public entity and block-entity
packets by default. Existing configurations below version 4 migrate once and
enable the guard. After that migration, an operator can disable it with:

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

Real storage, meaning openable containers such as chests, hides its contents
entirely until a player opens it. Display blocks keep their whole tree, since
the client rebuilds the block mesh from it: shelves, display cases, and the
other blocks that render their own contents show what is in them. A few block
types keep a fixed subset instead of the whole tree, because those slots are
what the world renders: a firepit keeps its fuel, input, and output slots; a
quern keeps its input slot; an unsealed barrel keeps its two visible slots and
a sealed one keeps none; a crate keeps the one stack it renders on its model,
or none with the lid closed. A custom block entity container that does not
derive from one of these keeps its contents public unless it derives from the
openable-container base or opts into display slots itself.
