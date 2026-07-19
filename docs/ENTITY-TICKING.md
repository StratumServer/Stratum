# Entity tick throttling

Stratum throttles server-side entity ticking by distance to the nearest player
(`Performance.EntityTicking` in `stratum.json` or the `stratum-performance.json`
sidecar, enabled by default). This page documents the exact semantics: what
anchors the distance bands, what is exempt, and what to expect when testing
against the throttle. All defaults below are the shipped values; every knob
lives under `Performance.EntityTicking`.

## Distance bands

Each entity ticks every Nth server tick based on the 3D euclidean distance to
the **nearest anchoring player**:

| Band | Distance (blocks) | Interval | Key |
|---|---|---|---|
| Near | <= 32 | every tick | `NearDistanceBlocks` |
| Mid | <= 64 | every 2nd tick | `MidDistanceBlocks`, `MidTickInterval` |
| Far | <= 96 | every 5th tick | `FarEntityDistanceBlocks`, `FarEntityTickInterval` |
| Very far | beyond | every 10th tick | `VeryFarTickInterval` |

Band edges are clamped so they never invert (`near <= mid <= far`), and the
intervals so they never decrease with distance.

## What anchors the bands: Playing clients only

Distances are measured to players **whose entity has spawned**, meaning clients
in the Playing state. This is intentional and worth internalizing:

- A client that is still connecting, authenticating, or downloading assets has
  no entity yet and anchors nothing.
- The measurement is **per dimension**: a player in another dimension does not
  anchor entities in this one.
- With **no anchoring player at all** (empty server, or none in the entity's
  dimension), every entity ticks at the very-far interval (1 in 10 by
  default). Entities never stop ticking entirely.

## Exemptions

Checked per entity, in this order:

- **Player entities** are never throttled.
- **`ThrottleCreatures` / `ThrottleInanimate`** (both `true`): turn off
  throttling for a whole entity class.
- **Moving entities** (`SkipMovingEntities`, `true`): an entity whose motion
  magnitude exceeds `MovingEntitySpeedThreshold` (0.01 blocks/tick) is not
  throttled that tick. Note that even a dropped item keeps enough residual
  motion to stay exempt for a long time.
- **Excluded mod domains** (`ExcludedModDomains`, default `["vsvillage"]`):
  entities from these asset domains are never throttled and are also exempt
  from the creature budget below. Custom AI/pathfinder mods can assume
  near-constant tick frequency; throttling them can corrupt their internal
  state machines (observed with VSVillage's pathfinder: villagers froze at
  town edges under an earlier version of this system). Resolved once per
  entity and cached.

## Ambient fauna tier

On top of the band interval, entities whose code path matches
`AmbientFaunaCodePrefixes` (fish, butterflies, bees, hens...) get an extra
stride multiplier (`AmbientFaunaTickMultiplier`, default 2): background mood
does not need full-rate AI even near a player. Disable with
`AmbientFaunaTierEnabled: false`.

## Creature budget

`MaxCreatureTicksPerTick` (default 0 = unlimited) hard-caps creature ticks per
server tick with round-robin fairness. Opt-in, and **has no effect in parallel
physics mode** (all creatures tick every frame there; `/stratum health` warns
when this combination is configured).

## Restoring vanilla behavior

`Performance.EntityTicking.Enabled: false` restores exact vanilla ticking:
every loaded entity ticks once per entity-simulation tick, everywhere.

## Testing against the throttle

Hard-won notes for anyone writing scenarios or load tests against this system
(this is how the [StratumParity](https://github.com/Pixnop/StratumParity)
differential suite pins these semantics down):

- **Your test client must reach the Playing state** before it anchors
  anything. A headless client stuck in Connected leaves the server in the
  no-anchor case above, and every entity quietly ticks at the very-far rate.
- **Keep probe entities motionless** or `SkipMovingEntities` will exempt them
  from the very throttle you are measuring. A straw dummy stands perfectly
  still; a dropped item does not.
- **Derive geometry from the player's actual position**, read after any
  teleport settles: joins scatter players around the spawn point, which can
  silently move a probe across a band boundary.
- **Assert exact counts, not rates**: an unthrottled entity ticks exactly once
  per entity-simulation tick, a very-far entity exactly 1 in 10 (up to stride
  phase at the window edges). With the toggle off, both must equal the
  simulation tick count exactly.
- Same-dimension only: a probe in another dimension measures the no-anchor
  case, not the band you placed a player in.
