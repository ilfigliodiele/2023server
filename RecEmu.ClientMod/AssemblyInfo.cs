using MelonLoader;

// MelonLoader only loads a mod whose MelonGame matches the running game, and Rec Room has shipped
// under two developer strings over the years. If the game refuses to load this mod, that pair is
// the first thing to check: run the game once and read MelonLoader's log, which prints the name and
// developer it detected.
[assembly: MelonGame("Rec Room", "Rec Room")]
[assembly: MelonInfo(typeof(RecEmu.ClientMod.Mod), "RecEmu Redirect", "RecEmu", ClientConfig.ModVersion)]

// Priority below default so anything else in the Mods folder loads first; this mod only rewrites
// addresses and has no reason to run before the game's own initialisation.
[assembly: MelonPriority(-100)]