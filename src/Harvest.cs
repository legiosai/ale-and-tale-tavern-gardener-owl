using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GardenHarvesterOwl;

internal enum HarvestResult { Collected, Unavailable, NoRoom, Failed }

internal static class Harvest
{
    [ThreadStatic]
    private static (ushort produce, ushort container)? quietWrite;

    internal static bool TryGetProduce(GardenBed bed, out Item produce, out ushort stackLimit)
    {
        produce = default;
        stackLimit = 0;
        if (bed == null || !bed.IsSpawned || !bed.IsServer || bed.isInactive.Value
            || bed.state.Value != GardenBed.State.PlantGrown || bed.currentSeedId.Value == 0)
            return false;

        PlantData plant = GardenManager.Instance.GetPlantData(bed.currentSeedId.Value);
        if (plant == null || plant.grownPlantItem == null || plant.grownPlantItem.quest)
            return false;

        ItemData data = plant.grownPlantItem;
        produce = new Item(data)
        {
            amount = bed.isFertilized.Value
                ? (ushort)Mathf.CeilToInt(plant.takeAmount * 1.5f)
                : plant.takeAmount,
        };
        if (data.hasRarity && bed.rarity != ItemData.Rarity.Undefined)
            produce.rarity = bed.rarity;
        stackLimit = data.maxStack;
        return produce.amount > 0 && stackLimit > 0;
    }

    internal static bool Fits(ContainerNet house, Item produce, ushort stackLimit)
    {
        if (house == null || !house.IsSpawned || !house.IsServer || produce.amount > stackLimit)
            return false;

        Vector2Int size = house.size.Value;
        int freeSlots = size.x * size.y - house.items.Count;
        if (freeSlots > 0)
            return true;
        int room = 0;
        foreach (Item item in house.items)
        {
            if (Matches(item, produce))
                room += Math.Max(0, stackLimit - item.amount);
        }
        return room >= produce.amount;
    }

    internal static HarvestResult TryCollect(GardenBed bed, ContainerNet house, ManualLogSource log)
    {
        if (!TryGetProduce(bed, out Item produce, out ushort stackLimit))
            return HarvestResult.Unavailable;
        if (!Fits(house, produce, stackLimit))
            return HarvestResult.NoRoom;

        Dictionary<uint, Item> before = MatchingItems(house, produce);
        bool complete = false;
        quietWrite = (produce.dataId, house.id.Value);
        try
        {
            house.AddNewItem(produce, out ushort remainder);
            var after = MatchingItems(house, produce);
            if (remainder != 0 || Quantity(after) - Quantity(before) != produce.amount)
                throw new InvalidOperationException("The house did not receive the complete harvest.");

            bed.state.Value = GardenBed.State.Abandoned;
            complete = true;
        }
        catch (Exception error)
        {
            // A vanilla observer can throw after the NetworkVariable has accepted its value.
            complete = bed.state.Value == GardenBed.State.Abandoned;
            log.LogError($"Harvest write for crop {produce.dataId}: {error}");
            if (!complete)
            {
                try
                {
                    Restore(house, produce, before);
                }
                catch (Exception rollbackError)
                {
                    log.LogError($"Could not verify harvest rollback. Pause this house and reload the previous save. {rollbackError}");
                }
            }
        }
        finally
        {
            quietWrite = null;
        }

        if (!complete)
            return HarvestResult.Failed;

        Notify(() => Game.Instance.InvokeGameEvent(GameEvent.ItemAdded, produce.dataId, produce.amount), log);
        Notify(() => Game.Instance.InvokeGameEvent(GameEvent.TakePlant, produce.dataId, 1), log);
        Notify(() => NewItemsPanel.Instance?.ShowItemPopupClientRpc(produce.dataId, produce.amount, house.id.Value), log);
        return HarvestResult.Collected;
    }

    private static void Notify(Action publish, ManualLogSource log)
    {
        try { publish(); }
        catch (Exception error) { log.LogWarning($"A completed harvest could not send a game notification. {error}"); }
    }

    private static bool Matches(Item a, Item b) => a.dataId == b.dataId && a.rarity == b.rarity;

    private static Dictionary<uint, Item> MatchingItems(ContainerNet house, Item produce)
    {
        var snapshot = new Dictionary<uint, Item>();
        foreach (Item item in house.items)
            if (Matches(item, produce))
                snapshot.Add(item.id, item);
        return snapshot;
    }

    private static int Quantity(Dictionary<uint, Item> items) => items.Values.Sum(x => (int)x.amount);

    private static void Restore(ContainerNet house, Item produce, Dictionary<uint, Item> before)
    {
        var current = MatchingItems(house, produce);
        foreach (Item item in current.Values)
        {
            if (!before.TryGetValue(item.id, out Item original))
                house.RemoveItemById(item.id);
            else if (item.amount != original.amount)
                house.SetItemById(item.id, original);
        }

        var restored = MatchingItems(house, produce);
        if (restored.Count != before.Count || before.Any(x =>
                !restored.TryGetValue(x.Key, out Item item) || item.amount != x.Value.amount))
            throw new InvalidOperationException("The original crop stacks could not be restored.");
    }

    [HarmonyPatch(typeof(Game), nameof(Game.InvokeGameEvent))]
    internal static class GameEvents
    {
        private static bool Prefix(GameEvent e, uint obj) => quietWrite is not { } write
            || write.produce != obj || (e != GameEvent.ItemAdded && e != GameEvent.ItemRemoved);
    }

    [HarmonyPatch(typeof(NewItemsPanel), nameof(NewItemsPanel.ShowItemPopupClientRpc))]
    internal static class ItemPopup
    {
        private static bool Prefix(ushort itemId, int containerId) => quietWrite is not { } write
            || write.produce != itemId || write.container != containerId;
    }
}
