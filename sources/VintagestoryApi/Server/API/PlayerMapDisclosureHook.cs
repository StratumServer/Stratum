using Vintagestory.API.Common;

namespace Vintagestory.API.Server
{
    /// <summary>
    /// Server-side hook used by the player map-pin broadcaster (PlayerMapLayer / SystemRemotePlayerTracking)
    /// to ask whether one player's position should be disclosed to another, and at what fidelity.
    ///
    /// Defaults are null (no override) — the broadcaster falls back to the world config flags
    /// (<c>mapHideOtherPlayers</c>, <c>mapPlayerRenderDistance</c>, <c>mapShowGroupPlayers</c>).
    ///
    /// Intended for server-side mods / forks that want finer control than the worldconfig knobs,
    /// e.g. staff overrides, faction/group rules, or coordinate-precision degradation.
    /// </summary>
    public static class PlayerMapDisclosureHook
    {
        /// <summary>
        /// Decide whether <paramref name="sender"/>'s map pin should be sent to <paramref name="receiver"/>
        /// this tick. Return true to send, false to send a despawn (hide). Return null to defer to the
        /// broadcaster's default logic.
        /// </summary>
        public delegate bool? AllowMapPinDisclosureDelegate(IServerPlayer sender, IServerPlayer receiver);

        /// <summary>
        /// Return the coordinate grid size (in blocks) to snap <paramref name="sender"/>'s position to
        /// before sending to <paramref name="receiver"/>. 0 or less = exact (no snap). Return null to
        /// defer to the broadcaster's default (exact).
        /// </summary>
        public delegate int? CoordinateSnapDelegate(IServerPlayer sender, IServerPlayer receiver);

        public static AllowMapPinDisclosureDelegate AllowMapPinDisclosure;
        public static CoordinateSnapDelegate CoordinateSnap;
    }
}
