using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using DG.Tweening;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace GardenHarvesterOwl;

internal sealed class HarvesterWorld
{
    internal const ushort CleanerHouseItem = 1156;

    private static readonly AccessTools.FieldRef<HelperCleaner, HelperCleanerStateMachine> Machine =
        AccessTools.FieldRefAccess<HelperCleaner, HelperCleanerStateMachine>("stateMachine");
    private static readonly AccessTools.FieldRef<HelperCleanerNestSittingState, bool> LeavingNest =
        AccessTools.FieldRefAccess<HelperCleanerNestSittingState, bool>("jumpInvoked");
    private static readonly AccessTools.FieldRef<SaveManager, bool> BinarySave =
        AccessTools.FieldRefAccess<SaveManager, bool>("_useBinSerialization");

    private readonly PlotStore plots;
    private readonly ManualLogSource log;
    private readonly Dictionary<Furniture, OwlWorker> workers = new();
    private readonly Dictionary<GardenBed, OwlWorker> reservations = new();
    private readonly Dictionary<GardenBed, ushort> pendingPlots = new();
    private readonly List<Furniture> remove = new();
    private PlotStore.SavedPlot[] restorePlots = Array.Empty<PlotStore.SavedPlot>();
    private FurnitureManager? furnitureManager;
    private float scanAt;

    internal HarvesterWorld(PlotStore plots, ManualLogSource log)
    {
        this.plots = plots;
        this.log = log;
    }

    internal static bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost
        && NetworkManager.Singleton.IsListening && Master.Instance != null && Master.Instance.isHostReady
        && !Master.Instance.isPlaySceneQuitting;

    internal void LoadPlots(SaveManager manager, string slot, string backupDate)
    {
        Clear();
        restorePlots = plots.Load(slot, backupDate, BinarySave(manager));
    }

    internal void Reset()
    {
        Clear();
        restorePlots = Array.Empty<PlotStore.SavedPlot>();
    }

    internal void SavePlots(SaveManager manager, string slot)
    {
        if (!IsHost)
            return;
        var saved = new List<PlotStore.SavedPlot>(restorePlots);
        GardenBed[]? beds = GardenManager.Instance?.gardenBeds;
        if (beds != null)
        {
            for (int i = 0; i < beds.Length; i++)
            {
                GardenBed bed = beds[i];
                if (GardenTending.IsEmpty(bed) && pendingPlots.TryGetValue(bed, out ushort seedId))
                    saved.Add(new PlotStore.SavedPlot(i, seedId));
            }
        }
        plots.Save(slot, BinarySave(manager), saved);
    }

    private static bool TryGetParts(Furniture house, out HelperCleaner owl, out ContainerNet storage)
    {
        owl = null!;
        storage = null!;
        if (!GardenerItem.IsGardener(house) || house.helperHouse.helper is not HelperCleaner cleaner || cleaner == null
            || !cleaner.IsSpawned || !cleaner.IsServer || cleaner.containerRef == null)
            return false;
        ContainerNet container = house.helperHouse.container;
        if (container == null || !container.IsSpawned || container.id.Value == 0 || cleaner.containerRef.containerNet != container)
            return false;
        owl = cleaner;
        storage = container;
        return true;
    }

    internal string StatusOf(Furniture house) => GardenerItem.OwlName + " · "
        + (workers.TryGetValue(house, out OwlWorker worker) ? worker.Status : "Getting ready");

    internal void Tick()
    {
        if (!IsHost || FurnitureManager.Instance == null)
        {
            if (!ReferenceEquals(furnitureManager, null))
                Reset();
            return;
        }

        if (furnitureManager != FurnitureManager.Instance)
        {
            Clear();
            furnitureManager = FurnitureManager.Instance;
            scanAt = 0f;
        }

        if (Time.time < scanAt)
            return;
        scanAt = Time.time + 0.5f;
        RestorePlots();

        remove.Clear();
        foreach (var pair in workers)
        {
            if (!TryGetParts(pair.Key, out HelperCleaner owl, out _) || WorkerFor(owl) != pair.Value)
            {
                pair.Value.ReleaseBed();
                remove.Add(pair.Key);
            }
        }
        foreach (Furniture house in remove)
            workers.Remove(house);

        foreach (Furniture house in furnitureManager.furniture.Values)
        {
            if (workers.ContainsKey(house) || !TryGetParts(house, out HelperCleaner owl, out ContainerNet storage))
                continue;

            HelperCleanerStateMachine machine = Machine(owl);
            if (machine == null || !CanTakeOver(owl, machine))
                continue;
            HelpersManager.Instance.RemoveFromTaskList(owl);
            var worker = new OwlWorker(machine, storage, reservations, pendingPlots, log);
            ChangeState(owl, machine, worker);
            workers.Add(house, worker);
            log.LogInfo($"House {house.id.Value} started gardening.");
        }
    }

    private void Clear()
    {
        foreach (OwlWorker worker in workers.Values)
            worker.ReleaseBed();
        workers.Clear();
        reservations.Clear();
        pendingPlots.Clear();
        furnitureManager = null;
    }

    private void RestorePlots()
    {
        GardenBed[]? beds = GardenManager.Instance?.gardenBeds;
        if (restorePlots.Length == 0 || beds == null || beds.Any(bed => bed == null || !bed.IsSpawned))
            return;
        foreach (PlotStore.SavedPlot plot in restorePlots)
        {
            if (plot.bedIndex >= 0 && plot.bedIndex < beds.Length && GardenTending.IsRegularSeed(plot.seedId)
                && GardenTending.IsEmpty(beds[plot.bedIndex]))
                pendingPlots[beds[plot.bedIndex]] = plot.seedId;
        }
        restorePlots = Array.Empty<PlotStore.SavedPlot>();
    }

    private static bool CanTakeOver(HelperCleaner owl, HelperCleanerStateMachine machine) =>
        HelpersManager.Instance != null && owl.agent != null && owl.animator != null && owl.networkAnimator != null
        && owl.landingPos != null && !DOTween.IsTweening(owl.transform)
        && machine.CurrentState is not HelperCleanerMessCleanState
        && (machine.CurrentState is not HelperCleanerNestSittingState nest || !LeavingNest(nest));

    private static void ChangeState(HelperCleaner owl, HelperCleanerStateMachine machine, StateBase<HelperCleanerStateMachine> state)
    {
        // The stock machine only accepts most transitions once the house is active.
        bool active = owl.isActivated;
        owl.isActivated = true;
        try { machine.ChangeState(state); }
        finally { owl.isActivated = active; }
    }

    internal static OwlWorker? WorkerFor(HelperCleaner owl) => owl.IsServer ? Machine(owl)?.CurrentState as OwlWorker : null;
}
