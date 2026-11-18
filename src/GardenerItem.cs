using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace GardenHarvesterOwl;

internal static class GardenerItem
{
    internal const string OwlName = "Gardener Owl";
    internal const string HouseName = "Gardener Owl House";
    internal const ushort ShopId = 65456;
    private const ushort Marker = 0xAC42;
    private const string NameKey = "GHO_GardenerOwlHouse";
    private const string DescriptionKey = "GHO_GardenerOwlHouseDescription";
    private const string Description = "Harvests and tills garden beds. Stock the house with seeds to replant the same crops.";

    private static ItemData? listing;
    [ThreadStatic]
    private static bool packingGardener;

    internal static bool IsGardener(Item item) => item.dataId == HarvesterWorld.CleanerHouseItem && item.durability == Marker;

    internal static bool IsGardener(Furniture house) => house != null && house.IsSpawned && house.helperHouse != null && house.itemData != null
        && house.itemData.id == HarvesterWorld.CleanerHouseItem && house.itemData.durability == 0
        && house.reliability.Value == Marker;

    [HarmonyPatch(typeof(ItemManager), "Awake")]
    private static class RegisterListing
    {
        private static void Postfix(ItemManager __instance, Dictionary<uint, ItemData> ____itemData)
        {
            if (ItemManager.Instance != __instance || ____itemData == null)
                return;
            if (____itemData.ContainsKey(ShopId)
                || !____itemData.TryGetValue(HarvesterWorld.CleanerHouseItem, out ItemData stock)
                || stock.durability != 0 || stock.maxStack != 1)
            {
                listing = null;
                Debug.LogWarning($"{HouseName} could not be registered on this item catalog.");
                return;
            }

            listing = UnityEngine.Object.Instantiate(stock);
            listing.id = ShopId;
            listing.name = NameKey;
            listing.itemDescription = DescriptionKey;
            listing.icon = GardenerAppearance.TintIcon(stock.icon);
            ____itemData.Add(ShopId, listing);
        }
    }

    [HarmonyPatch(typeof(ShopUI), "SortItems")]
    private static class MerchantStock
    {
        private static void Postfix(List<ItemData> ___availableItems, List<ItemData> ___lockedItems)
        {
            if (listing == null || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost)
                return;
            ushort quest = listing.questDependant;
            bool lockedByQuest = quest > 0 && !QuestManager.Instance.completedQuests.Contains(quest)
                && !QuestManager.Instance.activeQuests.Contains(quest);
            bool locked = (GameStatus.Instance.level.Value < listing.levelDependant || lockedByQuest) && !Game.Instance.shopUnlock.Value;
            List<ItemData> products = locked ? ___lockedItems : ___availableItems;
            int cleaner = products.FindIndex(item => item.id == HarvesterWorld.CleanerHouseItem);
            products.Insert(cleaner >= 0 ? cleaner + 1 : products.Count, listing);
        }
    }

    [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.CheckWeapon))]
    private static class MerchantPurchase
    {
        private static void Prefix(ref Item item, bool isShop)
        {
            if (!isShop || listing == null || item.dataId != ShopId)
                return;

            // The shop SKU stays local. The indestructible stock house has an unused durability field
            // which the game copies to furniture reliability and saves for every placed object.
            item.dataId = HarvesterWorld.CleanerHouseItem;
            item.durability = Marker;
        }
    }

    [HarmonyPatch(typeof(Furniture), nameof(Furniture.CollectServerRpc))]
    private static class PackHouse
    {
        private static void Prefix(Furniture __instance, out bool __state)
        {
            __state = packingGardener;
            packingGardener = IsGardener(__instance);
        }

        private static void Finalizer(bool __state) => packingGardener = __state;
    }

    [HarmonyPatch(typeof(ContainerNet), nameof(ContainerNet.AddNewItem), typeof(Item), typeof(Vector3), typeof(bool))]
    private static class ReturnPackedHouse
    {
        private static void Prefix(ref Item item)
        {
            if (packingGardener && item.dataId == HarvesterWorld.CleanerHouseItem)
            {
                packingGardener = false;
                item.durability = Marker;
            }
        }
    }

    [HarmonyPatch(typeof(ContainerItemUI), nameof(ContainerItemUI.Upd))]
    private static class InventoryIcon
    {
        private static void Prefix(ContainerItemUI __instance)
        {
            if (listing != null && IsGardener(__instance.item))
                __instance.itemData = listing;
        }
    }

    [HarmonyPatch(typeof(PlayerUI), "OnSelectedHandItemData")]
    private static class HeldName
    {
        private static void Postfix(PlayerUI __instance, uint itemDataId)
        {
            if (itemDataId == HarvesterWorld.CleanerHouseItem && !PlayerInventory.Instance.inDialog
                && IsGardener(PlayerInventory.Instance.selectedItem))
                __instance.SetSelectedItemNameText(HouseName);
        }
    }

    [HarmonyPatch(typeof(ItemInfoUI), nameof(ItemInfoUI.SetInvItem))]
    private static class BackpackDetails
    {
        private static void Prefix(Item item, ref ItemData itemData)
        {
            if (listing != null && IsGardener(item))
                itemData = listing;
        }
    }

    [HarmonyPatch(typeof(LocalizedStringDatabase), "GenerateLocalizedString")]
    private static class Text
    {
        private static bool Prefix(TableReference tableReference, TableEntryReference tableEntryReference, ref string __result)
        {
            string key = tableEntryReference.Key;
            if (tableReference.TableCollectionName == "Interactive" && key is OwlName or HouseName)
            {
                __result = key;
                return false;
            }
            if (tableReference.TableCollectionName != "ItemData")
                return true;
            switch (key)
            {
                case NameKey: __result = HouseName; return false;
                case DescriptionKey: __result = Description; return false;
                default: return true;
            }
        }
    }
}
