using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.AI;

namespace GardenHarvesterOwl;

internal sealed class OwlWorker : StateBase<HelperCleanerStateMachine>
{
    private enum Phase { Perched, Grounded, HopOut, ToBed, Working, Tilling, Sowing, ToNest, HopIn }

    private const float HopSeconds = 1.2f;
    private const float WorkSeconds = 0.8f;
    internal const float HoeSeconds = 0.48f;
    private const float RetrySeconds = 8f;
    private const float ArrivalTolerance = 0.55f;

    private readonly HelperCleaner owl;
    private readonly ContainerNet house;
    private readonly Dictionary<GardenBed, OwlWorker> reservations;
    private readonly Dictionary<GardenBed, ushort> pendingPlots;
    private readonly Dictionary<GardenBed, float> blockedUntil = new();
    private readonly NavMeshPath path = new();
    private readonly ManualLogSource log;
    private readonly List<GardenBed> candidates = new();

    private Phase phase;
    private GardenBed? bed;
    private Vector3 destination;
    private Vector3 hopStart;
    private Vector3 hopEnd;
    private Quaternion hopRotation;
    private float hopTime;
    private float finishWorkAt;
    private float tripDeadline;
    private float lastProgressAt;
    private Vector3 lastProgressFrom;
    private bool faulted;
    private float nextSearch;

    internal string Status { get; private set; } = "Waiting for ripe crops";
    internal bool IsHoeing => phase == Phase.Tilling;

    internal OwlWorker(HelperCleanerStateMachine machine, ContainerNet house,
        Dictionary<GardenBed, OwlWorker> reservations, Dictionary<GardenBed, ushort> pendingPlots, ManualLogSource log) : base(machine)
    {
        owl = machine.Owner;
        this.house = house;
        this.reservations = reservations;
        this.pendingPlots = pendingPlots;
        this.log = log;
    }

    public override void OnEnter()
    {
        if (Vector3.Distance(owl.transform.position, owl.defaultPos) < 0.3f)
            SetPerched();
        else
            phase = Phase.Grounded;
    }

    public override void OnExit()
    {
        ReleaseBed();
        StopWalking();
        owl.animator.SetBool("Cleanup", false);
    }

    internal void ReleaseBed()
    {
        if (!ReferenceEquals(bed, null) && reservations.TryGetValue(bed, out OwlWorker owner) && owner == this)
            reservations.Remove(bed);
        bed = null;
    }

    internal void SetPaused(bool value)
    {
        if (value)
            CancelWork("Paused");
        else
            nextSearch = 0f;
    }

    private bool CanHarvest => owl.isActivated && !faulted && owl.CanMove();

    public override void ExecutePerSecond()
    {
        if (phase is not (Phase.Perched or Phase.Grounded) || Time.time < nextSearch)
            return;

        if (!CanHarvest)
        {
            Status = faulted ? "Harvest paused after an error"
                : owl.isActivated ? "Inventory open" : "Paused";
            if (phase == Phase.Grounded && owl.CanMove())
                WalkHome();
            return;
        }

        if (TryChooseBed(out Vector3 landing))
        {
            if (phase == Phase.Perched)
                BeginHop(landing, Phase.HopOut);
            else
                StartBedWalk();
        }
        else if (phase == Phase.Grounded)
        {
            WalkHome();
        }
    }

