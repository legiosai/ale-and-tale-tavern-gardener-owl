# In-game test driver

Build `tests/GameProbe/GardenHarvesterOwl.GameProbe.csproj` with the same `GameManagedDir` and `BepInExCoreDir` properties as the mod. Install its DLL only in disposable test profiles. The driver requires a `.harvester-test-copy` marker at the copied game root and a `GHO_TEST_MODE` value. It selects the game’s built-in local transport and binds to loopback port 43826 for solo tests or 43827 for co-op.

Use an independent Proton prefix and a copy of `Ale and Tale Tavern_Data/saves/harvester-probe.sav`. The garden fixture expects the 30-bed map and a Cleaner Owl House. The complete host suite additionally expects a second stock Cleaner Owl House with a distinct furniture ID and container ID. Host tests grow and reset plants, fill inventory, and save this copy.

| Mode | Setup | Checks |
| --- | --- | --- |
| `routes` | Test driver | Compute paths from the normal nest to all 30 beds and back against the live navigation mesh. |
| `host` | Test driver and production mod | First and last bed, correct yield and rarity, exact-fit and blocked storage, a forced partial write, player race, pause, stalled path, quest exclusion, and two owls harvesting 30 distinct beds. Saves the result. |
| `reload` | Same tested save and mod profile | Role, crop state and inventory restore, and an older save fingerprint does not adopt the newer assignment. |
| `uninstall` | Same tested save with the harvester DLL removed | Stock house, owl and collected produce load. |
| `gardener` | Test driver and production mod in a copied world with two stock Cleaner Owl Houses | Loads legacy Cleaner assignments and checks that both keep cleaning. Tests the merchant purchase, place and pack, green models, hoe strikes and seed use. |
| `gardener-reload` | The copied save from `gardener` with the mod installed | Restores the bought house and plants the pending plot after restocking. Verifies that stock cleaners keep their role alongside pending work. |
| `gardener-uninstall` | The saved world from `gardener-reload` with the mod DLL removed | The game loads the house, planted crops and stored harvest. |
| `coop` and `guest` | Separate host and guest game folders under one parent. Put the mod in the host profile only, with the test driver in each. Start the guest first and wait for `coop-guest-menu-ready` before launching the host. | The guest joins, sees the stock owl move and the harvest replicate, collects two corn, disconnects, and rejoins. |

Each mode writes `PROBE PASS` or `PROBE FAIL` to BepInEx’s `LogOutput.log`. The co-op modes share files named `coop-*` in the parent directory, so use a fresh parent or clear prior marker files before repeating. The production ZIP contains the harvester DLL, installation instructions, and package metadata.
