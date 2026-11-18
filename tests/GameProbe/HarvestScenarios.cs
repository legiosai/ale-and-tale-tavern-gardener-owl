using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace GardenHarvesterOwl.GameProbe;

public sealed partial class ProbePlugin
{
    private static ushort partialTarget;

    private IEnumerable<object?> TestHarvestEdges(object world, Furniture house, HelperCleaner owl, GardenBed[] beds, GardenBed farthest)
    {
        ContainerNet storage = house.helperHouse.container;
        Vector2Int originalSize = storage.size.Value;
        house.helperHouse.isHelperActive.Value = false;
        FocusBeds(beds, farthest);
        Ripen(farthest, 1202, ItemData.Rarity.Rare, fertilized: true);
        Clear(storage);
        storage.size.Value = Vector2Int.one;
        AddFixtureStack(storage, Carrot, 7, ItemData.Rarity.Common);
        Require(CollectForTest(farthest, storage) == "NoRoom" && !IsAbandoned(farthest), "Wrong rarity stack accepted.");
        Clear(storage);
        AddFixtureStack(storage, Carrot, 8, ItemData.Rarity.Rare);
        Require(CollectForTest(farthest, storage) == "NoRoom" && Count(storage, Carrot, ItemData.Rarity.Rare) == 8,
            "A one-item shortfall changed the bed or stack.");
        Clear(storage);
        AddFixtureStack(storage, Carrot, 7, ItemData.Rarity.Rare);
        ProveRollback(farthest, storage);
        house.helperHouse.isHelperActive.Value = true;
        foreach (var frame in WaitFor(() => Count(storage, Carrot, ItemData.Rarity.Rare) == 10,
            () => $"exact-fit full inventory ({Status(world, house)})", 38)) yield return frame;
        foreach (var frame in WaitFor(() => Perched(owl), "owl to return after exact-fit harvest")) yield return frame;
        Logger.LogInfo("PROBE PASS exact-fit full slot, wrong rarity, one-item shortfall, forced partial rollback");

        Clear(storage);
        Ripen(farthest, 1202);
        foreach (var frame in WaitFor(() => AwayFromHome(owl), "owl to start for a bed with an empty house")) yield return frame;
        AddFixtureStack(storage, Corn, 10, ItemData.Rarity.Common);
        foreach (var frame in WaitFor(() => Perched(owl) && Status(world, house).Contains("Storage full"), "owl to return after storage filled mid-trip", 43)) yield return frame;
        Require(!IsAbandoned(farthest) && storage.GetItemAmount(Carrot) == 0, "A late capacity change removed a crop.");
        Logger.LogInfo("PROBE PASS storage filled during walk leaves the ripe crop intact");

        Clear(storage);
        foreach (var frame in WaitFor(() => AwayFromHome(owl), "owl to resume after storage frees")) yield return frame;
        uint manualBefore = PlayerInventory.Instance.inventory.GetItemAmount(Carrot);
        farthest.TakeGrownPlantServerRpc();
        foreach (var frame in WaitFor(() => IsAbandoned(farthest) && Perched(owl), "owl to cancel after a player harvest", 36)) yield return frame;
        Require(storage.GetItemAmount(Carrot) == 0 && PlayerInventory.Instance.inventory.GetItemAmount(Carrot) == manualBefore + 2,
            "Manual harvest produced a duplicate or the player did not receive the crop.");
        Logger.LogInfo("PROBE PASS manual harvest cancels a reserved job");

        Ripen(farthest, 1202);
        foreach (var frame in WaitFor(() => !owl.agent.enabled && Vector3.Distance(owl.transform.position, owl.defaultPos) > 0.35f
            && Status(world, house).Contains("Walking"), "owl to be mid-hop before pause")) yield return frame;
        house.helperHouse.isHelperActive.Value = false;
        foreach (var frame in WaitFor(() => Perched(owl) && Status(world, house).Contains("Paused"), "paused owl to return")) yield return frame;
        Require(!IsAbandoned(farthest) && storage.GetItemAmount(Carrot) == 0, "Pausing did not cancel the crop job.");
        house.helperHouse.isHelperActive.Value = true;
        foreach (var frame in WaitFor(() => AwayFromHome(owl), "owl to resume after pause")) yield return frame;
        owl.SetAgentProperty(HelperBase.AgentStopProperty.CanMoveByInventoryOpen, false);
        foreach (var frame in WaitFor(() => Status(world, house).Contains("Inventory open"), "inventory to suspend work")) yield return frame;
        Require(!IsAbandoned(farthest), "An inventory interaction did not suspend the crop job.");
        owl.SetAgentProperty(HelperBase.AgentStopProperty.CanMoveByInventoryOpen, true);
        foreach (var frame in WaitFor(() => Navigation.AgentReady(owl.agent) && owl.agent.hasPath
            && Status(world, house).Contains("Walking"), "owl to regain a path")) yield return frame;
        owl.agent.isStopped = true;
        foreach (var frame in WaitFor(() => Status(world, house).Contains("Path blocked"), "stalled path to release")) yield return frame;
        foreach (var frame in WaitFor(() => IsCleared(farthest) && storage.GetItemAmount(Carrot) == 2, "a successful retry after the stall", 46)) yield return frame;
        foreach (var frame in WaitFor(() => Perched(owl), "owl to return after retry")) yield return frame;
        Logger.LogInfo("PROBE PASS pause/resume, inventory suspension, and stalled route retry");

        Clear(storage);
        Ripen(farthest, 65053);
        foreach (var frame in WaitFor(() => Perched(owl), "the quest crop to be skipped")) yield return frame;
        Require(!IsAbandoned(farthest) && storage.items.Count == 0 && CollectForTest(farthest, storage) == "Unavailable",
            "The owl changed a quest crop.");
        farthest.state.Value = GardenBed.State.Abandoned;
        storage.size.Value = originalSize;
        Logger.LogInfo("PROBE PASS quest crop left for manual harvest");
    }