    public override void ExecutePerFrame()
    {
        switch (phase)
        {
            case Phase.HopOut:
            case Phase.HopIn:
                TickHop();
                break;
            case Phase.ToBed:
                if (!CanHarvest || !HasWork())
                    CancelWork(owl.CanMove() ? "Checking garden" : "Inventory open");
                else if (Arrived())
                    BeginWork();
                else if (TravelFailed())
                    BlockBed();
                break;
            case Phase.Working:
                if (!CanHarvest || !StillRipe())
                    CancelWork(owl.CanMove() ? "Checking garden" : "Inventory open");
                else if (Time.time >= finishWorkAt)
                    FinishWork();
                break;
            case Phase.Tilling:
            case Phase.Sowing:
                TickTending();
                break;
            case Phase.ToNest:
                if (!owl.CanMove())
                {
                    tripDeadline += Time.deltaTime;
                    RefreshProgress();
                }
                else if (Arrived())
                    BeginHop(owl.defaultPos, Phase.HopIn);
                else if (TravelFailed())
                    HomeBlocked();
                break;
        }

        float speed = Navigation.AgentReady(owl.agent) && owl.agent.speed > 0f
            ? owl.agent.velocity.magnitude / owl.agent.speed : 0f;
        owl.animator.SetFloat("MoveSpeed", speed, 0.1f, Time.deltaTime);
    }

    private bool TryChooseBed(out Vector3 landing)
    {
        landing = owl.transform.position;
        bool perched = phase == Phase.Perched;
        if ((perched && !Navigation.TryLanding(owl, out landing)) || (!perched && !Navigation.AgentReady(owl.agent)))
        {
            Status = "Path blocked";
            nextSearch = Time.time + RetrySeconds;
            return false;
        }

        GardenManager garden = GardenManager.Instance;
        if (garden == null || garden.gardenBeds == null)
            return false;

        bool sawRipe = false;
        bool sawRoom = false;
        bool sawReserved = false;
        bool needsSeed = false;
        candidates.Clear();
        foreach (GardenBed candidate in garden.gardenBeds)
        {
            if (PendingSeed(candidate, out ushort seedId))
            {
                if (candidate.state.Value == GardenBed.State.Plowed && !GardenTending.HasSeed(house, seedId))
                {
                    needsSeed = true;
                    continue;
                }
            }
            else
            {
                if (!Harvest.TryGetProduce(candidate, out Item produce, out ushort stackLimit))
                    continue;
                sawRipe = true;
                if (!Harvest.Fits(house, produce, stackLimit))
                    continue;
            }
            sawRoom = true;
            if (reservations.ContainsKey(candidate))
            {
                sawReserved = true;
                continue;
            }
            if (!blockedUntil.TryGetValue(candidate, out float retryAt) || Time.time >= retryAt)
                candidates.Add(candidate);
        }

        Vector3 start = landing;
        candidates.Sort((a, b) => Navigation.HorizontalSqrDistance(start, a.transform.position)
            .CompareTo(Navigation.HorizontalSqrDistance(start, b.transform.position)));
        for (int i = 0; i < Math.Min(3, candidates.Count); i++)
        {
            GardenBed candidate = candidates[i];
            if (!Navigation.TryBedPath(owl, start, candidate, path) || path.corners.Length == 0)
            {
                blockedUntil[candidate] = Time.time + RetrySeconds;
                continue;
            }
            bed = candidate;
            reservations.Add(candidate, this);
            destination = path.corners[path.corners.Length - 1];
            Status = PendingSeed(candidate, out _) ? "Walking to bed" : "Walking to crop";
            return true;
        }

        Status = sawRoom ? (sawReserved ? "Waiting for a free bed" : "Path blocked") : sawRipe ? "Storage full"
            : needsSeed ? "Add matching seeds" : "Waiting for ripe crops";
        return false;
    }

    private bool StillRipe() => bed != null && Harvest.TryGetProduce(bed, out _, out _);

    private bool PendingSeed(GardenBed candidate, out ushort seedId)
    {
        seedId = 0;
        if (candidate == null || !pendingPlots.TryGetValue(candidate, out seedId))
            return false;
        if (GardenTending.IsEmpty(candidate))
            return !candidate.isInactive.Value;
        pendingPlots.Remove(candidate);
        return false;
    }

    private bool HasWork() => bed != null && (StillRipe() || (PendingSeed(bed, out ushort seedId)
        && (bed.state.Value == GardenBed.State.Abandoned || GardenTending.HasSeed(house, seedId))));

