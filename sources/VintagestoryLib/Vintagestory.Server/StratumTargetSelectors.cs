using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// Friendly multi-target selector used by Stratum command overrides.
///
/// Syntax (single token, no spaces):
///   playername                 single online player by name (case-insensitive)
///   p1,p2,p3                   comma-separated list of online players
///   @all                       all admitted online players
///   @staff                     online players whose role is sumod/crmod/admin
///   @admin                     online players in the admin role
///   @mod / @mods               online players in sumod or crmod role
///   @&lt;rolecode&gt;          online players matching that exact role code
///
/// Falls back to the standard vanilla selector syntax (s[], o[], a[], p[], e[])
/// when the token starts with s/o/a/p/e/l followed by '['.
/// </summary>
internal static class StratumTargetSelector
{
    // Role buckets. Mirrors the defaults in StratumConfig (private there, duplicated here on purpose
    // so this stays usable even if config layout changes).
    private static readonly string[] StaffRoleCodes = { "sumod", "crmod", "admin" };
    private static readonly string[] AdminRoleCodes = { "admin" };
    private static readonly string[] ModRoleCodes = { "sumod", "crmod" };

    public static bool LooksLikeFriendlyToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (token[0] == '@') return true;
        if (token.IndexOf(',') >= 0) return true;
        return false;
    }

    public static bool LooksLikeVanillaSelector(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 2) return false;
        char c = char.ToLowerInvariant(token[0]);
        if (c != 's' && c != 'o' && c != 'a' && c != 'p' && c != 'e' && c != 'l') return false;
        return token[1] == '[';
    }

    /// <summary>
    /// Resolves a friendly token to online server players. Returns empty list if no match.
    /// Caller must check token via LooksLikeFriendlyToken first if vanilla syntax should be preserved.
    /// </summary>
    public static List<IServerPlayer> ResolveOnlinePlayers(string token, ICoreServerAPI api)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IServerPlayer>();
        if (string.IsNullOrWhiteSpace(token)) return result;

        IServerPlayer[] online = api.World.AllOnlinePlayers.OfType<IServerPlayer>().ToArray();

        foreach (string raw in token.Split(','))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;

            if (part[0] == '@')
            {
                string group = part.Substring(1).ToLowerInvariant();
                string[] roleFilter = MapGroupToRoles(group);
                if (group == "all" || group == "everyone" || group == "*")
                {
                    foreach (var p in online)
                    {
                        if (seen.Add(p.PlayerUID)) result.Add(p);
                    }
                }
                else if (roleFilter != null)
                {
                    foreach (var p in online)
                    {
                        string code = p.Role?.Code;
                        if (code == null) continue;
                        if (roleFilter.Any(r => string.Equals(r, code, StringComparison.OrdinalIgnoreCase))
                            && seen.Add(p.PlayerUID))
                        {
                            result.Add(p);
                        }
                    }
                }
                else
                {
                    // Treat @<text> as exact role code match
                    foreach (var p in online)
                    {
                        if (string.Equals(p.Role?.Code, group, StringComparison.OrdinalIgnoreCase)
                            && seen.Add(p.PlayerUID))
                        {
                            result.Add(p);
                        }
                    }
                }
            }
            else
            {
                var match = online.FirstOrDefault(p => string.Equals(p.PlayerName, part, StringComparison.OrdinalIgnoreCase));
                if (match != null && seen.Add(match.PlayerUID))
                {
                    result.Add(match);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves a friendly token to entities (online players' entities). Used by /tp source and /giveitem target.
    /// </summary>
    public static Entity[] ResolveOnlineEntities(string token, ICoreServerAPI api)
    {
        return ResolveOnlinePlayers(token, api)
            .Select(p => (Entity)p.Entity)
            .Where(e => e != null)
            .ToArray();
    }

    /// <summary>
    /// Resolves a friendly token to PlayerUidName pairs (online only). Used by /kick, /ban, /op overrides.
    /// </summary>
    public static PlayerUidName[] ResolveOnlinePlayerUids(string token, ICoreServerAPI api)
    {
        return ResolveOnlinePlayers(token, api)
            .Select(p => new PlayerUidName(p.PlayerUID, p.PlayerName))
            .ToArray();
    }

    private static string[] MapGroupToRoles(string group)
    {
        switch (group)
        {
            case "staff": return StaffRoleCodes;
            case "admin":
            case "admins": return AdminRoleCodes;
            case "mod":
            case "mods": return ModRoleCodes;
            default: return null;
        }
    }
}

/// <summary>
/// Drop-in replacement for vanilla EntitiesArgParser that also accepts Stratum friendly selectors
/// (@all, @staff, @admin, @mod, @&lt;role&gt;, comma-separated names). Falls back to base behaviour
/// for player names and vanilla p[]/e[]/s[]/l[] selectors.
/// </summary>
internal class StratumEntitiesArgParser : EntitiesArgParser
{
    private readonly ICoreServerAPI sapi;
    private Entity[] resolved;
    private bool resolvedAsFriendly;

    public StratumEntitiesArgParser(string argName, ICoreServerAPI api, bool isMandatoryArg)
        : base(argName, api, isMandatoryArg)
    {
        sapi = api;
    }

    public override string GetSyntaxExplanation(string indent)
    {
        return base.GetSyntaxExplanation(indent)
            + indent + "  Stratum extensions: comma-separated names (p1,p2), @all, @staff, @admin, @mod, @&lt;rolecode&gt;.";
    }

    public override object GetValue()
    {
        return resolvedAsFriendly ? resolved : base.GetValue();
    }

    public override void PreProcess(TextCommandCallingArgs args)
    {
        resolved = null;
        resolvedAsFriendly = false;
        base.PreProcess(args);
    }

    public override EnumParseResult TryProcess(TextCommandCallingArgs args, Action<AsyncParseResults> onReady = null)
    {
        string peek = args.RawArgs.PeekWord();
        if (peek != null && StratumTargetSelector.LooksLikeFriendlyToken(peek) && !StratumTargetSelector.LooksLikeVanillaSelector(peek))
        {
            args.RawArgs.PopWord();
            resolved = StratumTargetSelector.ResolveOnlineEntities(peek, sapi);
            resolvedAsFriendly = true;
            if (resolved.Length == 0)
            {
                lastErrorMessage = "No matching online players for selector '" + peek + "'";
                return EnumParseResult.Bad;
            }
            return EnumParseResult.Good;
        }

        return base.TryProcess(args, onReady);
    }
}

/// <summary>
/// Drop-in replacement for vanilla PlayersArgParser that also accepts Stratum friendly selectors.
/// Limited to online players (kick/ban/op want known online presence anyway). Vanilla a[]/o[]/s[]
/// selectors still work via base behaviour.
/// </summary>
internal class StratumPlayerUidsArgParser : PlayersArgParser
{
    private readonly ICoreServerAPI sapi;
    private PlayerUidName[] resolved;
    private bool resolvedAsFriendly;

    public StratumPlayerUidsArgParser(string argName, ICoreServerAPI api, bool isMandatoryArg)
        : base(argName, api, isMandatoryArg)
    {
        sapi = api;
    }

    public override string GetSyntaxExplanation(string indent)
    {
        return base.GetSyntaxExplanation(indent)
            + "\n" + indent + "  Stratum extensions: comma-separated names (p1,p2), @all, @staff, @admin, @mod, @&lt;rolecode&gt;.";
    }

    public override object GetValue()
    {
        return resolvedAsFriendly ? resolved : base.GetValue();
    }

    public override void PreProcess(TextCommandCallingArgs args)
    {
        resolved = null;
        resolvedAsFriendly = false;
        base.PreProcess(args);
    }

    public override EnumParseResult TryProcess(TextCommandCallingArgs args, Action<AsyncParseResults> onReady = null)
    {
        string peek = args.RawArgs.PeekWord();
        if (peek != null && StratumTargetSelector.LooksLikeFriendlyToken(peek))
        {
            args.RawArgs.PopWord();
            resolved = StratumTargetSelector.ResolveOnlinePlayerUids(peek, sapi);
            resolvedAsFriendly = true;
            if (resolved.Length == 0)
            {
                lastErrorMessage = "No matching online players for selector '" + peek + "'";
                return EnumParseResult.Bad;
            }
            return EnumParseResult.Good;
        }

        return base.TryProcess(args, onReady);
    }
}

/// <summary>
/// Re-registers a handful of vanilla commands with the Stratum friendly selectors layered on top.
/// Vanilla selector syntax (p[], e[], o[]) still works because the parsers delegate to base.
/// </summary>
internal static class StratumTargetCommandOverrides
{
    public static void Register(ServerMain server)
    {
        var api = server.api;
        var chat = api.commandapi;
        var parsers = chat.Parsers;
        var sapi = (ICoreServerAPI)api;

        // Remove vanilla registrations so .Create(name) won't throw on duplicate.
        var all = chat.AllSubcommands();
        all.Remove("tp");
        all.Remove("giveitem");
        all.Remove("giveblock");

        // /tp <source> <target>  — source supports friendly multi-target; target is a single position.
        chat.Create("tp")
            .RequiresPrivilege(Privilege.tp)
            .WithDesc("Teleport one or more players/entities to a location. Stratum supports friendly multi-target: /tp p1,p2 dest or /tp @admin dest.")
            .WithExamples(
                "<code>/tp Luke Hayden</code>",
                "<code>/tp luke,alice,bob hayden</code>",
                "<code>/tp @all spawn</code>",
                "<code>/tp @staff hayden</code>")
            .WithArgs(new StratumEntitiesArgParser("source", sapi, isMandatoryArg: true), parsers.WorldPosition("target"))
            .HandleWith((TextCommandCallingArgs args) =>
            {
                Vec3d target = args[1] as Vec3d;
                if (target == null) return TextCommandResult.Error("No matching target location found");
                return CmdUtil.EntityEach(args, e =>
                {
                    e.TeleportTo(target);
                    return TextCommandResult.Success("Ok, teleported " + e.GetName());
                });
            });

        // /giveitem <item> [quantity] [target] [attributes]
        chat.Create("giveitem")
            .RequiresPrivilege(Privilege.gamemode)
            .WithDescription("Give items to one or more targets. Stratum supports friendly multi-target.")
            .WithArgs(parsers.Item("item code"), parsers.OptionalInt("quantity", 1), new StratumOptionalEntitiesArgParser("target", sapi), parsers.OptionalAll("attributes"))
            .HandleWith((TextCommandCallingArgs args) =>
                args.Parsers[2].IsMissing
                    ? GiveItem(args.Caller.Entity, args)
                    : CmdUtil.EntityEach(args, e => GiveItem(e, args), 2));

        // /giveblock <block> [quantity] [target] [attributes]
        chat.Create("giveblock")
            .RequiresPrivilege(Privilege.gamemode)
            .WithDescription("Give blocks to one or more targets. Stratum supports friendly multi-target.")
            .WithArgs(parsers.Block("block code"), parsers.OptionalInt("quantity", 1), new StratumOptionalEntitiesArgParser("target", sapi), parsers.OptionalAll("attributes"))
            .HandleWith((TextCommandCallingArgs args) =>
                args.Parsers[2].IsMissing
                    ? GiveItem(args.Caller.Entity, args)
                    : CmdUtil.EntityEach(args, e => GiveItem(e, args), 2));
    }

    private static TextCommandResult GiveItem(Entity target, TextCommandCallingArgs args)
    {
        ItemStack stack = args[0] as ItemStack;
        int qty = stack.StackSize = (int)args[1];
        string attrs = (string)args.LastArg;
        if (attrs != null)
        {
            stack.Attributes.MergeTree(TreeAttribute.FromJson(attrs) as TreeAttribute);
        }
        if (target.TryGiveItemStack(stack.Clone()))
        {
            return TextCommandResult.Success("Ok, gave " + qty + "x " + stack.GetName());
        }
        return TextCommandResult.Error("Failed, target players inventory is likely full or cant accept this item for other reasons");
    }
}

/// <summary>
/// Optional variant of StratumEntitiesArgParser — when missing, defaults to caller's own entity (matches vanilla OptionalEntities behaviour).
/// </summary>
internal class StratumOptionalEntitiesArgParser : StratumEntitiesArgParser
{
    public StratumOptionalEntitiesArgParser(string argName, ICoreServerAPI api)
        : base(argName, api, isMandatoryArg: false)
    {
    }
}
