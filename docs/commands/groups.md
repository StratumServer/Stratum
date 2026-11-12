# Group administration

`/group admin` gives staff authority over player groups that vanilla leaves to
the players themselves: putting a player in a group without an invite, pinning
them there, freezing a roster for the length of an event, labelling groups by
kind, relating groups to each other, and tagging them in chat and on nametags.
It is server side only and needs no custom client.

Implemented in
[`StratumGroupPolicy.cs`](../../sources/VintagestoryLib/Vintagestory.Server/StratumGroupPolicy.cs),
[`CmdStratumGroups.cs`](../../sources/VintagestoryLib/Vintagestory.Server/CmdStratumGroups.cs),
[`StratumGroupsConfig.cs`](../../sources/VintagestoryLib/Vintagestory.Server/StratumGroupsConfig.cs),
with gates in
[`ServerySystemPlayerGroups.cs.patch`](../../patches/VintagestoryLib/Vintagestory.Server/ServerySystemPlayerGroups.cs.patch)
and four persisted fields in
[`PlayerGroup.cs.patch`](../../patches/VintagestoryApi/Server/PlayerGroup.cs.patch).

## At a glance

| Command | Audience | Access config key | Default privilege | Storage |
| --- | --- | --- | --- | --- |
| `/group admin` | Staff | `Commands.GroupAdmin` | `manageotherplayergroups` | `playergroups.json` (group state), player data (locks) |

Nothing here changes behaviour until you use it. A server that never defines a
kind and never runs `/group admin` behaves exactly as it did before.

## Syntax

```
/group admin add <group> <player> [access]
/group admin remove <group> <player>
/group admin lock <group> <player> [duration]
/group admin unlock <group> <player>
/group admin freeze <group> [on|off]
/group admin kind <group> [kind|none]
/group admin tag <group> [text|none]
/group admin relation <group> <other group> [ally|enemy|neutral]
/group admin info <group>
/group admin kinds
```

| Argument | Meaning |
| --- | --- |
| `<group>` | Group name, or its numeric Uid. |
| `<player>` | Player name. Offline players work, as long as they have joined once. |
| `access` | `1` = Member (default), `2` = Op, `3` = Owner. Same scale as vanilla `/group addplayer`. `0` = None is refused on both routes, because it is not a membership. Use `/group admin remove` instead. |
| `duration` | Omitted or `perm` for until staff unlock it, otherwise `30s`, `90m`, `4h`, `3d`, `2w`. |

Every write is written to the audit log with the acting staff member's name.

## Kinds

A kind is a label on a group that decides how it behaves. Kinds are defined in
`stratum.json` under `Groups.Kinds` and applied per group with
`/group admin kind`. A group with no kind is **unclassified**, which is what
every group made before this feature is, and what `/group create` produces
unless `Groups.DefaultKind` says otherwise.

| Kind field | Meaning |
| --- | --- |
| `Code` | The name used by `/group admin kind`. |
| `Exclusive` | A player may hold at most one live membership of this kind. Two *different* exclusive kinds do not conflict, so one faction plus one arena team is fine. Turning this on for a kind that groups already carry does not move anyone. Boot and `/stratum reload` log a warning naming each player left in two groups of the kind, and the reload reply lists them too. |
| `MaxMembers` | `0` for unlimited. Player joins are refused at the cap; staff adds go past it and say so in the audit log. |
| `Lockable` | Whether `/group admin lock` applies to groups of this kind. |
| `CountsAsSameSide` | Whether sharing this group counts as being on the same side, which is what `FriendlyFire.AllowGroupDamage=false` reads. |
| `PlayerCreatable` | Whether `/group create` may produce this kind. Only consulted for `Groups.DefaultKind`. |
| `Tag` | Fallback tag for groups of this kind that carry no tag of their own. |
| `TagPriority` | Highest wins when a player holds several tagged groups. Ties break on the lower group Uid. |

`CountsAsSameSide` is worth a moment. Before kinds, *any* shared group switched
friendly fire off between its members, so an administrative group like
"newcomers" or "event-signups" quietly disabled PvP for everyone in it. Give
such groups a kind with `CountsAsSameSide: false` and they stop doing that.
The default for every kind, and for unclassified groups, is `true`, so this
only changes once you ask for it.

Kinds and relations are combat rules only. Map privacy
(`AllowGroupMapVisibility`) still asks the plain question, "do these two share a
group?", so a `CountsAsSameSide: false` group keeps its members visible to each
other, and an alliance never shows one group's coordinates to the other.

If `Groups.DefaultKind` is exclusive, `/group create` refuses a player who
already holds a group of that kind, since the creator joins the new group as its
owner.

An example pair of kinds:

```json
"Groups": {
  "DefaultKind": null,
  "Kinds": [
    { "Code": "faction", "Exclusive": true,  "MaxMembers": 0, "Lockable": true,  "CountsAsSameSide": true,  "PlayerCreatable": false, "TagPriority": 100 },
    { "Code": "utility", "Exclusive": false, "MaxMembers": 0, "Lockable": false, "CountsAsSameSide": false, "PlayerCreatable": true,  "TagPriority": 10 }
  ]
}
```

## Adding and removing players

Vanilla already has `/group addplayer <group> <player> <access>` behind
`manageotherplayergroups`, and it adds without an invite. `/group admin add` is
the same act with the kind rules applied, and it is the spelling the rest of
this page uses. Both routes go through the same checks.

Where a player join is *refused* on an exclusive-kind conflict, a staff add
**reassigns**: the player leaves the conflicting group and joins the named one,
and the reply says which group they left. That is what "put this player on red
team" means during an event. The one thing that stops it is a lock on the group
they would be leaving; clear that first.