    private IEnumerable<object?> TestEntireGarden(object world, Furniture primary, GardenBed[] beds)
    {
        Furniture[] houses = Houses();
        Require(houses.Length == 2, "Stage a second stock cleaner house in the copied save for the reservation check.");
        Furniture other = houses.Single(x => x != primary);
        Require(Status(world, other) == "Cleaner", "The second house did not begin as Cleaner.");
        if (FurnitureManager.Instance.furniture.Values.Any(f => f.itemData.id == 1155))
            Require(FurnitureManager.Instance.furniture.Values.First(f => f.itemData.id == 1155).helperHouse.helper is HelperWaiter,
                "The stock waiter changed.");
        foreach (Furniture house in houses)
        {
            Clear(house.helperHouse.container);
            house.helperHouse.isHelperActive.Value = true;
        }
        FocusBeds(beds, beds);
        for (int i = 0; i < beds.Length; i++)
            Ripen(beds[i], i < 15 ? (ushort)1204 : (ushort)1202);
        int events = 0;
        Game.Instance.onGameEvent += (e, item, _) => { if (e == GameEvent.TakePlant && (item == Corn || item == Carrot)) events++; };
        Assign(world, other, true);
        foreach (var frame in WaitFor(() => beds.All(IsCleared), () => $"all 30 beds ({events} harvests, {Status(world, primary)}, {Status(world, other)})", 90)) yield return frame;
        Require(events == 30 && Total(houses, Corn) == 30 && Total(houses, Carrot) == 30,
            "Whole-garden yield differs from 30 carrots and 30 corn across 30 distinct harvests.");
        Require(houses.All(x => x.helperHouse.container.GetItemAmount(Corn) + x.helperHouse.container.GetItemAmount(Carrot) > 0),
            "Both workers must complete at least one crop.");
        Assign(world, other, false);
        foreach (var frame in WaitFor(() => Status(world, other) == "Cleaner", "second owl to return to its stock role", 35)) yield return frame;
        Logger.LogInfo("PROBE PASS two owls harvest all 30 beds once, 30 carrots and 30 corn, second owl switched back to Cleaner");
    }

