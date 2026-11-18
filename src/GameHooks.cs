using System;
using HarmonyLib;

namespace GardenHarvesterOwl;

internal static class GameHooks
{
    [HarmonyPatch(typeof(HelperCleaner), nameof(HelperCleaner.Activate))]
    internal static class ResumeOwl
    {
        private static bool Prefix(HelperCleaner __instance)
        {
            OwlWorker? worker = HarvesterWorld.WorkerFor(__instance);
            if (worker == null)
                return true;
            __instance.isActivated = true;
            worker.SetPaused(false);
            return false;
        }
    }

    [HarmonyPatch(typeof(HelperCleaner), nameof(HelperCleaner.Deactivate))]
    internal static class PauseOwl
    {
        private static bool Prefix(HelperCleaner __instance)
        {
            OwlWorker? worker = HarvesterWorld.WorkerFor(__instance);
            if (worker == null)
                return true;
            __instance.isActivated = false;
            worker.SetPaused(true);
            return false;
        }
    }

    [HarmonyPatch(typeof(HelperCleaner), nameof(HelperCleaner.OnNetworkDespawn))]
    internal static class DespawnOwl
    {
        private static void Prefix(HelperCleaner __instance) => HarvesterWorld.WorkerFor(__instance)?.ReleaseBed();
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.LoadGame))]
    internal static class LoadWorld
    {
        private static void Postfix(SaveManager __instance, string gameName, string backupDate, bool __result)
        {
            if (__result)
                Plugin.World?.LoadPlots(__instance, gameName, backupDate);
            else
                Plugin.World?.Reset();
        }
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.NewGame))]
    internal static class NewWorld
    {
        private static void Postfix() => Plugin.World?.Reset();
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveGame), typeof(string))]
    internal static class SaveWorld
    {
        private static void Prefix(SaveManager __instance, string fileName, out Action? __state)
        {
            __state = null;
            HarvesterWorld? world = Plugin.World;
            if (world == null || !HarvesterWorld.IsHost)
                return;
            __state = () => world.SavePlots(__instance, fileName);
            __instance.onGameSaved += __state;
        }

        private static void Finalizer(SaveManager __instance, Action? __state)
        {
            if (__state != null)
                __instance.onGameSaved -= __state;
        }
    }
}
