using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GardenHarvesterOwl;

[BepInPlugin(Id, GardenerItem.OwlName, Version)]
[BepInProcess("Ale and Tale Tavern.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    internal const string Id = "dev.valen.gardenharvesterowl";
    internal const string Version = "0.2.0";
    private const string SupportedAssembly = "fb4ed6f913cfa48064d9b7c4b8a583c04113e0c827792e2ab329e5d12b7f5e44";

    private Harmony? harmony;
    private ConfigEntry<KeyboardShortcut>? roleShortcut;
    private string? tooltip;
    private GUIStyle? tooltipStyle;

    internal static HarvesterWorld? World { get; private set; }

    private void Awake()
    {
        try
        {
            string assemblyPath = Path.Combine(Application.dataPath, "Managed", "Assembly-CSharp.dll");
            using var stream = File.OpenRead(assemblyPath);
            using var sha = SHA256.Create();
            string fingerprint = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (fingerprint != SupportedAssembly)
            {
                Logger.LogWarning($"{GardenerItem.OwlName} is disabled for this game build ({Application.version}, {fingerprint}).");
                return;
            }

            roleShortcut = Config.Bind("Controls", "ToggleHarvester", new KeyboardShortcut(KeyCode.H, KeyCode.LeftAlt),
                "While looking at a cleaner house during play, switch between Cleaner and Harvester.");
            var roles = new RoleStore(Path.Combine(Paths.ConfigPath, "GardenHarvesterOwl"), Logger);
            World = new HarvesterWorld(roles, Logger);
            harmony = new Harmony(Id);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo($"{GardenerItem.OwlName} {Version} ready for Ale & Tale Tavern {Application.version} (Steam build 26671410).");
        }
        catch (Exception error)
        {
            World = null;
            harmony?.UnpatchSelf();
            Logger.LogError($"{GardenerItem.OwlName} could not start. {error}");
        }
    }

    private void Update()
    {
        tooltip = null;
        if (World == null || roleShortcut == null)
            return;
        World.Tick();
        if (!HarvesterWorld.IsHost || PlayerInventory.Instance == null || PlayerInput.Instance == null
            || !PlayerInput.Instance.CanProcessInput() || Cursor.lockState != CursorLockMode.Locked)
            return;

        var inventory = PlayerInventory.Instance;
        if (!inventory.interactiveInSight || inventory.interactive == null)
            return;
        var house = inventory.interactive.GetComponentInParent<Furniture>();
        if (!HarvesterWorld.IsCleanerHouse(house))
            return;

        if (GardenerItem.IsGardener(house))
        {
            tooltip = World.StatusOf(house);
            return;
        }

        bool assigned = World.WantsHarvester(house);
        if (roleShortcut.Value.IsDown())
        {
            World.SetHarvester(house, !assigned);
            assigned = !assigned;
        }
        string nextRole = assigned ? "Cleaner" : "Harvester";
        tooltip = $"{World.StatusOf(house)}\n{roleShortcut.Value}  Switch to {nextRole}";
    }

    private void OnGUI()
    {
        if (tooltip == null)
            return;
        tooltipStyle ??= new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.Clamp(Screen.height / 60, 14, 20),
        };
        float width = Mathf.Min(Screen.width - 16, 330);
        GUI.Box(new Rect((Screen.width - width) * 0.5f, Screen.height * 0.5f + 86, width, 58), tooltip, tooltipStyle);
    }

    private void OnDestroy()
    {
        World?.ResetRoles();
        World = null;
        harmony?.UnpatchSelf();
    }
}
