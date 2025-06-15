/*
 * =================================================================================
 * KSPTextureCompressor.cs (Final Version)
 * ---------------------------------------------
 * This version incorporates suggestions from the code review, including hashed
 * cache filenames to prevent collisions, smoother garbage collection, and a more
 * powerful configuration system. This is the definitive version of the mod.
 *
 * UPDATE 25: Removed unused 'hasInitialized' variable to resolve compiler warning.
 *
 * --- IMPORTANT COMPILE INSTRUCTIONS ---
 * If you get errors, ensure you have project references to the following:
 * - Assembly-CSharp.dll
 * - UnityEngine.CoreModule.dll
 * - UnityEngine.AnimationModule.dll
 * - UnityEngine.IMGUIModule.dll
 * =================================================================================
 */

using KSP.UI.Screens;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Main entry point for the mod. Manages the OnDemandLoader and the UI.
/// </summary>
[KSPAddon(KSPAddon.Startup.MainMenu, true)]
public class KSPTextureCompressor : MonoBehaviour
{
    public static KSPTextureCompressor Instance { get; private set; }
    public OnDemandLoader Loader { get; private set; }
    private ApplicationLauncherButton toolbarButton;
    private bool showWindow = false;
    private Rect windowRect = new Rect(100, 100, 350, 280);

    private bool isPreCachingUI = false;
    private float preCacheProgress = 0f;
    private string preCacheStatus = "";

    public static string ModFolderPath { get; private set; }

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        ModFolderPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        Debug.Log("[KerbalTextureManager] Persistent instance created. Waiting for game to be ready...");

        GameEvents.onGUIApplicationLauncherReady.Add(InitializeMod);
    }

    private void InitializeMod()
    {
        GameEvents.onGUIApplicationLauncherReady.Remove(InitializeMod);

        Debug.Log("[KerbalTextureManager] Game is ready. Initializing...");
        Loader = new OnDemandLoader();
        StartCoroutine(Loader.Initialize());

        CreateToolbarButton();
    }

    void OnDestroy()
    {
        if (toolbarButton != null && ApplicationLauncher.Instance != null)
        {
            ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
        }
        if (Loader != null)
        {
            Loader.TearDown();
        }
    }

    #region UI Methods
    void CreateToolbarButton()
    {
        if (ApplicationLauncher.Instance != null && toolbarButton == null)
        {
            Texture2D buttonTexture = new Texture2D(38, 38, TextureFormat.RGBA32, false);
            string iconPath = Path.Combine(ModFolderPath, "icon.png");
            Debug.Log("[KerbalTextureManager] Attempting to load toolbar icon from: " + iconPath);

            if (File.Exists(iconPath))
            {
                byte[] fileData = File.ReadAllBytes(iconPath);
                buttonTexture.LoadImage(fileData);
            }
            else
            {
                Debug.LogWarning("[KerbalTextureManager] icon.png not found. Using fallback color.");
                for (int y = 0; y < 38; y++) for (int x = 0; x < 38; x++) buttonTexture.SetPixel(x, y, Color.cyan);
                buttonTexture.Apply();
            }

            toolbarButton = ApplicationLauncher.Instance.AddModApplication(() => showWindow = true, () => showWindow = false, null, null, null, null, ApplicationLauncher.AppScenes.ALWAYS, buttonTexture);
        }
    }

    void OnGUI() { if (Loader != null && showWindow) windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "Kerbal Texture Manager"); }

    void DrawWindow(int windowID)
    {
        GUILayout.BeginVertical(GUILayout.Width(330));
        GUILayout.Label("Managed Part Textures: " + Loader.GetManagedTextureCount());
        GUILayout.Label("Textures Currently in RAM: " + Loader.GetLoadedTextureCount());
        GUILayout.Label($"Est. Memory Saved: {Loader.GetEstimatedMemorySavedMB():F2} MB");
        GUILayout.Space(10);

        if (isPreCachingUI)
        {
            GUILayout.Label(preCacheStatus);
            DrawProgressBar(preCacheProgress);
        }
        else
        {
            if (GUILayout.Button("Boost (Run Garbage Collection Now)")) { StartCoroutine(Loader.RunGarbageCollection(true)); }
            if (GUILayout.Button("Re-Scan for All Textures")) { StartCoroutine(Loader.RescanForTextures()); }
            if (GUILayout.Button("Pre-Cache All Textures")) { StartCoroutine(PreCacheUITask()); }
            if (GUILayout.Button("Clear Texture Cache")) { Loader.ClearCache(); ScreenMessages.PostScreenMessage("Cache cleared. Restart KSP.", 5f, ScreenMessageStyle.UPPER_CENTER); }
        }
        GUILayout.EndVertical();
        GUI.DragWindow();
    }

    private IEnumerator PreCacheUITask()
    {
        isPreCachingUI = true;
        preCacheProgress = 0f;
        preCacheStatus = "Starting pre-caching...";
        yield return StartCoroutine(Loader.PreCacheAllTextures((status, progress) => {
            preCacheStatus = status;
            preCacheProgress = progress;
        }));
        isPreCachingUI = false;
    }

    void DrawProgressBar(float val)
    {
        Rect rect = GUILayoutUtility.GetRect(18, 18, "TextField");
        GUI.Box(rect, "");
        Rect barRect = new Rect(rect);
        barRect.width *= val;
        GUI.Box(barRect, "", "WhiteLabel");
    }
    #endregion
}

