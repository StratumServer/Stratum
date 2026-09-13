# StratumScenarios

Nineteen end-to-end scenarios that boot a real server in-process (through the
[Atlas](https://github.com/Pixnop/Atlas) test harness) and assert documented fork
behavior: chunk persistence across save/unload/reload, the simulation distance throttles
for entities, random ticks and block tick listeners, and the fact that each
`stratum-performance.json` toggle really restores vanilla behavior. `BootScenarios` also
checks that the fork's own command groups are registered, and guards against the worst
false green available here, a build made without `-p:EmbedPatchedFiles=true` that boots
the downloaded vanilla lib instead of this repo's.

Run it with `make scenarios`, `bash scripts/scenarios.sh` or `.\scripts\scenarios.ps1`.
`make scenarios` always builds first. The scripts alone build only if the launcher is
missing, so run `make build` first after a change or they test the previous build. Then
they materialize the install with one `--stratum-prepare-only` launch and run
`dotnet test` with `VINTAGE_STORY` pointing at it, in the configuration named by
`CONFIGURATION` (Release by default). Extra arguments go to `dotnet test`, so
`bash scripts/scenarios.sh --filter BootScenarios` runs a single class. The test phase
alone takes between a minute and a half and two and a half minutes depending on the
machine, the nine server boots included, on top of the builds and the prepare launch.
It pulls four NuGet packages: xunit, its Visual Studio runner, Microsoft.NET.Test.Sdk, and
`Pixnop.Atlas.XUnit`, which must be 0.13.1 or newer: older Atlas releases open the
synthetic join with the identification packet, which Stratum's first-packet gate drops,
so every scenario that joins a player times out.

The project is outside `VintageStory.slnx` on purpose: it needs a prepared install, and
a normal build must not depend on one. Nothing runs it automatically. Unlike the rest of
the repo it enables Nullable and ImplicitUsings: it is self-contained and compiles nothing
from the decompiled tree, so the choice stays local to it.

Scenario bodies run on the server's game thread: Atlas posts them to its
GameThreadScheduler, which the server pump drains between two ticks. The world mutations
they make (SetBlock, SetModdata and MarkModified, RegisterGameTickListener, adding an
entity behavior) therefore never race the tick loop. They can still race work the engine
runs off-thread, such as the server assets packet build at boot, which is why the random
tick scenarios join a player before their mass SetBlock.

The vanilla side of these measurements lives in
[StratumParity](https://github.com/Pixnop/StratumParity), which runs the same probes
against both flavors and diffs them. This copy answers a narrower question: did this
change break behavior the fork documents.
