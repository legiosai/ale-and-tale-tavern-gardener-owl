using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using Unity.Netcode;
using UnityEngine;

namespace GardenHarvesterOwl.GameProbe;

public sealed partial class ProbePlugin
{
    private IEnumerable<object?> TestCoopHost()
    {
        foreach (var frame in WaitFor(() => Houses().Length > 0 && GardenManager.Instance != null, "host furniture and beds")) yield return frame;
        object world = GetWorld();
        Furniture house = Houses()[0];
        ContainerNet storage = house.helperHouse.container;
        GardenBed[] beds = GardenManager.Instance.gardenBeds;
        GardenBed bed = beds[0];
        Clear(storage);
        FocusBeds(beds, bed);
        Ripen(bed, 1204);
        house.helperHouse.isHelperActive.Value = false;
        MarkAsGardener(house);
        Signal("host-ready");
        foreach (var frame in WaitFor(() => SignalExists("guest-ready") && NetworkManager.Singleton.ConnectedClientsIds.Count == 2,
            "a guest without the harvester plugin", 70)) yield return frame;

        house.helperHouse.isHelperActive.Value = true;
        foreach (var frame in WaitFor(() => SignalExists("guest-collected"), () => $"guest observation and transfer ({Status(world, house)}, corn={storage.GetItemAmount(Corn)})", 47)) yield return frame;
        Require(IsCleared(bed) && storage.GetItemAmount(Corn) == 0, "The host and guest disagreed after collection.");
        foreach (var frame in WaitFor(() => SignalExists("guest-rejoined"), "guest to reconnect and validate the saved network state", 50)) yield return frame;
        Require(IsCleared(bed) && storage.GetItemAmount(Corn) == 0, "Guest reconnect changed the harvest state.");
        Logger.LogInfo("PROBE PASS host saw one guest harvest transfer and consistent state after reconnect");
    }

    private IEnumerable<object?> TestGuest()
    {
        Require(!Chainloader.PluginInfos.ContainsKey(ModId), "The guest profile contains the harvest plugin.");
        HelperCleaner? owl = null;
        foreach (var frame in WaitFor(() => Houses().Length > 0 && GardenManager.Instance != null
            && PlayerInventory.Instance != null && PlayerInventory.Instance.inventory != null
            && (owl = FindRemoteOwl(Houses()[0].helperHouse.container)) != null,
            "the replicated house, owl, and garden", 40)) yield return frame;
        ushort houseId = Houses()[0].id.Value;
        ContainerNet storage = Houses()[0].helperHouse.container;
        GardenBed bed = GardenManager.Instance.gardenBeds[0];
        Require(bed.state.Value == GardenBed.State.PlantGrown && storage.GetItemAmount(Corn) == 0, "The initial garden state did not replicate.");
        Vector3 start = owl!.transform.position;
        Signal("guest-ready");

        foreach (var frame in WaitFor(() => Vector3.Distance(start, owl.transform.position) > 3f, "remote owl movement", 38)) yield return frame;
        foreach (var frame in WaitFor(() => IsCleared(bed) && storage.GetItemAmount(Corn) == 2, "crop and storage changes on guest", 38)) yield return frame;
        uint before = PlayerInventory.Instance.inventory.GetItemAmount(Corn);
        storage.TakeAllServerRpc();
        foreach (var frame in WaitFor(() => storage.GetItemAmount(Corn) == 0 && PlayerInventory.Instance.inventory.GetItemAmount(Corn) == before + 2,
            "transfer using the base-game container")) yield return frame;
        Logger.LogInfo("PROBE PASS guest without mod saw the walk, harvest, and two corn in its backpack");
        Signal("guest-collected");
        UipManager.Instance.Disconnect();

        foreach (var frame in WaitFor(() => MainMenu.Instance != null && !NetworkManager.Singleton.IsListening, "the main menu after disconnect")) yield return frame;
        UipManager.Instance.StartClient("127.0.0.1");
        foreach (var frame in WaitFor(() => NetworkManager.Singleton.IsConnectedClient && PlayerNet.Instance != null
            && Houses().Any(x => x.id.Value == houseId) && GardenManager.Instance != null && PlayerInventory.Instance.inventory != null,
            "the second connection and synchronized world", 45)) yield return frame;
        ContainerNet after = Houses().Single(x => x.id.Value == houseId).helperHouse.container;
        Require(IsCleared(GardenManager.Instance.gardenBeds[0]) && after.GetItemAmount(Corn) == 0,
            "The reconnect brought back produce or a crop that was already harvested.");
        Logger.LogInfo("PROBE PASS guest rejoined with the same cleared bed and empty house");
        Signal("guest-rejoined");
    }

    private static HelperCleaner? FindRemoteOwl(ContainerNet house) => FindObjectsOfType<HelperCleaner>()
        .FirstOrDefault(x => x.containerRef != null && x.containerRef.containerId.Value == house.id.Value);

    private static string SignalPath(string name) => Path.Combine(Path.GetDirectoryName(Paths.GameRootPath)!, "coop-" + name);
    private static bool SignalExists(string name) => File.Exists(SignalPath(name));
    private static void Signal(string name) => File.WriteAllText(SignalPath(name), name);
}