public class OnDemandLoader
{
    private Dictionary<string, string> managedPartTextures = new Dictionary<string, string>();
    private Dictionary<string, long> originalTextureSizes = new Dictionary<string, long>();
    private Dictionary<string, Texture2D> loadedTextures = new Dictionary<string, Texture2D>();
    private HashSet<string> requiredTextures = new HashSet<string>();
    private Coroutine garbageCollector;
    private bool isInitialized = false;
    private bool isPreCaching = false;

    private static readonly string cachePath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KerbalTextureManagerCache");
    private ConfigManager config;

    public IEnumerator Initialize()
    {
        config = new ConfigManager();
        if (!Directory.Exists(cachePath)) Directory.CreateDirectory(cachePath);

        yield return KSPTextureCompressor.Instance.StartCoroutine(BuildTextureWhitelist());

        GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
        GameEvents.onEditorShipModified.Add(OnEditorShipModified);
        GameEvents.onVesselCreate.Add(OnVesselChange);
        GameEvents.onVesselLoaded.Add(OnVesselChange);
        GameEvents.onVesselSwitching.Add(OnVesselSwitching);

        isInitialized = true;
        Debug.Log($"[KerbalTextureManager] Loader is active. Managing {managedPartTextures.Count} textures.");
        garbageCollector = KSPTextureCompressor.Instance.StartCoroutine(GarbageCollectionRoutine());
    }

    public void TearDown()
    {
        GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
        GameEvents.onEditorShipModified.Remove(OnEditorShipModified);
        GameEvents.onVesselCreate.Remove(OnVesselChange);
        GameEvents.onVesselLoaded.Remove(OnVesselChange);
        GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
    }

    #region Public Interface
    public int GetManagedTextureCount() => managedPartTextures.Count;
    public int GetLoadedTextureCount() => loadedTextures.Count;
    public float GetEstimatedMemorySavedMB()
    {
        long originalSize = 0;
        long compressedSize = 0;
        foreach (var pair in loadedTextures)
        {
            if (originalTextureSizes.ContainsKey(pair.Key)) originalSize += originalTextureSizes[pair.Key];
            if (pair.Value != null) compressedSize += Profiler.GetRuntimeMemorySizeLong(pair.Value);
        }
        return (originalSize - compressedSize) / 1024f / 1024f;
    }
    public void ClearCache() { try { if (Directory.Exists(cachePath)) { Directory.Delete(cachePath, true); Directory.CreateDirectory(cachePath); } } catch (System.Exception e) { Debug.LogError($"[KerbalTextureManager] Failed to clear cache: {e.Message}"); } }
    #endregion