    private static bool IsAbandoned(GardenBed bed) => bed.state.Value == GardenBed.State.Abandoned;
    private static bool IsCleared(GardenBed bed) => bed.currentSeedId.Value == 0
        && bed.state.Value is GardenBed.State.Abandoned or GardenBed.State.Plowed;
    private static bool AwayFromHome(HelperCleaner owl) => Navigation.AgentReady(owl.agent)
        && Vector3.Distance(owl.transform.position, owl.defaultPos) > 3f;
    private static uint Total(Furniture[] houses, ushort item) => (uint)houses.Sum(x => (long)x.helperHouse.container.GetItemAmount(item));

    private static void AddFixtureStack(ContainerNet storage, ushort id, ushort amount, ItemData.Rarity rarity)
    {
        Require(ItemManager.Instance.GetItemData(id, out ItemData data), "Test crop not found.");
        Require(storage.AddNewItem(new Item(data) { amount = amount, rarity = rarity }, out ushort remainder) && remainder == 0,
            "Could not add the fixture stack.");
    }

    private string CollectForTest(GardenBed bed, ContainerNet storage)
    {
        var assembly = Chainloader.PluginInfos[ModId].Instance.GetType().Assembly;
        var harvest = AccessTools.TypeByName("GardenHarvesterOwl.Harvest");
        Require(harvest.Assembly == assembly, "Unexpected harvest implementation.");
        return AccessTools.Method(harvest, "TryCollect").Invoke(null, new object[] { bed, storage, Logger })!.ToString();
    }

    private void ProveRollback(GardenBed bed, ContainerNet storage)
    {
        var add = AccessTools.Method(typeof(ContainerNet), nameof(ContainerNet.AddNewItem),
            new[] { typeof(Item), typeof(ushort).MakeByRefType(), typeof(bool) });
        var harmony = new Harmony("dev.valen.gardenharvesterowl.gameprobe.partial");
        int notifications = 0;
        Action<GameEvent, uint, int> observe = (e, id, _) =>
        {
            if (id == Carrot && e is GameEvent.ItemAdded or GameEvent.ItemRemoved or GameEvent.TakePlant)
                notifications++;
        };
        partialTarget = storage.id.Value;
        Game.Instance.onGameEvent += observe;
        try
        {
            harmony.Patch(add, prefix: new HarmonyMethod(typeof(ProbePlugin), nameof(PartialInsert)),
                postfix: new HarmonyMethod(typeof(ProbePlugin), nameof(PartialRemainder)));
            Require(CollectForTest(bed, storage) == "Failed", "Injected partial insertion was accepted.");
            Require(!IsAbandoned(bed) && Count(storage, Carrot, ItemData.Rarity.Rare) == 7 && notifications == 0,
                "Partial transfer changed the crop, stored quantity, or game event count.");
        }
        finally
        {
            harmony.UnpatchSelf();
            partialTarget = 0;
            Game.Instance.onGameEvent -= observe;
        }
    }

    private static void PartialInsert(ContainerNet __instance, ref Item item, out ushort __state)
    {
        __state = 0;
        if (__instance.id.Value == partialTarget && item.dataId == Carrot && item.amount == 3)
        {
            item.amount = 1;
            __state = 2;
        }
    }

    private static void PartialRemainder(ref ushort leftAmount, ushort __state) => leftAmount += __state;

    private void TestMismatchedSaveFingerprint()
    {
        string saves = Path.Combine(Application.dataPath, "saves");
        string backup = Directory.GetFiles(saves, SaveSlot + "_*.bk").OrderBy(File.GetLastWriteTimeUtc).First();
        string backupName = Path.GetFileNameWithoutExtension(backup);
        string date = backupName.Substring(SaveSlot.Length + 1);
        var type = AccessTools.TypeByName("GardenHarvesterOwl.RoleStore");
        object store = Activator.CreateInstance(type,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
            new object[] { Path.Combine(Paths.ConfigPath, "GardenHarvesterOwl"), Logger }, null)!;
        object?[] arguments = { SaveSlot, date, true, null };
        var restored = (IEnumerable)AccessTools.Method(type, "Load").Invoke(store, arguments);
        Require(!restored.GetEnumerator().MoveNext() && arguments[3] is Array { Length: 0 },
            "An older save restored an assignment or pending plot from the newer world.");
        Logger.LogInfo("PROBE PASS older save fingerprint ignored");
    }
}
