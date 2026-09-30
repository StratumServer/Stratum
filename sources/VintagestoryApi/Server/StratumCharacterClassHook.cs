using System;
using System.Collections.Generic;

namespace Vintagestory.API.Server;

// Bridge for #278 class change requests. CharacterSystem (in VSSurvivalMod) owns the class list
// and the code that swaps a player's class and trait stats, and VintagestoryLib cannot reference
// it. CharacterSystem.StartServerSide fills both delegates; they stay null when the survival mod
// is not loaded, and /class then reports that class changes are unavailable.
public static class StratumCharacterClassHook
{
	// Codes of the enabled character classes, in the order the character dialog lists them.
	public static Func<IReadOnlyList<string>> ListClasses;

	// Switches an online player to the given class code without handing out that class's
	// starting gear, the same way a vanilla reselection outside creative mode does.
	public static Action<IServerPlayer, string> ApplyClass;
}