    #region Event Handlers
    private void OnLevelWasLoaded(GameScenes scene) { UpdateRequiredTextures(); }
    private void OnEditorShipModified(ShipConstruct ship) { UpdateRequiredTextures(); }
    private void OnVesselChange(Vessel v) { UpdateRequiredTextures(); }
    private void OnVesselSwitching(Vessel from, Vessel to) { UpdateRequiredTextures(); }
    #endregion

    private void UpdateRequiredTextures()
    {
        if (!isInitialized) return;
        requiredTextures.Clear();
        var partsToCheck = new List<Part>();
        if (FlightGlobals.ActiveVessel != null) partsToCheck.AddRange(FlightGlobals.ActiveVessel.parts);
        if (EditorLogic.fetch?.ship != null) partsToCheck.AddRange(EditorLogic.fetch.ship.parts);
        foreach (Part part in partsToCheck.Distinct()) AddTexturesForPart(part);
    }

    private void AddTexturesForPart(Part part)
    {
        if (part == null) return;
        foreach (Renderer renderer in part.FindModelComponents<Renderer>())
        {
            if (renderer == null) continue;
            foreach (Material material in renderer.materials)
            {
                if (material == null) continue;
                foreach (var texPropName in material.GetTexturePropertyNames())
                {
                    var texture = material.GetTexture(texPropName);
                    if (texture != null && managedPartTextures.ContainsKey(texture.name))
                    {
                        requiredTextures.Add(texture.name);
                        GetTexture(texture.name);
                    }
                }
            }
        }
    }

    public Texture2D GetTexture(string name)
    {
        if (string.IsNullOrEmpty(name) || !managedPartTextures.ContainsKey(name)) return null;
        if (loadedTextures.ContainsKey(name) && loadedTextures[name] != null) return loadedTextures[name];

        Texture2D newTexture = LoadAndCompressTexture(name);
        if (newTexture != null)
        {
            if (loadedTextures.ContainsKey(name)) loadedTextures[name] = newTexture;
            else loadedTextures.Add(name, newTexture);
            return newTexture;
        }
        return null;
    }

    private Texture2D LoadAndCompressTexture(string name)
    {
        string filePath = managedPartTextures[name];
        string hashedName = GetMd5Hash(filePath);
        string cachedPath = Path.Combine(cachePath, hashedName + ".dds");

        if (File.Exists(cachedPath))
        {
            try
            {
                byte[] ddsBytes = File.ReadAllBytes(cachedPath);
                if (ddsBytes.Length > 128)
                {
                    var header = ReadDdsHeader(ddsBytes);
                    Texture2D tex = new Texture2D(header.width, header.height, header.format, false);
                    byte[] pixelData = new byte[ddsBytes.Length - 128];
                    System.Buffer.BlockCopy(ddsBytes, 128, pixelData, 0, pixelData.Length);
                    if (pixelData.Length > 0) { tex.LoadRawTextureData(pixelData); tex.Apply(false, true); return tex; }
                }
                File.Delete(cachedPath);
            }
            catch (System.Exception e) { Debug.LogError($"[KerbalTextureManager] Error reading cache for {name}, deleting. Error: {e.Message}"); File.Delete(cachedPath); }
        }

        try
        {
            if (!File.Exists(filePath)) { Debug.LogError($"[KerbalTextureManager] Original file not found: {filePath}"); return null; }

            byte[] fileData = File.ReadAllBytes(filePath);
            Texture2D texture = new Texture2D(2, 2);
            texture.LoadImage(fileData);

            if (texture.width < 128 && texture.height < 128) return texture;

            bool isNormalMap = config.IsNormalMap(name);
            if (isNormalMap) { texture = ConvertToDXT5nm(texture); }
            else { texture.Compress(true); }

            WriteDdsFile(cachedPath, texture);
            texture.Apply(false, true);
            return texture;
        }
        catch (System.Exception e) { Debug.LogError($"[KerbalTextureManager] Failed to load/compress texture: {name}. Error: {e.Message}"); return null; }
    }

    #region Routines and Helpers