    private void StartBedWalk()
    {
        if (!CanHarvest || !HasWork())
        {
            CancelWork("Checking garden");
            return;
        }
        if (!StartWalk(destination, Phase.ToBed))
            BlockBed();
    }

    private void WalkHome()
    {
        if (!Navigation.AgentReady(owl.agent) || !Navigation.TryLanding(owl, out Vector3 landing)
            || !Navigation.TryPath(owl.agent.nextPosition, landing, Navigation.Filter(owl.agent), path))
        {
            HomeBlocked();
            return;
        }
        if ((owl.transform.position - landing).sqrMagnitude <= ArrivalTolerance * ArrivalTolerance)
        {
            BeginHop(owl.defaultPos, Phase.HopIn);
            return;
        }
        if (!StartWalk(landing, Phase.ToNest))
            HomeBlocked();
    }

    private bool StartWalk(Vector3 target, Phase newPhase)
    {
        if (!Navigation.AgentReady(owl.agent) || !owl.CanMove() || !owl.agent.SetDestination(target))
            return false;
        owl.agent.isStopped = false;
        destination = target;
        phase = newPhase;
        tripDeadline = Time.time + Mathf.Max(24f, 12f + 3f * Vector3.Distance(owl.transform.position, target) / Mathf.Max(1f, owl.agent.speed));
        RefreshProgress();
        return true;
    }

    private void BeginHop(Vector3 target, Phase newPhase)
    {
        StopWalking();
        if (owl.agent.enabled)
            owl.agent.enabled = false;
        hopStart = owl.transform.position;
        hopRotation = owl.transform.rotation;
        hopEnd = target;
        hopTime = Time.time;
        phase = newPhase;
        owl.animator.SetBool("NestSit", newPhase == Phase.HopIn);
        owl.networkAnimator.SetTrigger("Jump");
    }

    private void TickHop()
    {
        float progress = Mathf.Clamp01((Time.time - hopTime) / HopSeconds);
        owl.transform.position = Vector3.Lerp(hopStart, hopEnd, progress) + Vector3.up * (4f * progress * (1f - progress));
        owl.transform.rotation = Quaternion.Slerp(hopRotation, Quaternion.Euler(owl.defaultRot), progress);
        if (progress < 1f)
            return;
        if (phase == Phase.HopIn)
        {
            SetPerched();
            return;
        }
        phase = Phase.Grounded;
        owl.agent.enabled = true;
        StartBedWalk();
    }

    private void SetPerched()
    {
        phase = Phase.Perched;
        owl.animator.SetBool("NestSit", true);
        owl.agent.enabled = false;
    }

    private bool Arrived() => (owl.transform.position - destination).sqrMagnitude <= ArrivalTolerance * ArrivalTolerance;

    private void RefreshProgress()
    {
        lastProgressAt = Time.time;
        lastProgressFrom = owl.transform.position;
    }

    private bool TravelFailed()
    {
        if (!Navigation.AgentReady(owl.agent) || Time.time > tripDeadline)
            return true;
        if ((owl.transform.position - lastProgressFrom).sqrMagnitude > 0.09f)
            RefreshProgress();
        return Time.time - lastProgressAt > 5f
            || (!owl.agent.pathPending && owl.agent.pathStatus != NavMeshPathStatus.PathComplete);
    }

    private void BeginWork()
    {
        if (bed == null || !Navigation.IsBeside(owl, owl.transform.position, bed.transform.position))
        {
            BlockBed();
            return;
        }
        StopWalking();
        Vector3 toward = bed.transform.position - owl.transform.position;
        toward.y = 0f;
        if (toward.sqrMagnitude > 0.01f)
            owl.transform.rotation = Quaternion.LookRotation(toward);
        owl.animator.SetBool("Cleanup", true);
        if (PendingSeed(bed, out ushort seedId))
        {
            BeginTending(seedId);
            return;
        }
        finishWorkAt = Time.time + WorkSeconds;
        phase = Phase.Working;
        Status = "Harvesting";
    }

