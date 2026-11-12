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
- Player data (packet 41): inventory, armor, and privileges. An observer who is
  already connected may retain the name in its player list for the rest of that
  session because the vanilla client only removes it after a real disconnect;
  new observers do not receive the vanished player's entry.
- The active hotbar slot, so observers never learn what a vanished player holds.
- A mount carrying only vanished passengers, for clients that must not see them.
  A mount shared with an ordinary player remains visible to bystanders, so its
  seat data can still reveal the vanished passenger and their position.
- Projectiles they fire: arrows, thrown spears, thrown stones and snowballs,
  and fishing bobbers, for the whole flight rather than only at spawn.
- `/near` results, map disclosure, join, leave, and death messages.

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

A vanished staff member's movement, look, and commands do not emit the entity or
player packets filtered above, but anything that touches the world is visible to
everyone nearby regardless of vanish. Sharing a mount with an ordinary player is
also a known limitation: the mount stays visible and its seat data can disclose
the vanished passenger.
