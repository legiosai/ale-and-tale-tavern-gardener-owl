using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GardenHarvesterOwl;

internal sealed class GardenerAppearance : MonoBehaviour
{
    private const ushort HoeItem = 302;
    private static readonly string[] ColorMaps = { "_BaseMap", "_BaseColorMap", "_MainTex" };
    private static readonly Dictionary<Texture, Texture2D> GreenTextures = new();
    private Furniture? house;
    private HelperCleaner? owl;
    private Interactive? interaction;
    private Transform? hoe;
    private float swingStart;
    private bool hasHoe;
    private bool initialized;

    private void LateUpdate()
    {
        if (!initialized)
        {
            if (owl != null && house == null && owl.containerRef.containerNet != null)
                house = owl.containerRef.containerNet.GetComponentInParent<Furniture>();
            if (house == null || !house.IsSpawned)
                return;
            if (!GardenerItem.IsGardener(house))
            {
                enabled = false;
                return;
            }
            SetAppearance(house);
            initialized = true;
        }
        if (owl != null)
            MoveHoe(owl.IsServer ? HarvesterWorld.WorkerFor(owl)?.IsHoeing == true : owl.animator.GetBool("Cleanup"));
    }

    private void SetAppearance(Furniture gardenerHouse)
    {
        if (owl == null && gardenerHouse.IsServer && gardenerHouse.helperHouse.container is ContainerNet storage)
            storage.contName.Value = GardenerItem.OwlName;
        if (interaction != null)
            interaction.ObjectTitle = owl != null ? GardenerItem.OwlName : GardenerItem.HouseName;
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (owl == null ? renderer.sharedMaterial?.name == "Owls_Nest1" : renderer.name is "hood_low" or "coat_low")
                Paint(renderer);
        }
    }

    private void Paint(Renderer renderer)
    {
        Material material = renderer.sharedMaterial;
        if (material == null)
            return;
        var paint = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(paint);
        foreach (string map in ColorMaps)
        {
            if (!material.HasProperty(map) || material.GetTexture(map) is not Texture source || source == null)
                continue;
            try
            {
                if (!GreenTextures.TryGetValue(source, out Texture2D recolored))
                {
                    recolored = TintPixels(source, new Rect(0, 0, source.width, source.height), mipMaps: true);
                    GreenTextures.Add(source, recolored);
                }
                paint.SetTexture(map, recolored);
                renderer.SetPropertyBlock(paint);
                return;
            }
            catch (Exception error)
            {
                Debug.LogWarning($"{GardenerItem.OwlName} fabric texture: {error.Message}");
                break;
            }
        }
        string color = material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
        if (material.HasProperty(color))
        {
            paint.SetColor(color, new Color(0.55f, 1f, 0.44f, 1f));
            renderer.SetPropertyBlock(paint);
        }
    }

    private void MoveHoe(bool working)
    {
        if (working && !hasHoe)
        {
            hasHoe = true;
            hoe = MakeHoe();
        }
        if (hoe == null || (!working && !hoe.gameObject.activeSelf))
            return;
        if (working != hoe.gameObject.activeSelf)
        {
            swingStart = Time.time;
            hoe.gameObject.SetActive(working);
        }
        if (working)
        {
            float swing = Mathf.Repeat((Time.time - swingStart) / OwlWorker.HoeSeconds, 1f);
            hoe.localRotation = Quaternion.Euler(Mathf.SmoothStep(-52f, 48f, swing), 0f, -18f);
        }
    }

    private Transform? MakeHoe()
    {
        if (!ItemManager.Instance.GetItemData(HoeItem, out ItemData tool) || tool.tpPrefab == null)
            return null;
        var prop = new GameObject("Gardener Owl Hoe");
        prop.SetActive(false);
        prop.transform.SetParent(transform, false);
        prop.transform.localPosition = new Vector3(0.29f, 0.63f, 0.57f);
        prop.transform.localScale = Vector3.one * 0.8f;
        Transform model = tool.tpPrefab.transform.Find("Model");
        if (model == null)
            return prop.transform;
        foreach (MeshFilter original in model.GetComponentsInChildren<MeshFilter>())
        {
            if (original.sharedMesh == null || !original.TryGetComponent(out MeshRenderer source) || !source.enabled)
                continue;
            var mesh = new GameObject(original.name);
            mesh.transform.SetParent(prop.transform, false);
            mesh.transform.localPosition = model.InverseTransformPoint(original.transform.position);
            mesh.transform.localRotation = Quaternion.Inverse(model.rotation) * original.transform.rotation;
            mesh.transform.localScale = original.transform.localScale;
            mesh.AddComponent<MeshFilter>().sharedMesh = original.sharedMesh;
            mesh.AddComponent<MeshRenderer>().sharedMaterials = source.sharedMaterials;
        }
        return prop.transform;
    }

    internal static Sprite? TintIcon(Sprite? icon)
    {
        if (icon == null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            return icon;
        try
        {
            Texture2D pixels = TintPixels(icon.texture, icon.rect, mipMaps: false);
            return Sprite.Create(pixels, new Rect(0, 0, pixels.width, pixels.height),
                new Vector2(0.5f, 0.5f), icon.pixelsPerUnit);
        }
        catch (Exception error)
        {
            Debug.LogWarning($"{GardenerItem.HouseName} icon: {error.Message}");
            return icon;
        }
    }

    private static Texture2D TintPixels(Texture source, Rect rect, bool mipMaps)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        var recolored = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGBA32, mipMaps);
        try
        {
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            recolored.ReadPixels(rect, 0, 0, false);
            Color32[] pixels = recolored.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                int brightest = Math.Max(p.r, Math.Max(p.g, p.b));
                int detail = (2 * p.r + 4 * p.g + 2 * p.b + 2 * brightest) / 10;
                pixels[i] = new Color32((byte)(12 + detail * 133 / 255), (byte)(26 + detail * 218 / 255),
                    (byte)(12 + detail * 109 / 255), p.a);
            }
            recolored.wrapMode = source.wrapMode;
            recolored.filterMode = source.filterMode;
            recolored.SetPixels32(pixels);
            recolored.Apply(mipMaps, makeNoLongerReadable: true);
            return recolored;
        }
        catch
        {
            Destroy(recolored);
            throw;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
        }
    }

    [HarmonyPatch(typeof(Furniture), nameof(Furniture.OnNetworkSpawn))]
    private static class HouseVisual
    {
        private static void Postfix(Furniture __instance)
        {
            if (!GardenerItem.IsGardener(__instance))
                return;
            GardenerAppearance visual = __instance.gameObject.AddComponent<GardenerAppearance>();
            visual.house = __instance;
            visual.interaction = __instance.helperHouse.interactive;
        }
    }

    [HarmonyPatch(typeof(HelperCleaner), nameof(HelperCleaner.OnNetworkSpawn))]
    private static class OwlVisual
    {
        private static void Postfix(HelperCleaner __instance)
        {
            GardenerAppearance visual = __instance.gameObject.AddComponent<GardenerAppearance>();
            visual.owl = __instance;
            visual.interaction = __instance.interactive;
        }
    }
}