    public IEnumerator BuildTextureWhitelist()
    {
        Debug.Log("[KerbalTextureManager] Building part texture whitelist from .cfg files...");
        managedPartTextures.Clear();
        originalTextureSizes.Clear();

        foreach (AvailablePart partInfo in PartLoader.LoadedPartsList)
        {
            if (partInfo?.partConfig == null) continue;

            ConfigNode[] modelNodes = partInfo.partConfig.GetNodes("MODEL");
            foreach (ConfigNode node in modelNodes)
            {
                if (node == null) continue;

                string[] textureValues = node.GetValues("texture");
                foreach (string val in textureValues)
                {
                    string textureName = val.Split(',')[0].Trim();
                    string textureDirectory = val.Split(',')[1].Trim();

                    if (config.IsBlacklisted(textureName)) continue;

                    var texInfo = GameDatabase.Instance.GetTexture(textureDirectory + "/" + textureName, false);
                    if (texInfo != null)
                    {
                        var fileInfo = GameDatabase.Instance.GetTextureInfo(texInfo.name);
                        if (fileInfo != null && !managedPartTextures.ContainsKey(texInfo.name))
                        {
                            managedPartTextures.Add(texInfo.name, fileInfo.file.fullPath);
                            originalTextureSizes.Add(texInfo.name, new FileInfo(fileInfo.file.fullPath).Length);
                        }
                    }
                }
            }
            yield return null;
        }
    }

    public IEnumerator RescanForTextures()
    {
        Debug.Log("[KerbalTextureManager] Re-scanning for textures...");
        int initialCount = managedPartTextures.Count;
        yield return KSPTextureCompressor.Instance.StartCoroutine(BuildTextureWhitelist());
        int newCount = managedPartTextures.Count - initialCount;
        Debug.Log($"[KerbalTextureManager] Re-scan complete. Found {newCount} new textures.");
        ScreenMessages.PostScreenMessage($"Re-scan found {newCount} new textures to manage.", 5f, ScreenMessageStyle.UPPER_CENTER);
    }

    public IEnumerator PreCacheAllTextures(System.Action<string, float> updateCallback)
    {
        isPreCaching = true;
        Debug.Log("[KerbalTextureManager] Starting pre-caching...");
        var textureNames = managedPartTextures.Keys.ToList();
        for (int i = 0; i < textureNames.Count; i++)
        {
            GetTexture(textureNames[i]);
            float progress = (float)(i + 1) / textureNames.Count;
            updateCallback($"Caching: {i + 1}/{textureNames.Count}", progress);
            if (i % 5 == 0) yield return null;
        }
        updateCallback("Pre-caching complete!", 1f);
        ScreenMessages.PostScreenMessage("Pre-caching complete!", 5f, ScreenMessageStyle.UPPER_CENTER);
        isPreCaching = false;
    }

    public IEnumerator RunGarbageCollection(bool manualRun = false)
    {
        if (manualRun) Debug.Log("[KerbalTextureManager] Manual GC triggered...");
        List<string> texturesToUnload = loadedTextures.Keys.Where(texName => !requiredTextures.Contains(texName)).ToList();
        if (texturesToUnload.Any())
        {
            if (manualRun) ScreenMessages.PostScreenMessage($"Boost: Unloaded {texturesToUnload.Count} textures.", 3f, ScreenMessageStyle.UPPER_RIGHT);

            int destroyedCount = 0;
            foreach (string texName in texturesToUnload)
            {
                if (loadedTextures.ContainsKey(texName) && loadedTextures[texName] != null)
                {
                    UnityEngine.Object.Destroy(loadedTextures[texName]);
                    destroyedCount++;
                    if (destroyedCount % 10 == 0) yield return null;
                }
                loadedTextures.Remove(texName);
            }
        }
        else { if (manualRun) ScreenMessages.PostScreenMessage("Boost: No textures to unload.", 3f, ScreenMessageStyle.UPPER_RIGHT); }
        yield return null;
    }

    private IEnumerator GarbageCollectionRoutine()
    {
        yield return new WaitForSeconds(config.GarbageCollectionInterval);
        while (true)
        {
            if (isInitialized && !isPreCaching) { yield return RunGarbageCollection(); }
            yield return new WaitForSeconds(config.GarbageCollectionInterval);
        }
    }

