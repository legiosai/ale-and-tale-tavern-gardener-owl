using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace GardenHarvesterOwl.GameProbe;

public sealed partial class ProbePlugin
{
    private const string ModId = "dev.valen.gardenharvesterowl";
    private const ushort Corn = 17;
    private const ushort Carrot = 15;

    private IEnumerable<object?> TestHost()
    {
        foreach (var frame in WaitFor(() => Houses().Length > 0 && GardenManager.Instance != null, "furniture and garden to spawn"))
            yield return frame;
        object world = GetWorld();
        Furniture house = Houses()[0];
        ContainerNet storage = house.helperHouse.container;
        HelperCleaner owl = (HelperCleaner)house.helperHouse.helper;
        GardenBed[] beds = GardenManager.Instance.gardenBeds;
        Require(beds.Length == 30, "Expected the copied 30-bed garden.");
        VerifyRegularVarieties(storage, beds);
        GardenBed nearest = beds[0];
        GardenBed farthest = beds[29];
        FocusBeds(beds, nearest);
        Ripen(nearest, 1204);
        uint playerCorn = PlayerInventory.Instance.inventory.GetItemAmount(Corn);
        int cornHarvests = 0;
        bool besideAtHarvest = false;
        Game.Instance.onGameEvent += (e, item, _) =>
        {
            if (e != GameEvent.TakePlant || item != Corn)
                return;
            cornHarvests++;
            besideAtHarvest = Navigation.IsBeside(owl, owl.transform.position, nearest.transform.position);
        };

        Time.timeScale = 2.5f;
        MarkAsGardener(house);
        foreach (var frame in WaitFor(() => storage.GetItemAmount(Corn) == 2, () => $"corn harvest ({Status(world, house)})", 38)) yield return frame;
        Require(nearest.state.Value == GardenBed.State.Abandoned && nearest.currentSeedId.Value == 0, "Corn bed did not reset.");
        Require(cornHarvests == 1 && besideAtHarvest && PlayerInventory.Instance.inventory.GetItemAmount(Corn) == playerCorn,
            "Corn harvest did not match the nearby one-bed transaction.");
        foreach (var frame in WaitFor(() => Perched(owl), "owl to return after nearest bed")) yield return frame;
        Logger.LogInfo("PROBE PASS nearest bed walk, one harvest, player inventory, return to nest");

        FocusBeds(beds, farthest);
        Ripen(farthest, 1202, ItemData.Rarity.Rare, fertilized: true);
        foreach (var frame in WaitFor(() => Count(storage, Carrot, ItemData.Rarity.Rare) == 3, () => $"far carrot harvest ({Status(world, house)})", 38)) yield return frame;
        Require(!farthest.isFertilized.Value && farthest.state.Value == GardenBed.State.Abandoned, "Carrot bed state did not reset.");
        foreach (var frame in WaitFor(() => Perched(owl), "owl to return after farthest bed")) yield return frame;
        Logger.LogInfo("PROBE PASS farthest bed walk, rare fertilized yield, return to nest");

        foreach (var frame in TestHarvestEdges(world, house, owl, beds, farthest)) yield return frame;
        foreach (var frame in TestEntireGarden(world, house, beds)) yield return frame;

        SaveManager.Instance.SaveGame();
        string[] plotFiles = Directory.GetFiles(Path.Combine(Paths.ConfigPath, "GardenHarvesterOwl"), "*.json");
        Require(plotFiles.Length == 1 && File.ReadAllText(plotFiles[0]).Contains("\"plots\""),
            "The plot file must contain the pending garden work.");
        Logger.LogInfo("PROBE PASS native save and pending plot file");
    }

