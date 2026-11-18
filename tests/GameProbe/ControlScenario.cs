using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace GardenHarvesterOwl.GameProbe;

public sealed partial class ProbePlugin
{
    private IEnumerable<object?> TestControls()
    {
        foreach (var frame in WaitFor(() => Houses().Length > 0 && PlayerMovement.Instance != null, "the player and Cleaner Owl House"))
            yield return frame;

        Furniture house = Houses()[0];
        VerifyRegularVarieties(house.helperHouse.container);
        var owl = (HelperCleaner)house.helperHouse.helper;
        Interactive interactive = house.helperHouse.interactive;
        Vector3 aim = interactive.transform.position;
        if (interactive.TryGetComponent<Collider>(out var collider))
            aim = collider.bounds.center;
        var target = new GameObject("Test gaze");
        target.transform.position = aim;
        var movement = PlayerMovement.Instance;
        var inventory = PlayerInventory.Instance;
        inventory.SetState(PlayerInventory.State.Play);
        Cursor.lockState = CursorLockMode.Locked;
        movement.SetLookAt(target.transform);
        bool Aimed() => inventory.interactiveInSight && inventory.interactive != null
            && inventory.interactive.GetComponentInParent<Furniture>() == house;
        bool CanToggle() => Aimed() && PlayerInput.Instance.CanProcessInput()
            && Cursor.lockState == CursorLockMode.Locked;

        for (int step = 0; step < 8 && !Aimed(); step++)
        {
            Vector3 ground = new Vector3(aim.x, house.transform.position.y, aim.z)
                + Quaternion.Euler(0f, 45f * step, 0f) * (Vector3.forward * 1.6f);
            if (!NavMesh.SamplePosition(ground, out NavMeshHit hit, 1f, Navigation.Filter(owl.agent)))
                continue;
            movement.Teleport(hit.position + Vector3.up * 0.08f);
            movement.FreezePosition(true);
            float until = Time.realtimeSinceStartup + 0.5f;
            while (!Aimed() && Time.realtimeSinceStartup < until)
                yield return null;
        }

        foreach (var frame in WaitFor(CanToggle, "the stock crosshair and controls to focus the cleaner house", 8))
            yield return frame;
        object world = GetWorld();
        Require(Status(world, house) == "Cleaner", "Control test must begin with a stock cleaner.");
        Logger.LogInfo("PROBE INPUT ready for Left Alt + H");
        foreach (var frame in WaitFor(() => Status(world, house).StartsWith("Harvester", StringComparison.Ordinal),
            "real role shortcut input", 45)) yield return frame;
        foreach (var frame in WaitFor(() => CanToggle() && !Input.GetKey(KeyCode.H) && !Input.GetKey(KeyCode.LeftAlt),
            () => $"released keys and focus (aimed={Aimed()}, cursor={Cursor.lockState}, input={PlayerInput.Instance.CanProcessInput()})", 12))
            yield return frame;
        Logger.LogInfo("PROBE INPUT ready to switch back");
        foreach (var frame in WaitFor(() => Status(world, house) == "Cleaner", "real role shortcut to restore Cleaner", 45)) yield return frame;
        movement.FreezePosition(false);
        movement.SetLookAt();
        Destroy(target);
        Logger.LogInfo("PROBE PASS real hotkey routed through the base-game crosshair in both directions");
    }

    private void VerifyRegularVarieties(ContainerNet storage)
    {
        GardenBed[] beds = GardenManager.Instance.gardenBeds;
        GardenBed bed = beds[0];
        FocusBeds(beds, bed);
        Clear(storage);
        (ushort seed, ushort produce)[] crops = { (1201, 10), (1202, 15), (1203, 16), (1204, 17), (1205, 18) };
        foreach (var crop in crops)
        {
            Ripen(bed, crop.seed);
            Require(CollectForTest(bed, storage) == "Collected" && IsAbandoned(bed)
                && storage.GetItemAmount(crop.produce) == 2, $"Wrong yield or item for seed {crop.seed}.");
        }
        Clear(storage);
        Logger.LogInfo("PROBE PASS actual harvest adapter for all five regular crop varieties");
    }
}
