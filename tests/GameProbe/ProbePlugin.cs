using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.AI;

namespace GardenHarvesterOwl.GameProbe;

[BepInPlugin("dev.valen.gardenharvesterowl.gameprobe", "Garden Harvester Owl Test Driver", "0.1.0")]
public sealed partial class ProbePlugin : BaseUnityPlugin
{
    private const string SaveSlot = "harvester-probe";
    private const ushort TestPort = 43826;
    private string? mode;
    private float menuReady;
    private float abortAt;
    private bool started;
    private IEnumerator<object?>? scenario;

    private void Awake()
    {
        // The test profile uses the game's local test transport and fixed loopback address.
        if (!File.Exists(Path.Combine(Paths.GameRootPath, ".harvester-test-copy")))
            return;
        mode = Environment.GetEnvironmentVariable("GHO_TEST_MODE");
        if (mode is not ("routes" or "host" or "coop" or "guest" or "reload" or "uninstall" or "controls"
            or "gardener" or "gardener-reload" or "gardener-uninstall"))
        {
            mode = null;
            return;
        }
        Application.runInBackground = true;
        var harmony = new Harmony("dev.valen.gardenharvesterowl.gameprobe");
        MethodInfo start = AccessTools.Method(typeof(Master), "Start");
        harmony.Patch(start, prefix: new HarmonyMethod(typeof(ProbePlugin), nameof(SelectLocalTransport)));
        abortAt = Time.realtimeSinceStartup + 210f;
        Logger.LogInfo($"PROBE mode {mode}");
    }

    private static void SelectLocalTransport(Master __instance)
    {
        __instance.netTransportType = Master.NetTransportType.LocalDev;
        string? testMode = Environment.GetEnvironmentVariable("GHO_TEST_MODE");
        if (testMode is "coop" or "guest")
        {
            DebugLog.Instance.logNothing = false;
            DebugLog.Instance.netManager = true;
        }
    }

    private void Update()
    {
        if (mode == null)
            return;
        try
        {
            if (mode == "controls" && Input.GetKeyDown(KeyCode.H))
                Logger.LogInfo($"PROBE INPUT H down (alt={Input.GetKey(KeyCode.LeftAlt)}, cursor={Cursor.lockState})");
            if (Time.realtimeSinceStartup > abortAt)
                throw new TimeoutException("World or test operation timed out.");
            if (!started)
            {
                if (MainMenu.Instance == null || UipManager.Instance == null || SaveManager.Instance == null)
                    return;
                if (menuReady == 0f)
                    menuReady = Time.realtimeSinceStartup + 0.8f;
                if (Time.realtimeSinceStartup < menuReady)
                    return;
                if (mode == "guest")
                {
                    if (!SignalExists("guest-menu-ready"))
                        Signal("guest-menu-ready");
                    if (!SignalExists("host-ready"))
                        return;
                }
                StartWorld();
                started = true;
            }
            if (scenario != null)
            {
                if (!scenario.MoveNext())
                    Finish($"PASS {mode}");
                return;
            }
            if (NetworkManager.Singleton == null || PlayerNet.Instance == null
                || (mode == "guest" ? !NetworkManager.Singleton.IsConnectedClient : !Master.Instance.isHostReady))
                return;
            scenario = mode switch
            {
                "host" => TestHost().GetEnumerator(),
                "coop" => TestCoopHost().GetEnumerator(),
                "guest" => TestGuest().GetEnumerator(),
                "reload" => TestReload().GetEnumerator(),
                "uninstall" => TestUninstall().GetEnumerator(),
                "controls" => TestControls().GetEnumerator(),
                "gardener" => TestGardener().GetEnumerator(),
                "gardener-reload" => TestGardenerReload().GetEnumerator(),
                "gardener-uninstall" => TestGardenerUninstall().GetEnumerator(),
                _ => TestRoutes().GetEnumerator(),
            };
        }
        catch (Exception error)
        {
            Logger.LogError("PROBE FAIL " + error);
            Finish("FAIL", 1);
        }
    }

    private void StartWorld()
    {
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        ushort port = mode is "coop" or "guest" ? (ushort)(TestPort + 1) : TestPort;
        var transport = UipManager.Instance.GetComponent<UnityTransport>();
        transport.SetConnectionData("127.0.0.1", port, "127.0.0.1");
        AppSettingsManager.Instance.appSettings.system.ipPort = port;
        if (mode == "guest")
        {
            UipManager.Instance.StartClient("127.0.0.1");
            return;
        }
        if (!SaveManager.Instance.LoadGame(SaveSlot))
            throw new InvalidOperationException("Test fixture could not load.");
        MainMenu.Instance.HostGame(false);
    }

    private void CheckRoutes()
    {
        HelperCleaner owl = (HelperCleaner)FurnitureManager.Instance.furniture.Values
            .First(x => x.itemData.id == 1156).helperHouse.helper;
        if (!Navigation.TryLanding(owl, out Vector3 landing))
            throw new InvalidOperationException($"No landing point near {owl.landingPos.position}.");
        var path = new NavMeshPath();
        int index = 0;
        int reachable = 0;
        foreach (GardenBed bed in GardenManager.Instance.gardenBeds)
        {
            index++;
            bool outbound = Navigation.TryBedPath(owl, landing, bed, path);
            Vector3 approach = outbound ? path.corners[path.corners.Length - 1] : default;
            bool roundTrip = outbound && Navigation.TryPath(approach, landing, Navigation.Filter(owl.agent), path);
            if (roundTrip)
                reachable++;
            Logger.LogInfo($"PROBE route bed {index} at {bed.transform.position}, out={outbound}, roundTrip={roundTrip}");
        }
        Require(index == 30 && reachable == index, $"Reachable {reachable}/{index} from {landing}");
    }

    private IEnumerable<object?> TestRoutes()
    {
        CheckRoutes();
        Logger.LogInfo("PROBE PASS all garden paths");
        yield break;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }

    private void Finish(string message, int exitCode = 0)
    {
        mode = null;
        Logger.LogInfo("PROBE " + message);
        Application.Quit(exitCode);
    }
}
