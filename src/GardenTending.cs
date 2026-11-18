using System;
using BepInEx.Logging;

namespace GardenHarvesterOwl;

internal enum PlantingResult { Planted, NoSeed, Unavailable, Failed }

internal static class GardenTending
{
    internal static bool IsEmpty(GardenBed bed) => bed != null && bed.IsSpawned && bed.currentSeedId.Value == 0
        && bed.state.Value is GardenBed.State.Abandoned or GardenBed.State.Plowed;

    internal static bool IsRegularSeed(ushort seedId) => seedId is >= 1201 and <= 1205;

    internal static bool HasSeed(ContainerNet storage, ushort seedId) => TryFindSeed(storage, seedId, out _);

    private static bool TryFindSeed(ContainerNet storage, ushort seedId, out Item seed)
    {
        foreach (Item item in storage.items)
        {
            if (item.dataId == seedId && item.id != 0 && item.amount > 0)
            {
                seed = item;
                return true;
            }
        }
        seed = default;
        return false;
    }

    internal static PlantingResult TrySow(GardenBed bed, ContainerNet storage, ushort seedId, ManualLogSource log)
    {
        if (!IsEmpty(bed) || !bed.IsServer || bed.isInactive.Value || bed.state.Value != GardenBed.State.Plowed
            || !IsRegularSeed(seedId) || storage == null || !storage.IsSpawned || !storage.IsServer)
            return PlantingResult.Unavailable;
        if (!TryFindSeed(storage, seedId, out Item seed))
            return PlantingResult.NoSeed;

        PlantData plant = GardenManager.Instance.GetPlantData(seedId);
        ItemData.Rarity originalRarity = bed.rarity;
        float originalTime = bed.remainingGrowTime;
        bool watered = bed.isWatered.Value;
        bool fertilized = bed.isFertilized.Value;
        byte strikes = bed.hoeHitCount.Value;
        try
        {
            if (!storage.RemoveItemAmount(seed.id, 1))
                return PlantingResult.NoSeed;
            // These are the native planting transitions, using the owl's stock of seeds.
            bed.rarity = seed.rarity;
            bed.currentSeedId.Value = seedId;
            bed.remainingGrowTime = plant.growTime;
            bed.state.Value = GardenBed.State.SeedPlanted;
        }
        catch (Exception error)
        {
            log.LogError($"Planting seed {seedId}: {error}");
            if (bed.state.Value != GardenBed.State.SeedPlanted || bed.currentSeedId.Value != seedId)
            {
                try
                {
                    RestoreSeed(storage, seed);
                    if (bed.currentSeedId.Value == seedId)
                    {
                        bed.state.Value = GardenBed.State.Abandoned;
                        bed.state.Value = GardenBed.State.Plowed;
                        bed.isWatered.Value = watered;
                        bed.isFertilized.Value = fertilized;
                        bed.hoeHitCount.Value = strikes;
                    }
                    bed.rarity = originalRarity;
                    bed.remainingGrowTime = originalTime;
                }
                catch (Exception rollbackError)
                {
                    log.LogError($"Could not restore the seed or soil after planting failed. {rollbackError}");
                }
                return PlantingResult.Failed;
            }
        }

        try
        {
            Game.Instance.InvokeGameEvent(GameEvent.PlantSeed, seedId, 1);
            SoundManager.Instance.PlayServerRpc(SoundEvent.GardenPlantSeed, bed.transform.position);
        }
        catch (Exception error)
        {
            log.LogWarning($"Could not send the planting notification for seed {seedId}. {error}");
        }
        return PlantingResult.Planted;
    }

    private static void RestoreSeed(ContainerNet storage, Item seed)
    {
        if (storage.GetItemById(seed.id, out Item current, genError: false))
        {
            if (current.amount == seed.amount - 1)
                storage.SetItemById(seed.id, seed);
        }
        else if (seed.amount == 1 && !storage.GetItemByPlace(seed.order, out _, genError: false))
        {
            ItemManager.Instance.RegisterItemToContainer(seed.id, storage.id.Value);
            storage.items.Add(seed);
        }
        if (!storage.GetItemById(seed.id, out current, genError: false) || !current.Equals(seed))
            throw new InvalidOperationException("The original seed stack could not be restored.");
    }
}
