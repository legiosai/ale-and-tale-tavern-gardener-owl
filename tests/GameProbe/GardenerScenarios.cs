using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GardenHarvesterOwl.GameProbe;

public sealed partial class ProbePlugin
{
    private const ushort GardenerShopId = 65456;
    private const ushort CornSeed = 1204;
    private const ushort CarrotSeed = 1202;

    private IEnumerable<object?> TestGardener()
    {
        object world = GetWorld();
        foreach (var frame in WaitFor(() => Houses().Length == 2 && GardenManager.Instance != null
            && PlayerInventory.Instance?.inventory != null, "the copied garden and player inventory")) yield return frame;

        GardenBed[] beds = GardenManager.Instance.gardenBeds;
        GardenBed corn = beds[0];
        GardenBed carrots = beds[1];
        FocusBeds(beds, corn);
        Ripen(corn, CornSeed);
        float inspectAt = Time.realtimeSinceStartup + 2.5f;
        foreach (var frame in WaitFor(() => Time.realtimeSinceStartup >= inspectAt, "previous Cleaner assignments to be ignored")) yield return frame;
        Require(Houses().All(house => !HasWorker(world, house)) && corn.state.Value == GardenBed.State.PlantGrown,
            "An older assignment switched a Cleaner Owl to garden work.");
        Logger.LogInfo("PROBE PASS legacy Cleaner assignments were ignored and both native cleaners stayed in their stock role");

        Furniture stock = Houses()[1];
        Vector3 placement = stock.transform.position;
        Quaternion rotation = stock.transform.rotation;
        ContainerNet inventory = PlayerInventory.Instance.inventory;
        Clear(inventory);
        stock.CollectServerRpc();
        foreach (var frame in WaitFor(() => Houses().Length == 1, "the test placement spot to free up")) yield return frame;

        var status = GameStatus.Instance;
        status.money.Value = 6000;
        status.level.Value = 2;
        Game.Instance.shopUnlock.Value = false;
        PlayerInventory.Instance.SetState(PlayerInventory.State.Shop);
        foreach (var frame in WaitFor(() => ShopUI.Instance != null && ShopUI.Instance.isActiveAndEnabled, "the merchant panel", 8)) yield return frame;
        ShopUI shop = ShopUI.Instance;
        shop.SetState(ShopUI.State.Furniture);
        ShopItemUI listing = FindListing(shop, GardenerShopId);
        ShopItemUI cleaner = FindListing(shop, 1156);
        Require(listing != null && cleaner != null && CardText(listing) == "Gardener Owl House", "Merchant did not show both named products.");
        Require((bool)AccessTools.Field(typeof(ShopItemUI), "_isLocked").GetValue(listing), "Level-two listing should be locked.");
        ItemManager.Instance.Buy(GardenerShopId);
        Require(status.money.Value == 6000 && PackedHouse(inventory).id == 0, "Merchant sold it below level three.");

        status.level.Value = 3;
        AccessTools.Method(typeof(ShopUI), "Refresh").Invoke(shop, null);
        listing = FindListing(shop, GardenerShopId);
        Require(!(bool)AccessTools.Field(typeof(ShopItemUI), "_isLocked").GetValue(listing), "Level-three listing did not unlock.");
        Require(ItemManager.Instance.GetItemData(GardenerShopId, out ItemData sku) && sku.GetItemPrice() == 1800
            && sku.icon != null && ItemManager.Instance.GetItemData(1156, out ItemData stockItem) && sku.icon != stockItem.icon,
            "The merchant had an incorrect price or icon.");
        AccessTools.Method(typeof(ShopItemUI), "OnBuy").Invoke(listing, null);
        foreach (var frame in WaitFor(() => PackedHouse(inventory).id != 0, "the merchant to put the marked house in inventory")) yield return frame;
        Require(status.money.Value == 4200 && inventory.GetItemAmount(GardenerShopId) == 0,
            "The purchase cost or the serialized item type is wrong.");
        PlayerInventory.Instance.SetState(PlayerInventory.State.Play);
        Logger.LogInfo("PROBE PASS merchant offers both houses, Gardener Owl House costs 1800 at level 3 and has a distinct green icon");

        Item package = PackedHouse(inventory);
        FurnitureManager.Instance.PlaceFurnitureServerRpc(package.id, placement, rotation);
        foreach (var frame in WaitFor(() => Houses().Any(IsPlacedGardener), "the purchased house to place through the base-game RPC")) yield return frame;
        Furniture house = Houses().Single(IsPlacedGardener);
        house.CollectServerRpc();
        foreach (var frame in WaitFor(() => PackedHouse(inventory).id != 0 && !Houses().Any(IsPlacedGardener), "the packed house to keep its variant")) yield return frame;
        FurnitureManager.Instance.PlaceFurnitureServerRpc(PackedHouse(inventory).id, placement, rotation);
        foreach (var frame in WaitFor(() => Houses().Any(IsPlacedGardener), "the packed house to be placed again")) yield return frame;
        house = Houses().Single(IsPlacedGardener);
        foreach (var frame in WaitFor(() => house.helperHouse.helper is HelperCleaner h && h.IsSpawned
            && Status(world, house).StartsWith("Gardener Owl", StringComparison.Ordinal), "the purchased owl to begin its role")) yield return frame;

        var owl = (HelperCleaner)house.helperHouse.helper;
        foreach (var frame in WaitFor(() => house.helperHouse.interactive.ObjectTitle == "Gardener Owl House"
            && owl.interactive.ObjectTitle == "Gardener Owl" && HasGreenMap(owl, "coat_low") && HasGreenMap(owl, "hood_low")
            && house.GetComponentsInChildren<Renderer>().Any(renderer => renderer.sharedMaterial?.name == "Owls_Nest1" && HasGreenMap(renderer)),
            "the green house, cloak and hood and their labels", 10)) yield return frame;
        Furniture cleanerHouse = Houses().Single(x => x != house);
        Require(!HasWorker(world, cleanerHouse) && !HasGreenMap((HelperCleaner)cleanerHouse.helperHouse.helper, "coat_low"),
            "The stock cleaner changed role or color when a gardener joined.");
        Logger.LogInfo("PROBE PASS bought house is green and auto-assigned; packing and replacing preserves it");

        foreach (GardenBed bed in beds)
            bed.state.Value = GardenBed.State.Abandoned;
        FocusBeds(beds, corn);
        Ripen(corn, CornSeed);
        ContainerNet storage = house.helperHouse.container;
        AddFixtureStack(storage, CornSeed, 1, ItemData.Rarity.Rare);
        Time.timeScale = 2f;
        foreach (var frame in WaitFor(() => corn.hoeHitCount.Value > 0 && owl.transform.Find("Gardener Owl Hoe") is Transform hoe
            && hoe.gameObject.activeSelf && hoe.GetComponentInChildren<MeshRenderer>() != null,
            () => $"the stock hoe and native strike ({Status(world, house)})", 35)) yield return frame;
        int expectedHits = (int)AccessTools.Field(typeof(GardenBed), "targetHoeHitCount").GetValue(corn);
        foreach (var frame in WaitFor(() => corn.state.Value == GardenBed.State.SeedPlanted, "the owl to replant the corn")) yield return frame;
        Require(corn.currentSeedId.Value == CornSeed && corn.rarity == ItemData.Rarity.Rare && corn.remainingGrowTime > 0
            && corn.hoeHitCount.Value == expectedHits && storage.GetItemAmount(CornSeed) == 0 && storage.GetItemAmount(Corn) == 2,
            "Harvesting, hoeing or using exactly one matching rare seed failed.");
        Logger.LogInfo("PROBE PASS visible stock hoe, native strike count, two corn harvested and one rare corn seed planted");

        FocusBeds(beds, carrots);
        Ripen(carrots, CarrotSeed);
        foreach (var frame in WaitFor(() => carrots.state.Value == GardenBed.State.Plowed
            && storage.GetItemAmount(Carrot) == 2, "the owl to till the carrot bed without carrot seeds", 35)) yield return frame;
        Require(carrots.currentSeedId.Value == 0 && storage.GetItemAmount(CarrotSeed) == 0, "The owl planted without a matching seed.");
        SaveManager.Instance.SaveGame();
        string plots = Directory.GetFiles(Path.Combine(Paths.ConfigPath, "GardenHarvesterOwl"), "*.json").Select(File.ReadAllText).Single();
        Require(plots.Contains("\"seedId\": 1202") && !plots.Contains("\"houses\""), "The save should retain the pending plot without Cleaner assignments.");
        Logger.LogInfo("PROBE PASS without seed the bed stays plowed, harvest stays in storage, and the pending crop is saved");
    }