    private IEnumerable<object?> TestReload()
    {
        object world = GetWorld();
        foreach (var frame in WaitFor(() => Houses().Length == 2 && Houses().All(house => HasWorker(world, house)), "saved garden houses to resume their role"))
            yield return frame;
        Furniture[] houses = Houses();
        Require(Total(houses, Corn) == 30 && Total(houses, Carrot) == 30 && GardenManager.Instance.gardenBeds.All(IsCleared),
            "Reloaded storage changed.");
        TestMismatchedSaveFingerprint();
        SaveManager.Instance.SaveGame();
        Logger.LogInfo("PROBE PASS purchased houses and crop quantities across reload");
    }

    private IEnumerable<object?> TestUninstall()
    {
        Require(!Chainloader.PluginInfos.ContainsKey(ModId), "Uninstall test still has the harvester DLL.");
        foreach (var frame in WaitFor(() => Houses().Length == 2, "vanilla houses to load")) yield return frame;
        Furniture[] houses = Houses();
        Require(houses.All(x => x.helperHouse.helper is HelperCleaner) && Total(houses, Corn) == 30 && Total(houses, Carrot) == 30,
            "Stock cleaner or stored produce failed to load without the mod.");
        Logger.LogInfo("PROBE PASS base-game house and produce after removal");
    }

    private static Furniture[] Houses() => FurnitureManager.Instance == null ? Array.Empty<Furniture>()
        : FurnitureManager.Instance.furniture.Values.Where(x => x != null && x.itemData.id == 1156
            && x.helperHouse != null && x.helperHouse.container != null && x.helperHouse.container.IsSpawned)
            .OrderBy(x => x.id.Value).ToArray();

    private static object GetWorld()
    {
        if (!Chainloader.PluginInfos.TryGetValue(ModId, out var info) || info.Instance == null)
            throw new InvalidOperationException("Expected the production DLL in this test mode.");
        return AccessTools.Property(info.Instance.GetType(), "World").GetValue(null)
            ?? throw new InvalidOperationException("The production plugin did not initialize.");
    }

    private static void MarkAsGardener(Furniture house)
    {
        var item = AccessTools.TypeByName("GardenHarvesterOwl.GardenerItem");
        house.reliability.Value = (ushort)AccessTools.Field(item, "Marker").GetRawConstantValue();
    }

    private static bool HasWorker(object world, Furniture house) => house.helperHouse.helper is HelperCleaner owl
        && AccessTools.Method(world.GetType(), "WorkerFor").Invoke(null, new object[] { owl }) != null;

    private static string Status(object world, Furniture house) =>
        (string)AccessTools.Method(world.GetType(), "StatusOf").Invoke(world, new object[] { house });

    private static bool Perched(HelperCleaner owl) => !owl.agent.enabled && Vector3.Distance(owl.transform.position, owl.defaultPos) < 0.3f;

    private static int Count(ContainerNet storage, ushort crop, ItemData.Rarity rarity)
    {
        int amount = 0;
        foreach (Item item in storage.items)
            if (item.dataId == crop && item.rarity == rarity)
                amount += item.amount;
        return amount;
    }

    private static void Clear(ContainerNet storage)
    {
        for (int i = storage.items.Count - 1; i >= 0; i--)
            storage.RemoveItemById(storage.items[i].id);
    }

    private static void FocusBeds(GardenBed[] beds, params GardenBed[] active)
    {
        foreach (GardenBed bed in beds)
            bed.isInactive.Value = !active.Contains(bed);
    }

    private static void Ripen(GardenBed bed, ushort seed, ItemData.Rarity rarity = ItemData.Rarity.Common, bool fertilized = false)
    {
        bed.currentSeedId.Value = seed;
        bed.rarity = rarity;
        bed.isFertilized.Value = fertilized;
        bed.state.Value = GardenBed.State.PlantGrown;
    }

    private static IEnumerable<object?> WaitFor(Func<bool> ready, string why, float seconds = 25f) => WaitFor(ready, () => why, seconds);

    private static IEnumerable<object?> WaitFor(Func<bool> ready, Func<string> why, float seconds = 25f)
    {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (!ready())
        {
            if (Time.realtimeSinceStartup > deadline)
                throw new TimeoutException("Waiting for " + why());
            yield return null;
        }
    }
}
