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

internal sealed class PlotStore
{
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
    private sealed class SavedPlots
    {
        public int version = 1;
        public string saveFingerprint = "";
        public SavedPlot[] plots = Array.Empty<SavedPlot>();
    }

    private readonly string directory;
    private readonly ManualLogSource log;
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        MaxDepth = 8,
    };

    internal PlotStore(string directory, ManualLogSource log)
    {
        this.directory = directory;
        this.log = log;
    }

    internal SavedPlot[] Load(string slot, string backupDate, bool binary)
    {
        try
        {
            string plotsPath = PlotsPath(slot);
            string gameSave = SavePath(slot, backupDate, binary);
            if (!File.Exists(plotsPath) || !File.Exists(gameSave))
                return Array.Empty<SavedPlot>();
            if (new FileInfo(plotsPath).Length > 64 * 1024)
                throw new InvalidDataException("The garden plots file is larger than expected.");
            SavedPlots? saved = JsonConvert.DeserializeObject<SavedPlots>(File.ReadAllText(plotsPath), JsonSettings);
            if (saved?.version != 1 || saved.saveFingerprint != HashFile(gameSave))
            {
                log.LogInfo("Pending garden plots do not match the loaded save.");
                return Array.Empty<SavedPlot>();
            }
            return saved.plots ?? Array.Empty<SavedPlot>();
        }
        catch (Exception error)
        {
            log.LogWarning($"Pending garden plots could not be loaded. {error.Message}");
            return Array.Empty<SavedPlot>();
        }
    }

    internal void Save(string slot, bool binary, IEnumerable<SavedPlot> plots)
    {
        string? temporary = null;
        try
        {
            string gameSave = SavePath(slot, "", binary);
            if (!File.Exists(gameSave))
                return;
            Directory.CreateDirectory(directory);
            string plotsPath = PlotsPath(slot);
            temporary = plotsPath + ".tmp";
            var saved = new SavedPlots
            {
                saveFingerprint = HashFile(gameSave),
                plots = plots.OrderBy(x => x.bedIndex).ToArray(),
            };
            File.WriteAllText(temporary, JsonConvert.SerializeObject(saved, Formatting.Indented, JsonSettings));
            if (File.Exists(plotsPath))
                File.Replace(temporary, plotsPath, null);
            else
                File.Move(temporary, plotsPath);
            temporary = null;
        }
        catch (Exception error)
        {
            log.LogWarning($"The game saved, but pending garden plots could not be saved. {error.Message}");
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

    private string PlotsPath(string slot) => Path.Combine(directory, Hash(Encoding.UTF8.GetBytes(slot.ToUpperInvariant())) + ".json");

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