    private IEnumerable<object?> TestGardenerReload()
    {
        object world = GetWorld();
        foreach (var frame in WaitFor(() => Houses().Any(IsPlacedGardener) && GardenManager.Instance != null,
            "the purchased house to load from the native save")) yield return frame;
        Furniture house = Houses().Single(IsPlacedGardener);
        GardenBed bed = GardenManager.Instance.gardenBeds[1];
        ContainerNet storage = house.helperHouse.container;
        Require(bed.state.Value == GardenBed.State.Plowed && storage.GetItemAmount(Corn) == 2 && storage.GetItemAmount(Carrot) == 2,
            "Native reload lost the tilled plot or the stored harvest.");
        FocusBeds(GardenManager.Instance.gardenBeds, bed);
        AddFixtureStack(storage, CarrotSeed, 2, ItemData.Rarity.Common);
        Time.timeScale = 2f;
        foreach (var frame in WaitFor(() => bed.state.Value == GardenBed.State.SeedPlanted,
            () => $"the pending carrot seed to plant after restocking ({Status(world, house)})", 40)) yield return frame;
        Require(bed.currentSeedId.Value == CarrotSeed && storage.GetItemAmount(CarrotSeed) == 1 && storage.GetItemAmount(Carrot) == 2,
            "Restocking a previously tilled bed used the wrong seed count or changed produce.");
        Require(Houses().Where(member => member != house).All(member => !HasWorker(world, member)),
            "An older assignment was restored alongside pending work.");
        SaveManager.Instance.SaveGame();
        Logger.LogInfo("PROBE PASS new launch restored the purchased house and replanted the waiting plot after restocking");
    }

