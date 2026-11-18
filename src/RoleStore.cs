using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace GardenHarvesterOwl;

internal sealed class RoleStore
{
    [Serializable]
    internal struct HouseKey : IEquatable<HouseKey>
    {
        public ushort furnitureId;
        public ushort containerId;

        internal HouseKey(Furniture house)
        {
            furnitureId = house.id.Value;
            containerId = house.helperHouse.container.id.Value;
        }

        public bool Equals(HouseKey other) => furnitureId == other.furnitureId && containerId == other.containerId;
        public override bool Equals(object? obj) => obj is HouseKey key && Equals(key);
        public override int GetHashCode() => (furnitureId << 16) | containerId;
    }

    [Serializable]
    internal struct SavedPlot
    {
        public int bedIndex;
        public ushort seedId;

        internal SavedPlot(int bedIndex, ushort seedId)
        {
            this.bedIndex = bedIndex;
            this.seedId = seedId;
        }
    }

    [Serializable]
    private sealed class SavedRoles
    {
        public int version = 1;
        public string saveFingerprint = "";
        public HouseKey[] houses = Array.Empty<HouseKey>();
        public SavedPlot[] plots = Array.Empty<SavedPlot>();
    }

    private readonly string directory;
    private readonly ManualLogSource log;
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        MaxDepth = 8,
    };

    internal RoleStore(string directory, ManualLogSource log)
    {
        this.directory = directory;
        this.log = log;
    }

    internal HashSet<HouseKey> Load(string slot, string backupDate, bool binary, out SavedPlot[] plots)
    {
        plots = Array.Empty<SavedPlot>();
        try
        {
            string rolesPath = RolesPath(slot);
            string gameSave = SavePath(slot, backupDate, binary);
            if (!File.Exists(rolesPath) || !File.Exists(gameSave))
                return new();
            if (new FileInfo(rolesPath).Length > 64 * 1024)
                throw new InvalidDataException("The owl roles file is larger than expected.");
            SavedRoles? roles = JsonConvert.DeserializeObject<SavedRoles>(File.ReadAllText(rolesPath), JsonSettings);
            if (roles?.version != 1 || roles.saveFingerprint != HashFile(gameSave) || roles.houses == null)
            {
                log.LogInfo("Gardening assignments do not match the loaded save.");
                return new();
            }
            plots = roles.plots ?? Array.Empty<SavedPlot>();
            return new HashSet<HouseKey>(roles.houses.Where(x => x.furnitureId != 0 && x.containerId != 0));
        }
        catch (Exception error)
        {
            log.LogWarning($"Owl role assignments could not be loaded. {error.Message}");
            return new();
        }
    }

    internal void Save(string slot, bool binary, IEnumerable<HouseKey> houses, IEnumerable<SavedPlot> plots)
    {
        string? temporary = null;
        try
        {
            string gameSave = SavePath(slot, "", binary);
            if (!File.Exists(gameSave))
                return;
            Directory.CreateDirectory(directory);
            string rolesPath = RolesPath(slot);
            temporary = rolesPath + ".tmp";
            var roles = new SavedRoles
            {
                saveFingerprint = HashFile(gameSave),
                houses = houses.OrderBy(x => x.furnitureId).ToArray(),
                plots = plots.OrderBy(x => x.bedIndex).ToArray(),
            };
            File.WriteAllText(temporary, JsonConvert.SerializeObject(roles, Formatting.Indented, JsonSettings));
            if (File.Exists(rolesPath))
                File.Replace(temporary, rolesPath, null);
            else
                File.Move(temporary, rolesPath);
            temporary = null;
        }
        catch (Exception error)
        {
            log.LogWarning($"The game saved, but the owl roles file could not be updated. {error.Message}");
        }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private string RolesPath(string slot) => Path.Combine(directory, Hash(Encoding.UTF8.GetBytes(slot.ToUpperInvariant())) + ".json");

    private static string SavePath(string slot, string backupDate, bool binary)
    {
        string basename = backupDate.Length == 0 ? slot : slot + "_" + backupDate;
        if (basename.Length == 0 || basename == "." || basename == ".." || basename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Invalid save slot name.");
        string extension = backupDate.Length == 0
            ? (binary ? ".sav" : ".json") : (binary ? ".bk" : ".jbk");
        return Path.Combine(Application.dataPath, "saves", basename + extension);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(stream));
    }

    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(bytes));
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