    private string GetMd5Hash(string input)
    {
        using (MD5 md5Hash = MD5.Create())
        {
            byte[] data = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(input));
            StringBuilder sBuilder = new StringBuilder();
            for (int i = 0; i < data.Length; i++) { sBuilder.Append(data[i].ToString("x2")); }
            return sBuilder.ToString();
        }
    }

    private Texture2D ConvertToDXT5nm(Texture2D source)
    {
        Texture2D dxt5nm = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        Color32[] pixels = source.GetPixels32();
        for (int i = 0; i < pixels.Length; i++) { Color32 p = pixels[i]; p.r = p.a; p.a = p.g; pixels[i] = p; }
        dxt5nm.SetPixels32(pixels);
        dxt5nm.Apply();
        dxt5nm.Compress(true);
        UnityEngine.Object.Destroy(source);
        return dxt5nm;
    }

    private static void WriteDdsFile(string path, Texture2D texture)
    {
        try
        {
            byte[] textureData = texture.GetRawTextureData();
            byte[] ddsHeader = CreateDdsHeader(texture, textureData.Length);
            using (FileStream fs = new FileStream(path, FileMode.Create))
            using (BinaryWriter writer = new BinaryWriter(fs)) { writer.Write(ddsHeader); writer.Write(textureData); }
        }
        catch (System.Exception e) { Debug.LogError($"[KerbalTextureManager] Error writing DDS file for {path}. Error: {e.Message}"); }
    }

    private static byte[] CreateDdsHeader(Texture2D texture, int linearSize)
    {
        using (MemoryStream ms = new MemoryStream(128))
        {
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write(0x20534444); writer.Write(124); writer.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x80000);
                writer.Write(texture.height); writer.Write(texture.width); writer.Write(linearSize); writer.Write(0); writer.Write(0);
                for (int i = 0; i < 11; i++) writer.Write(0);
                writer.Write(32); writer.Write(0x4);
                if (texture.format == TextureFormat.DXT1) writer.Write(0x31545844); else if (texture.format == TextureFormat.DXT5) writer.Write(0x35545844); else writer.Write(0);
                writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                writer.Write(0x1000); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            }
            return ms.ToArray();
        }
    }

    private struct DdsHeaderInfo { public int width; public int height; public TextureFormat format; }
    private static DdsHeaderInfo ReadDdsHeader(byte[] ddsBytes) { return new DdsHeaderInfo { height = System.BitConverter.ToInt32(ddsBytes, 12), width = System.BitConverter.ToInt32(ddsBytes, 16), format = System.BitConverter.ToUInt32(ddsBytes, 84) == 0x31545844 ? TextureFormat.DXT1 : TextureFormat.DXT5 }; }
    #endregion
}

public class ConfigManager
{
    public float GarbageCollectionInterval { get; private set; } = 30f;
    private List<string> blacklist = new List<string>();
    private List<string> normalMapSuffixes = new List<string> { "_bump", "_nrm" };

    public ConfigManager()
    {
        string configPath = Path.Combine(KSPTextureCompressor.ModFolderPath, "settings.cfg");
        if (!File.Exists(configPath)) return;
        ConfigNode node = ConfigNode.Load(configPath);
        if (node == null) return;

        float.TryParse(node.GetValue("garbage_collection_interval"), out float interval);
        GarbageCollectionInterval = interval > 0 ? interval : 30f;

        foreach (var blackListEntry in node.GetValues("blacklist"))
        {
            blacklist.Add(blackListEntry.Trim());
        }

        if (node.HasValue("normal_map_suffixes"))
        {
            normalMapSuffixes = node.GetValue("normal_map_suffixes").Split(',').Select(s => s.Trim()).ToList();
        }
        Debug.Log($"[KerbalTextureManager] Loaded {blacklist.Count} blacklist entries and {normalMapSuffixes.Count} normal map suffixes.");
    }

    public bool IsBlacklisted(string textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return false;
        foreach (var entry in blacklist) { if (textureName.ToLower().Contains(entry.ToLower())) return true; }
        return false;
    }

    public bool IsNormalMap(string textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return false;
        string lowerName = textureName.ToLower();
        foreach (var suffix in normalMapSuffixes) { if (lowerName.Contains(suffix)) return true; }
        return false;
    }
}