    private IEnumerable<object?> TestGardenerUninstall()
    {
        Require(!Chainloader.PluginInfos.ContainsKey(ModId), "Expected a base-game launch after removing the DLL.");
        foreach (var frame in WaitFor(() => Houses().Any(IsPlacedGardener) && GardenManager.Instance != null,
            "the base game to load the purchased house")) yield return frame;
        Furniture house = Houses().Single(IsPlacedGardener);
        Require(house.helperHouse.helper is HelperCleaner && house.helperHouse.container.GetItemAmount(Carrot) == 2
            && GardenManager.Instance.gardenBeds[1].currentSeedId.Value == CarrotSeed,
            "Removing the mod lost a native house, crop, or stored produce.");
        Logger.LogInfo("PROBE PASS mod removal preserved bought furniture, planted crops and harvest");
    }

    private static Item PackedHouse(ContainerNet inventory)
    {
        foreach (Item item in inventory.items)
        {
            if (item.dataId == 1156 && item.durability != 0)
                return item;
        }
        return default;
    }
    private static bool IsPlacedGardener(Furniture house) => house.itemData.id == 1156 && house.reliability.Value != 0;

    private void StageLegacyAssignments()
    {
        string save = Path.Combine(Application.dataPath, "saves", SaveSlot + ".sav");
        byte[] data = File.ReadAllBytes(save);
        var source = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(data))));
        var houses = new JArray(source["devices"]!.Children<JObject>()
            .Where(device => device.Value<int>("itemDataId") == 1156 && device.Value<int>("reliability") == 0)
            .Select(device => new JObject { ["furnitureId"] = device.Value<int>("id"), ["containerId"] = device["container"]!.Value<int>("id") }));
        Require(houses.Count > 0, "The upgrade fixture must include a stock Cleaner Owl House.");

        using var sha = SHA256.Create();
        string Hash(byte[] bytes) => BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        string directory = Path.Combine(Paths.ConfigPath, "GardenHarvesterOwl");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Hash(Encoding.UTF8.GetBytes(SaveSlot.ToUpperInvariant())) + ".json");
        var snapshot = mode == "gardener-reload" ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        snapshot["version"] = 1;
        snapshot["saveFingerprint"] = Hash(data);
        snapshot["houses"] = houses;
        File.WriteAllText(path, snapshot.ToString());
    }

    private static ShopItemUI FindListing(ShopUI shop, ushort id) => shop.GetComponentsInChildren<ShopItemUI>()
        .Single(row => ((ItemData)AccessTools.Field(typeof(ShopItemUI), "_itemData").GetValue(row))?.id == id);

    private static string CardText(ShopItemUI row)
    {
        object title = AccessTools.Field(typeof(ShopItemUI), "title").GetValue(row);
        return (string)AccessTools.Property(title.GetType(), "text").GetValue(title);
    }

    private static bool HasGreenMap(HelperCleaner owl, string mesh) => owl.GetComponentsInChildren<Renderer>()
        .Where(renderer => renderer.name == mesh).Any(HasGreenMap);

    private static bool HasGreenMap(Renderer renderer)
    {
        var paint = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(paint);
        return new[] { "_BaseMap", "_BaseColorMap", "_MainTex" }.Any(map => paint.GetTexture(map) != null);
    }
}
