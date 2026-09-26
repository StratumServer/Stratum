# Class change commands

`/class` lets a player change their character class in game, and `/classrequests`
lets staff approve or deny the changes a player cannot make on their own. It is
server side only and needs no custom client. See issue #278.

The implementation is in
[`CmdStratumClassChanges.cs`](../../sources/VintagestoryLib/Vintagestory.Server/CmdStratumClassChanges.cs),
[`StratumClassChangeStore.cs`](../../sources/VintagestoryLib/Vintagestory.Server/StratumClassChangeStore.cs),
and [`StratumCharacterClassHook.cs`](../../sources/VintagestoryApi/Server/StratumCharacterClassHook.cs),
which [`Character.cs.patch`](../../patches/VSSurvivalMod/Systems/Character/Character.cs.patch)
connects to vanilla's `CharacterSystem`.

## At a glance

| Command | Audience | Access config key | Default privilege | Default roles |
| --- | --- | --- | --- | --- |
| `/class` | Players | `Commands.ClassChange` | `stratum.classchange` | player and moderator roles |
| `/classrequests` | Staff | `Commands.ClassChangeManage` | `stratum.classrequests` | `sumod`, `crmod` |

The default roles only get these privileges on a fresh `serverconfig.json`. On an
existing server, add them with `/roles` or by editing the roles.

## How a change works

A player runs `/class change <class> [reason]`.

- While they have **self-service changes** left, the class changes at once and
  one change is used up.
- Once they have none left, the change becomes a **request**. Everyone online
  who can use `/classrequests` sees it in chat, with the player's current class,
  the requested class and the reason. Staff who join later see how many
  requests are waiting.
- A player has at most one open request. A new one replaces the old one.

An approval switches the class straight away if the player is online. If they
are offline it applies on their next join. A denial is shown to the player
right away if they are online, and on their next join otherwise.

The class change itself is vanilla's `CharacterSystem.setCharacterClass`. It
removes the old class's traits and applies the new ones. Like a vanilla
reselection outside creative mode, it does not hand out the new class's
starting gear, and it keeps the player's appearance. It also stamps
`LastCharacterSelectionDate`, so vanilla's `allowClassChangeAfterMonths` counts
from this change.

A player has to have created their character before `/class change` works.

This is separate from vanilla's `allowcharselonce` and the character dialog,
which keep working as before. Changes made through the dialog do not use up
self-service changes.

## Config

In `stratum-commands.json`, under `Commands.ClassChangeSettings`:

| Key | Default | Meaning |
| --- | --- | --- |
| `SelfServiceChanges` | `0` | How many changes a player may make before they need approval. `-1` means unlimited, `0` means every change is a request. |
| `MaxReasonLength` | `200` | Longest reason a player may give, in characters (1 to 1000). |

Both can be changed on a running server, for example
`/stratum set Commands.ClassChangeSettings.SelfServiceChanges 1`.

The count of changes a player has used is stored on their player data under
`stratum.class-changes-used.v1`. Requests are stored in
`stratum.classrequests.json` in the server config folder. The file keeps every
open request and the 200 most recent closed ones.

## `/class`

```
/class
/class list
/class change <class> [reason]
/class cancel
```

| Argument | Meaning |
| --- | --- |
| _(none)_ or `info` | Show your class, your remaining self-service changes, and your open request. |
| `list` | List the class codes and names. |
| `change <class> [reason]` | Change class, or request the change. The class is a code from `/class list`, in any case. |
| `cancel` | Withdraw your open request. |

## `/classrequests`

```
/classrequests
/classrequests list [pending|approved|denied|cancelled|all]
/classrequests info <id>
/classrequests approve <id> [note]
/classrequests deny <id> [reason]
```

| Argument | Meaning |
| --- | --- |
| _(none)_ or `list` | List pending requests, newest first, 20 at most. Pass a status to list others. |
| `info <id>` | Show one request in full, including who settled it and whether it has been applied. |
| `approve <id> [note]` | Approve a pending request. The note is shown to the player. |
| `deny <id> [reason]` | Deny a pending request. The reason is shown to the player. |

A request can only be settled once. Approving a request for a class the server
no longer has fails, so deny it instead. The console can always use
`/classrequests`.

Every change, request, approval, denial and withdrawal is written to the audit
log.