`/group admin info` lists the group's online members as well as its member
count. The online list is what tag changes walk to update nametags, so a
player missing from it keeps a stale tag.

`/group admin remove` clears any lock on that group as it removes the player,
because a staff removal is deliberate.

## Locks

`/group admin lock <group> <player> [duration]` pins a player into a group.
While locked they cannot `/group leave` it, and a group op cannot kick them out
of it either, which would otherwise void the lock from the group UI. The owner
cannot `/group disband` a group that has a locked member, their own lock
included, because disbanding removes everyone at once. Staff removal still
works; to disband, staff unlock or remove the locked members first.

Locks live in the player's own data, so they survive a restart, apply to
offline players and disappear with the player. They are never swept on a timer:
an expired lock lapses the next time it is read.

## Roster freeze

`/group admin freeze <group> on` stops the roster moving: no joins, no invites,
no accepted invites, no leaves, no kicks, no disband. Staff commands still go
through. This is the blunt instrument for the length of a tournament; locks are
the per-player version.

## What players see

`/group admin` is staff only, but the state it writes governs ordinary players, so the
vanilla `/group info [group]` carries the Stratum half of it too:

```
Created: 9/22/2026 7:41:02 PM
Created by: Alice
Members: Alice, Bob
Kind: faction, one at a time
Tag: [RED]
Roster: frozen - no joins, invites, leaves or kicks until staff unfreeze it
Relations: BlueTeam=enemy, Greens=ally
Your membership: locked by staff until 2026-09-23 18:00 UTC - you cannot leave and cannot be kicked
```

Every one of those lines is omitted when it has nothing to say, so a group with no kind,
no tag, an open roster and no relations prints exactly what vanilla printed.

Relations are public on purpose. An ally relation decides whether a player's weapon lands
on someone, so it is already discoverable by swinging at them, and a tag is already on
their nametag. Saying it outright beats making players reverse-engineer it from combat.
Set `Groups.ShowStateToPlayers` to `false` to keep the whole block to staff.

`Your membership` is the caller's own lock only. Who else is pinned into a group is staff
business and stays in `/group admin info`.

## Relations

`/group admin relation <group> <other> ally|enemy|neutral` records how two
groups stand. Relations are written to both groups at once, so they read the
same from either side.

With `Groups.AlliesCountAsSameSide` (default `true`), an `ally` relation also
turns friendly fire off between the two groups' members, as long as both kinds
count as the same side. When two players hold several groups each, the pairs
are weighed in this order:

1. Sharing a group of a counting kind outright makes them the same side.
2. Otherwise, any `enemy` pair between their groups makes them not the same
   side, even if another pair is allied. That lets you carve one pairing out of
   a wider alliance.
3. Otherwise, any `ally` pair makes them the same side.

Set `Groups.RelationsEnabled` to `false` to switch the subcommand off.

## Tags

`/group admin tag <group> RED` puts `[RED]` in front of that group's members in
chat and on their nametags, ahead of any role prefix, so a tagged staff member
reads `[RED] [Admin] Alice`. `none` clears it. `Groups.MaxTagLength` (default
8) caps the length, `Groups.TagFormat` controls the brackets, `Groups.TagColor`
colours it in chat, and `Groups.ShowTagInChat` / `Groups.ShowTagInNametag`
switch each surface off.

Tags reach vanilla clients through the ordinary nametag and chat paths, the
same way role prefixes do. See [role-prefixes.md](../role-prefixes.md).

## Config keys

All under `Groups` in `stratum.json`:

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Turns the whole feature, including `/group admin`, off. |
| `DefaultKind` | `null` | Kind stamped onto groups made with `/group create`. Must exist and be `PlayerCreatable`. `none` means the same as `null`. |
| `Kinds` | one example `faction` entry, used by nothing until a group is given it | Kind definitions, see above. |
| `RelationsEnabled` | `true` | Whether `/group admin relation` works. |
| `AlliesCountAsSameSide` | `true` | Whether allied groups count as one side for friendly fire. |
| `ShowStateToPlayers` | `true` | Whether `/group info` shows kind, tag, roster state and relations to ordinary players. |
| `ShowTagInChat` | `true` | Render group tags in chat. |
| `ShowTagInNametag` | `true` | Render group tags on nametags. |
| `TagFormat` | `[{tag}]` | Must contain `{tag}`. |
| `TagColor` | `null` | Chat colour for the tag, a colour name or `#rrggbb`. |
| `MaxTagLength` | `8` | Longest tag `/group admin tag` accepts. |

`Commands.GroupAdmin` in `stratum-commands.json` holds the access config for
the subcommand, defaulting to the vanilla `manageotherplayergroups` privilege.
It is read on every call, like the other Stratum commands: `Commands.Enabled`,
`Commands.GroupAdmin.Enabled`, its privilege and its cooldown all take effect
on `/stratum reload` or `/stratum set` without a restart.

The privilege can only narrow access, not widen it. `/group admin` sits under
the vanilla `/group` tree, which already requires `controlplayergroups`, so a
caller needs that as well as `Commands.GroupAdmin.Privilege`. Setting the
privilege to `chat` does not open the subcommand to ordinary players.

## Testing it

`tests/StratumScenarios/GroupAdminScenarios.cs` covers the rules end to end
against a live server: exclusive-kind refusal, staff reassignment, locks and
their expiry, kick and disband refusal, roster freeze, member caps, the
exclusive `DefaultKind` on create, runtime access config, kind-aware and
relation-aware same-side checks kept apart from map privacy, and tags following
both staff and ordinary membership changes. Run with `make scenarios`.
