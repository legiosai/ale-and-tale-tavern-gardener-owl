# Gardener Owl

An Ale & Tale Tavern mod by **Legios**.

Buy a **Gardener Owl House** from the merchant’s **Furniture** tab for **1,800 coins** at tavern level **3**. Place the green house near the garden and put seeds in its inventory. The green owl harvests ripe crops, tills each harvested bed with a hoe, and plants one matching seed from the house.

It tends barley, carrots, potatoes, corn, and grapes. It keeps the full harvest in the house, including the game’s fertilizer bonus and crop rarity. A bed stays tilled until the house has a matching seed. Leave room for produce, and water or fertilize the seedlings with the normal tools. The quest Mandrake is handled by the player.

## Install and play

Download the ZIP from the [public releases](https://github.com/legiosai/gardener-owl/releases).

1. Use Steam build **26671410**, which displays game version **1.6.19**. The shipped loader target is **BepInEx 5.4.23.5 x64**. Install it in a mod manager profile or follow the upstream install guide. On Linux or Steam Deck, launch the profile with Proton and configure its `winhttp` DLL override. [BepInEx release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) · [Proton setup](https://docs.bepinex.dev/articles/advanced/proton_wine.html)
2. Merge the ZIP’s `BepInEx` folder into that profile’s game or mod directory. The DLL belongs at `BepInEx/plugins/GardenHarvesterOwl/GardenHarvesterOwl.dll`.
3. Load a world as host. Buy and place the Gardener Owl House. The purchased owl begins gardening automatically when its house is enabled. A stock Cleaner Owl House can also switch roles with **Left Alt + H** while you look at it. The shortcut is configurable in `BepInEx/config/dev.valen.gardenharvesterowl.cfg`.
4. Open the owl’s inventory to take produce and add seeds. Use the house’s normal enable or disable interaction to pause or resume. Free up inventory space if the tooltip says **Storage full**.

Check `BepInEx/LogOutput.log` for `Gardener Owl 0.2.0 ready`. The plugin checks the game assembly on launch and activates on the supported Steam build.

## Save files and co-op

The game saves the purchased house, produce, crop states, and house activation. The mod saves converted Cleaner Owl assignments and the plots waiting for seeds in `BepInEx/config/GardenHarvesterOwl/`. Keep that folder with the matching game save to restore those assignments and pending plantings.

The host buys the Gardener Owl and runs its gardening tasks. Guests use the normal owl inventory and share the garden state. Guests with the mod see the green appearance. Vanilla guests see the stock owl model.

To remove the mod, exit the game and remove its DLL. The furniture and crops load through the base game, and the owl operates as a cleaner.

## Build from source

Use a supported .NET SDK and point the build to your copy of the game and BepInEx. The project targets .NET Standard 2.1.

```sh
dotnet build src/GardenHarvesterOwl.csproj -c Release \
  -p:GameManagedDir='/path/to/Ale and Tale Tavern_Data/Managed' \
  -p:BepInExCoreDir='/path/to/BepInEx/core'
python3 tools/package.py src/bin/Release/netstandard2.1/GardenHarvesterOwl.dll dist/GardenerOwl-0.2.0.zip
```

The `tests/GameProbe` project is a test-only plugin for a copied world. Its launch conditions and cases are documented in [the test guide](tests/GameProbe/README.md).