    private void FinishWork()
    {
        if (bed == null || !Navigation.IsBeside(owl, owl.transform.position, bed.transform.position)
            || !reservations.TryGetValue(bed, out OwlWorker worker) || worker != this)
        {
            CancelWork("Checking garden");
            return;
        }

        ushort seedId = bed.currentSeedId.Value;
        HarvestResult result;
        try { result = Harvest.TryCollect(bed, house, log); }
        catch (Exception error)
        {
            log.LogError(error);
            result = HarvestResult.Failed;
        }
        if (result == HarvestResult.Collected && GardenTending.IsRegularSeed(seedId))
        {
            pendingPlots[bed] = seedId;
            BeginTending(seedId);
            return;
        }
        faulted = result == HarvestResult.Failed;
        CancelWork(result == HarvestResult.NoRoom ? "Storage full" : faulted ? "Harvest paused after an error" : "Checking garden");
    }

    private void BeginTending(ushort seedId)
    {
        if (bed != null && bed.state.Value == GardenBed.State.Abandoned)
        {
            phase = Phase.Tilling;
            finishWorkAt = Time.time + HoeSeconds;
            tripDeadline = Time.time + RetrySeconds;
            Status = "Tilling soil";
        }
        else if (GardenTending.HasSeed(house, seedId))
        {
            phase = Phase.Sowing;
            finishWorkAt = Time.time + WorkSeconds;
            Status = "Planting seed";
        }
        else
        {
            CancelWork("Add matching seeds");
        }
    }

    private void TickTending()
    {
        if (bed == null || !CanHarvest || !PendingSeed(bed, out ushort seedId)
            || !Navigation.IsBeside(owl, owl.transform.position, bed.transform.position))
        {
            CancelWork(owl.CanMove() ? "Checking garden" : "Inventory open");
            return;
        }
        if (Time.time < finishWorkAt)
            return;

        if (phase == Phase.Tilling)
        {
            if (bed.state.Value == GardenBed.State.Abandoned && Time.time < tripDeadline)
            {
                try
                {
                    bed.PlowBedServerRpc();
                    SoundManager.Instance.PlayServerRpc(SoundEvent.HoeHit, bed.transform.position);
                }
                catch (Exception error)
                {
                    log.LogWarning($"An owl's hoe strike could not complete. {error}");
                }
                finishWorkAt = Time.time + HoeSeconds;
            }
            if (bed.state.Value == GardenBed.State.Plowed)
                BeginTending(seedId);
            else if (Time.time >= tripDeadline)
                BlockBed();
            return;
        }

        PlantingResult result = GardenTending.TrySow(bed, house, seedId, log);
        if (result == PlantingResult.Planted)
            pendingPlots.Remove(bed);
        faulted = result == PlantingResult.Failed;
        CancelWork(faulted ? "Gardening paused after an error"
            : result == PlantingResult.NoSeed ? "Add matching seeds" : "Checking garden");
    }

    private void BlockBed()
    {
        if (bed != null)
            blockedUntil[bed] = Time.time + RetrySeconds;
        CancelWork("Path blocked");
    }

    private void HomeBlocked()
    {
        StopWalking();
        phase = Phase.Grounded;
        nextSearch = Time.time + RetrySeconds;
        Status = "Path blocked";
    }

    private void StopWalking()
    {
        if (Navigation.AgentReady(owl.agent))
        {
            owl.agent.isStopped = true;
            owl.agent.ResetPath();
        }
    }

    private void CancelWork(string status)
    {
        ReleaseBed();
        Status = status;
        if (phase is Phase.Perched or Phase.HopIn or Phase.HopOut or Phase.ToNest)
            return;
        StopWalking();
        owl.animator.SetBool("Cleanup", false);
        phase = Phase.Grounded;
    }
}
