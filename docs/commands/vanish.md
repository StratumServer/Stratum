# Vanish

`/vanish` hides a staff member from other players. It requires the
`stratum.vanish` privilege, which the `sumod`, `crmod` and admin roles carry by
default.

```text
/vanish            toggle
/vanish on|off     set explicitly
/vanish status     show your own state
/vanish hideothers [on|off|toggle]
```

`hideothers` is your own per-session preference: with it on you stop seeing
other vanished staff. It is one-directional and it resets to
`commands.vanishHideOtherVanishedDefault` on reconnect.

Vanish persists across a reconnect and across a server restart. It is restored
during the connecting client's identification, before the player entity is
spawned for anyone, so a reconnect never flashes the vanished player to nearby
clients and produces no join message. Losing the `stratum.vanish` privilege
while the stored flag is set clears both on the next join.

## What vanish hides

- The player entity itself: spawn, despawn, position, animation, attribute and
  tag packets, for every client that must not see them.
- Player data (packet 41): inventory, armor, privileges, and the entry in the
  client's player list.
- The active hotbar slot, so observers never learn what a vanished player holds.
- A mount they are riding, unless the observer is riding the same mount.
- Projectiles they fire: arrows, thrown spears, thrown stones and snowballs,
  and fishing bobbers, for the whole flight rather than only at spawn.
- `/near` results, map disclosure, join and leave messages.

## What vanish does NOT hide

Vanish filters entity and player packets. It does not filter world effects, so
a vanished player still gives their position away by interacting with the
world. Specifically, out of scope today:

- **Blocks.** Breaking, placing or using a block broadcasts the change to
  every client in range, with coordinates. So does opening a door or a chest.
- **Particles.** Block-break particles, footstep dust, splashes and any other
  particle a player's actions spawn are broadcast unfiltered.
- **Sounds.** Footsteps, tool use, block break and place sounds, and eating
  and drinking sounds are broadcast unfiltered and carry a position.

Block entity contents, chunk updates and any mod that broadcasts its own
packets are equally unfiltered.

A vanished staff member who needs to stay undetected should observe only: move,
look, and use commands. Anything that touches the world is visible to everyone
nearby regardless of vanish.
