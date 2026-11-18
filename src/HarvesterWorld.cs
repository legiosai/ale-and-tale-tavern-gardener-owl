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
    private sealed class Assignment
    {
        internal bool Desired = true;
        internal OwlWorker? Worker;
    }

    internal const ushort CleanerHouseItem = 1156;

    private static readonly AccessTools.FieldRef<HelperCleaner, HelperCleanerStateMachine> Machine =
        AccessTools.FieldRefAccess<HelperCleaner, HelperCleanerStateMachine>("stateMachine");
    private static readonly AccessTools.FieldRef<HelperCleanerNestSittingState, bool> LeavingNest =
        AccessTools.FieldRefAccess<HelperCleanerNestSittingState, bool>("jumpInvoked");
    private static readonly AccessTools.FieldRef<SaveManager, bool> BinarySave =
        AccessTools.FieldRefAccess<SaveManager, bool>("_useBinSerialization");

    private readonly RoleStore roles;
    private readonly ManualLogSource log;
    private readonly Dictionary<Furniture, Assignment> assignments = new();
    private readonly Dictionary<GardenBed, OwlWorker> reservations = new();
    private readonly Dictionary<GardenBed, ushort> pendingPlots = new();
    private readonly List<Furniture> remove = new();
    private HashSet<RoleStore.HouseKey> restore = new();
    private RoleStore.SavedPlot[] restorePlots = Array.Empty<RoleStore.SavedPlot>();
    private FurnitureManager? furnitureManager;
    private float scanAt;

    internal HarvesterWorld(RoleStore roles, ManualLogSource log)
    {
        this.roles = roles;
        this.log = log;
    }

    internal static bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost
        && NetworkManager.Singleton.IsListening && Master.Instance != null && Master.Instance.isHostReady
        && !Master.Instance.isPlaySceneQuitting;

    internal void LoadRoles(SaveManager manager, string slot, string backupDate)
    {
        Clear();
        restore = roles.Load(slot, backupDate, BinarySave(manager), out restorePlots);
    }

    internal void ResetRoles()
    {
        Clear();
        restore.Clear();
        restorePlots = Array.Empty<RoleStore.SavedPlot>();
    }

    internal void SaveRoles(SaveManager manager, string slot)
    {
        if (!IsHost)
            return;
        var keys = assignments.Where(pair => pair.Value.Desired && !GardenerItem.IsGardener(pair.Key)
                && TryGetParts(pair.Key, out _, out _))
            .Select(pair => new RoleStore.HouseKey(pair.Key));
        var plots = new List<RoleStore.SavedPlot>(restorePlots);
        GardenBed[]? beds = GardenManager.Instance?.gardenBeds;
        if (beds != null)
        {
            for (int i = 0; i < beds.Length; i++)
            {
                GardenBed bed = beds[i];
                if (GardenTending.IsEmpty(bed) && pendingPlots.TryGetValue(bed, out ushort seedId))
                    plots.Add(new RoleStore.SavedPlot(i, seedId));
            }
        }
        roles.Save(slot, BinarySave(manager), keys.Concat(restore).Distinct(), plots);
    }

    internal static bool IsCleanerHouse(Furniture house) => house != null && house.IsSpawned
        && house.itemData != null && house.itemData.id == CleanerHouseItem && house.helperHouse != null;

    internal static bool TryGetParts(Furniture house, out HelperCleaner owl, out ContainerNet storage)
    {
        owl = null!;
        storage = null!;
        if (!IsCleanerHouse(house) || house.helperHouse.helper is not HelperCleaner cleaner || cleaner == null
            || !cleaner.IsSpawned || !cleaner.IsServer || cleaner.containerRef == null)
            return false;
        ContainerNet container = house.helperHouse.container;
        if (container == null || !container.IsSpawned || container.id.Value == 0 || cleaner.containerRef.containerNet != container)
            return false;
        owl = cleaner;
        storage = container;
        return true;
    }

    internal string StatusOf(Furniture house)
    {
        if (!assignments.TryGetValue(house, out Assignment assignment))
            return GardenerItem.IsGardener(house) ? GardenerItem.OwlName + " · Getting ready" : "Cleaner";
        if (!assignment.Desired)
            return "Returning to Cleaner";
        string role = GardenerItem.IsGardener(house) ? GardenerItem.OwlName : "Harvester";
        return role + " · " + (assignment.Worker?.Status ?? "Finishing current work");
    }

    internal bool WantsHarvester(Furniture house) => assignments.TryGetValue(house, out Assignment assignment) && assignment.Desired;

    internal void SetHarvester(Furniture house, bool enabled)
    {
        if (!IsHost || !IsCleanerHouse(house))
            return;
        if (!assignments.TryGetValue(house, out Assignment assignment))
        {
            if (enabled)
                assignments.Add(house, new Assignment());
            return;
        }
        assignment.Desired = enabled;
        assignment.Worker?.SetRetiring(!enabled);
    }

    internal void Tick()
    {
        if (!IsHost || FurnitureManager.Instance == null)
        {
            if (!ReferenceEquals(furnitureManager, null))
                ResetRoles();
            return;
        }

        if (furnitureManager != FurnitureManager.Instance)
        {
            Clear();
            furnitureManager = FurnitureManager.Instance;
            scanAt = 0f;
        }

        if (Time.time >= scanAt)
        {
            scanAt = Time.time + 0.5f;
            foreach (Furniture house in furnitureManager.furniture.Values)
            {
                if (TryGetParts(house, out _, out _) && (restore.Remove(new RoleStore.HouseKey(house))
                    || (GardenerItem.IsGardener(house) && !assignments.ContainsKey(house))))
                    SetHarvester(house, true);
            }
            RestorePlots();
        }

        remove.Clear();
        foreach (var pair in assignments)
        {
            Furniture house = pair.Key;
            Assignment job = pair.Value;
            if (!IsCleanerHouse(house))
            {
                job.Worker?.ReleaseBed();
                remove.Add(house);
                continue;
            }
            if (!job.Desired && job.Worker == null)
            {
                remove.Add(house);
                continue;
            }
            if (!TryGetParts(house, out HelperCleaner owl, out ContainerNet storage))
                continue;

            HelperCleanerStateMachine machine = Machine(owl);
            if (machine == null)
                continue;
            if (job.Worker != null && machine.CurrentState != job.Worker)
            {
                job.Worker.ReleaseBed();
                job.Worker = null;
            }
            if (job.Desired && job.Worker == null && CanTakeOver(owl, machine))
            {
                HelpersManager.Instance.RemoveFromTaskList(owl);
                job.Worker = new OwlWorker(machine, storage, reservations, pendingPlots, log);
                ChangeState(owl, machine, job.Worker);
                log.LogInfo($"House {house.id.Value} assigned {GardenerItem.OwlName}.");
            }
            else if (!job.Desired && job.Worker?.CanRestoreCleaner == true)
            {
                StateBase<HelperCleanerStateMachine> stock = Vector3.Distance(owl.transform.position, owl.defaultPos) < 0.3f
                    ? new HelperCleanerNestSittingState(machine, false) : new HelperCleanerMoveToDefaultPosState(machine);
                ChangeState(owl, machine, stock);
                if (Navigation.AgentReady(owl.agent))
                    owl.UpdateAgentStopped();
                remove.Add(house);
                log.LogInfo($"House {house.id.Value} returned to Cleaner.");
            }
        }
        foreach (Furniture house in remove)
            assignments.Remove(house);
    }

    private void Clear()
    {
        foreach (Assignment job in assignments.Values)
            job.Worker?.ReleaseBed();
        assignments.Clear();
        reservations.Clear();
        pendingPlots.Clear();
        furnitureManager = null;
    }

    private void RestorePlots()
    {
        GardenBed[]? beds = GardenManager.Instance?.gardenBeds;
        if (restorePlots.Length == 0 || beds == null || beds.Any(bed => bed == null || !bed.IsSpawned))
            return;
        foreach (RoleStore.SavedPlot plot in restorePlots)
        {
            if (plot.bedIndex >= 0 && plot.bedIndex < beds.Length && GardenTending.IsRegularSeed(plot.seedId)
                && GardenTending.IsEmpty(beds[plot.bedIndex]))
                pendingPlots[beds[plot.bedIndex]] = plot.seedId;
        }
        restorePlots = Array.Empty<RoleStore.SavedPlot>();
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
