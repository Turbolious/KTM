/*
 * =================================================================================
 * KSPTextureCompressor.cs (Modernized & Fixed Version)
 * ---------------------------------------------------------------------------------
 * Fixes:
 * - Properly assigns compressed textures to part sharedMaterials & GameDatabase
 * - Uses Unity's Texture2D.Compress(true) instead of invalid SetPixels on DXT formats
 * - Excludes KerbalTextureManagerCache and UI/Icon folders from GameData scan
 * - Hooks up DXT5nm channel swizzling for normal maps
 * - Enforces multiple-of-4 dimension check required by DXT1/DXT5 block compression
 * - Prevents Out-Of-Memory crashes during Pre-Cache All Textures
 * - Fixes unreadable texture pooling crash and displays FPS/RAM telemetry in UI
 * =================================================================================
 */

using KSP.UI.Screens;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

[KSPAddon(KSPAddon.Startup.MainMenu, true)]
public class KSPTextureCompressor : MonoBehaviour
{
    public static KSPTextureCompressor Instance { get; private set; }
    public OnDemandLoader Loader { get; private set; }
    private ApplicationLauncherButton toolbarButton;
    private bool showWindow = false;
    private Rect windowRect = new Rect(100, 100, 360, 320);

    private bool isPreCachingUI = false;
    private float preCacheProgress = 0f;
    private string preCacheStatus = "";

    // Settings UI state
    private bool showSettings = false;
    private Texture2D gearIcon;
    private Texture2D closeIcon;

    public static string ModFolderPath { get; private set; }

    private float fps = 0f;
    private const float fpsUpdateInterval = 0.5f;
    private float fpsAccum = 0f;
    private int fpsFrames = 0;
    private float fpsTimeLeft = 0.5f;
    private float lastProcessRamMB = 0f;
    private float lastMonoHeapMB = 0f;
    private float lastCpuPercent = 0f;
    private Process currentProcess;
    private DateTime lastCpuSampleTime = DateTime.MinValue;
    private long lastCpuSampleTicks = 0;

    void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        ModFolderPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        Debug.Log("[KerbalTextureManager] Persistent instance created. Waiting for game to be ready...");

        GameEvents.onGUIApplicationLauncherReady.Add(InitializeMod);

        gearIcon = LoadIcon("gear.png", Color.gray);
        closeIcon = LoadIcon("close.png", Color.red);

        fpsTimeLeft = fpsUpdateInterval;
        currentProcess = Process.GetCurrentProcess();
    }

    private Texture2D LoadIcon(string fileName, Color fallback)
    {
        string path = Path.Combine(ModFolderPath, fileName);
        Texture2D tex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
        if (File.Exists(path))
        {
            tex.LoadImage(File.ReadAllBytes(path));
        }
        else
        {
            Color32[] cols = new Color32[16 * 16];
            Color32 fb = fallback;
            for (int i = 0; i < cols.Length; i++) cols[i] = fb;
            tex.SetPixels32(cols);
            tex.Apply();
        }
        return tex;
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
        GameEvents.onGUIApplicationLauncherReady.Remove(InitializeMod);
        if (toolbarButton != null && ApplicationLauncher.Instance != null)
        {
            ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
        }
        if (Loader != null)
        {
            Loader.TearDown();
        }
        if (gearIcon != null) Destroy(gearIcon);
        if (closeIcon != null) Destroy(closeIcon);
    }

    void Update()
    {
        if (!showWindow) return;

        fpsTimeLeft -= Time.unscaledDeltaTime;
        if (Time.unscaledDeltaTime > 0f)
            fpsAccum += 1f / Time.unscaledDeltaTime;
        ++fpsFrames;

        if (fpsTimeLeft <= 0f)
        {
            fps = fpsFrames > 0 ? fpsAccum / fpsFrames : 0f;
            fpsTimeLeft = fpsUpdateInterval;
            fpsAccum = 0f;
            fpsFrames = 0;

            lastMonoHeapMB = GC.GetTotalMemory(false) / (1024f * 1024f);
            try
            {
                currentProcess.Refresh();
                lastProcessRamMB = currentProcess.WorkingSet64 / (1024f * 1024f);
                lastCpuPercent = GetCpuUsage();
            }
            catch
            {
                lastCpuPercent = 0f;
            }
        }
    }

    private float GetCpuUsage()
    {
        var now = DateTime.UtcNow;
        var cpuTime = currentProcess.TotalProcessorTime.Ticks;
        if (lastCpuSampleTime == DateTime.MinValue)
        {
            lastCpuSampleTime = now;
            lastCpuSampleTicks = cpuTime;
            return 0f;
        }
        double interval = (now - lastCpuSampleTime).TotalMilliseconds;
        if (interval <= 0) return lastCpuPercent;

        double cpuUsed = (cpuTime - lastCpuSampleTicks) / (double)TimeSpan.TicksPerMillisecond;
        int cpuCores = Environment.ProcessorCount;
        float percent = (float)((cpuUsed / interval) * 100.0 / cpuCores);
        lastCpuSampleTime = now;
        lastCpuSampleTicks = cpuTime;
        return Mathf.Clamp(percent, 0f, 100f);
    }

    #region UI Methods
    void CreateToolbarButton()
    {
        if (ApplicationLauncher.Instance != null && toolbarButton == null)
        {
            Texture2D buttonTexture = new Texture2D(38, 38, TextureFormat.RGBA32, false);
            string iconPath = Path.Combine(ModFolderPath, "icon.png");

            if (File.Exists(iconPath))
            {
                buttonTexture.LoadImage(File.ReadAllBytes(iconPath));
            }
            else
            {
                Debug.LogWarning("[KerbalTextureManager] icon.png not found. Using fallback color.");
                Color32[] cols = new Color32[38 * 38];
                for (int i = 0; i < cols.Length; i++) cols[i] = Color.cyan;
                buttonTexture.SetPixels32(cols);
                buttonTexture.Apply();
            }

            toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                () => showWindow = true,
                () => showWindow = false,
                null, null, null, null,
                ApplicationLauncher.AppScenes.ALWAYS,
                buttonTexture);
        }
    }

    void OnGUI()
    {
        if (Loader != null && showWindow)
            windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "Kerbal Texture Manager");
    }

    void DrawWindow(int windowID)
    {
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        if (GUILayout.Button(gearIcon, GUILayout.Width(24), GUILayout.Height(24)))
            showSettings = !showSettings;

        if (GUILayout.Button(closeIcon, GUILayout.Width(24), GUILayout.Height(24)))
        {
            showWindow = false;
            if (toolbarButton != null) toolbarButton.SetFalse(false);
        }
        GUILayout.EndHorizontal();

        if (showSettings)
        {
            DrawSettings();
        }
        else
        {
            GUILayout.BeginVertical(GUILayout.Width(340));
            GUILayout.Label($"<b>Telemetry:</b> {fps:F1} FPS | CPU: {lastCpuPercent:F1}%");
            GUILayout.Label($"Process RAM: {lastProcessRamMB:F0} MB | Mono Heap: {lastMonoHeapMB:F0} MB");
            GUILayout.Space(6);

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
                if (GUILayout.Button("Boost (Run Garbage Collection Now)"))
                    StartCoroutine(Loader.RunGarbageCollection(true));
                if (GUILayout.Button("Re-Scan for All Textures"))
                    StartCoroutine(Loader.RescanForTextures());
                if (GUILayout.Button("Pre-Cache All Textures"))
                    StartCoroutine(PreCacheUITask());
                if (GUILayout.Button("Clear Texture Cache"))
                {
                    Loader.ClearCache();
                    ScreenMessages.PostScreenMessage("Cache cleared. Restart KSP.", 5f, ScreenMessageStyle.UPPER_CENTER);
                }
                if (GUILayout.Button("Reload Settings"))
                    Loader.ReloadConfig();
            }
            GUILayout.EndVertical();
        }
        GUI.DragWindow();
    }

    void DrawSettings()
    {
        GUILayout.BeginVertical(GUILayout.Width(320));
        GUILayout.Label("<b>Settings</b>");

        bool downscale = Loader.DownscalingEnabled;
        bool newDownscale = GUILayout.Toggle(downscale, "Downscale large textures to 2048x2048");
        if (newDownscale != downscale)
        {
            Loader.DownscalingEnabled = newDownscale;
            ScreenMessages.PostScreenMessage(
                "Settings changed! Clear cache & Pre-Cache for changes to take effect.",
                6f, ScreenMessageStyle.UPPER_CENTER);
        }

        GUILayout.Space(10);
        if (GUILayout.Button("Back")) showSettings = false;
        GUILayout.EndVertical();
    }

    private IEnumerator PreCacheUITask()
    {
        isPreCachingUI = true;
        preCacheProgress = 0f;
        preCacheStatus = "Starting pre-caching...";
        yield return StartCoroutine(Loader.PreCacheAllTextures((status, progress) =>
        {
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
        barRect.width *= Mathf.Clamp01(val);
        GUI.Box(barRect, "");
    }
    #endregion
}

public class OnDemandLoader
{
    private readonly Dictionary<string, string> managedPartTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> originalTextureSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> loadedTextures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> requiredTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private Coroutine garbageCollector;
    private bool isInitialized = false;
    private bool isPreCaching = false;

    private static readonly string cachePath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KerbalTextureManagerCache");
    private ConfigManager config;

    public bool DownscalingEnabled { get; set; } = false;

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
        if (garbageCollector != null && KSPTextureCompressor.Instance != null)
            KSPTextureCompressor.Instance.StopCoroutine(garbageCollector);

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
            if (originalTextureSizes.TryGetValue(pair.Key, out long orig))
                originalSize += orig;
            if (pair.Value != null)
                compressedSize += Profiler.GetRuntimeMemorySizeLong(pair.Value);
        }
        return Mathf.Max(0f, (originalSize - compressedSize) / (1024f * 1024f));
    }

    public void ClearCache()
    {
        try
        {
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, true);
                Directory.CreateDirectory(cachePath);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[KerbalTextureManager] Failed to clear cache: {e.Message}");
        }
    }

    public void ReloadConfig()
    {
        config = new ConfigManager();
        Debug.Log("[KerbalTextureManager] Config reloaded.");
    }
    #endregion

    #region Event Handlers
    private void OnLevelWasLoaded(GameScenes scene) => UpdateRequiredTextures();
    private void OnEditorShipModified(ShipConstruct ship) => UpdateRequiredTextures();
    private void OnVesselChange(Vessel v) => UpdateRequiredTextures();
    private void OnVesselSwitching(Vessel from, Vessel to) => UpdateRequiredTextures();
    #endregion

    private void UpdateRequiredTextures()
    {
        if (!isInitialized) return;
        requiredTextures.Clear();
        var partsToCheck = new List<Part>();

        if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
        {
            partsToCheck.AddRange(EditorLogic.fetch.ship.Parts);
        }
        else if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
        {
            partsToCheck.AddRange(FlightGlobals.ActiveVessel.parts);
        }

        foreach (Part part in partsToCheck.Distinct())
            AddTexturesForPart(part);
    }

    private void AddTexturesForPart(Part part)
    {
        if (part == null) return;
        foreach (Renderer renderer in part.FindModelComponents<Renderer>())
        {
            if (renderer == null) continue;
            // Use sharedMaterials so we do not instantiate leaked Material copies
            Material[] sharedMats = renderer.sharedMaterials;
            if (sharedMats == null) continue;

            foreach (Material material in sharedMats)
            {
                if (material == null) continue;
                foreach (var texPropName in material.GetTexturePropertyNames())
                {
                    Texture currentTex = material.GetTexture(texPropName);
                    if (currentTex == null || string.IsNullOrEmpty(currentTex.name)) continue;

                    string texKey = currentTex.name.Replace('\\', '/');
                    if (managedPartTextures.ContainsKey(texKey))
                    {
                        requiredTextures.Add(texKey);

                        // Measure uncompressed runtime memory before replacing
                        if (!originalTextureSizes.ContainsKey(texKey) || originalTextureSizes[texKey] < Profiler.GetRuntimeMemorySizeLong(currentTex))
                        {
                            originalTextureSizes[texKey] = Profiler.GetRuntimeMemorySizeLong(currentTex);
                        }

                        Texture2D compressedTex = GetTexture(texKey);
                        if (compressedTex != null && currentTex != compressedTex)
                        {
                            compressedTex.name = currentTex.name;
                            material.SetTexture(texPropName, compressedTex);
                            ReplaceInGameDatabase(texKey, compressedTex);
                        }
                    }
                }
            }
        }
    }

    private void ReplaceInGameDatabase(string texKey, Texture2D newTex)
    {
        if (GameDatabase.Instance == null) return;
        var texInfo = GameDatabase.Instance.GetTextureInfo(texKey);
        if (texInfo != null && texInfo.texture != newTex)
        {
            Texture2D oldTex = texInfo.texture;
            texInfo.texture = newTex;
            if (oldTex != null && !loadedTextures.ContainsValue(oldTex))
            {
                UnityEngine.Object.Destroy(oldTex);
            }
        }
    }

    public Texture2D GetTexture(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string key = name.Replace('\\', '/');
        if (!managedPartTextures.ContainsKey(key)) return null;
        if (loadedTextures.TryGetValue(key, out Texture2D existing) && existing != null)
            return existing;

        Texture2D newTexture = LoadAndCompressTexture(key);
        if (newTexture != null)
        {
            newTexture.name = key;
            loadedTextures[key] = newTexture;
            return newTexture;
        }
        return null;
    }

    private Texture2D LoadAndCompressTexture(string key)
    {
        string filePath = managedPartTextures[key];
        string hashedName = GetMd5Hash(filePath + (DownscalingEnabled ? "_downscaled" : ""));
        string cachedPath = Path.Combine(cachePath, hashedName + ".dds");

        // 1. Load from DDS cache if available
        if (File.Exists(cachedPath))
        {
            try
            {
                byte[] ddsBytes = File.ReadAllBytes(cachedPath);
                if (ddsBytes.Length > 128)
                {
                    var header = ReadDdsHeader(ddsBytes);
                    bool hasMips = header.mipMapCount > 1;
                    Texture2D tex = new Texture2D(header.width, header.height, header.format, hasMips);
                    byte[] pixelData = new byte[ddsBytes.Length - 128];
                    Buffer.BlockCopy(ddsBytes, 128, pixelData, 0, pixelData.Length);
                    if (pixelData.Length > 0)
                    {
                        tex.LoadRawTextureData(pixelData);
                        tex.Apply(false, true);
                        return tex;
                    }
                }
                File.Delete(cachedPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[KerbalTextureManager] Corrupt cache for {key}, rebuilding. ({e.Message})");
                try { File.Delete(cachedPath); } catch { }
            }
        }

        // 2. Load raw PNG/JPG from disk and compress via Unity
        try
        {
            if (!File.Exists(filePath))
            {
                Debug.LogError($"[KerbalTextureManager] Original file not found: {filePath}");
                return null;
            }

            byte[] fileData = File.ReadAllBytes(filePath);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!texture.LoadImage(fileData, false)) // Keep readable so we can inspect/compress
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            if (DownscalingEnabled)
                texture = DownscaleIfLarge(texture);

            // Skip tiny textures or non-multiple-of-4 dimensions (DXT requires multiples of 4)
            if ((texture.width < 128 && texture.height < 128) || (texture.width % 4 != 0) || (texture.height % 4 != 0))
            {
                texture.Apply(true, true);
                return texture;
            }

            bool isNormalMap = config.IsNormalMap(key);
            if (isNormalMap)
            {
                texture = ConvertToDXT5nm(texture);
            }
            else
            {
                bool hasAlpha = TextureHasAlpha(texture);
                if (!hasAlpha)
                {
                    // Convert RGBA32 -> RGB24 so Unity's Compress() selects DXT1 (6:1) instead of DXT5 (4:1)
                    Texture2D rgbTex = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, true);
                    rgbTex.SetPixels32(texture.GetPixels32());
                    rgbTex.Apply(true, false);
                    UnityEngine.Object.Destroy(texture);
                    texture = rgbTex;
                }
                else
                {
                    texture.Apply(true, false);
                }

                // Compress in-place on main thread (RGB24 -> DXT1, RGBA32 -> DXT5)
                texture.Compress(true);
            }

            // Write raw DXT blocks + mipmaps to cache before making non-readable
            WriteDdsFile(cachedPath, texture);
            texture.Apply(false, true);
            return texture;
        }
        catch (Exception e)
        {
            Debug.LogError($"[KerbalTextureManager] Failed to load/compress texture: {key}. Error: {e.Message}");
            return null;
        }
    }

    public IEnumerator BuildTextureWhitelist()
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string texturesDir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");

        // Only target uncompressed PNG/JPG part textures; KSP already loads .dds files as compressed DXT
        var allFiles = Directory.EnumerateFiles(texturesDir, "*.*", SearchOption.AllDirectories)
            .Where(f =>
                (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                 f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) &&
                !f.StartsWith(cachePath, StringComparison.OrdinalIgnoreCase));

        int processed = 0;
        foreach (var file in allFiles)
        {
            string relPath = file.Substring(texturesDir.Length + 1).Replace('\\', '/');
            string relPathNoExt = relPath.Substring(0, relPath.LastIndexOf('.'));

            // Skip common UI, agency, flag, and icon paths to avoid white-box UI corruption
            if (IsUiOrExcludedPath(relPathNoExt)) continue;

            if (!seenNames.Contains(relPathNoExt) && !config.IsBlacklisted(relPathNoExt))
            {
                managedPartTextures[relPathNoExt] = file;
                seenNames.Add(relPathNoExt);
                var fileInfo = new FileInfo(file);
                originalTextureSizes[relPathNoExt] = fileInfo.Length * 4; // Estimate uncompressed RGBA in RAM
            }

            processed++;
            if (processed % 100 == 0)
                yield return null;
        }
        Debug.Log($"[KerbalTextureManager] BuildTextureWhitelist: Managing {managedPartTextures.Count} uncompressed textures.");
    }

    private bool IsUiOrExcludedPath(string relPath)
    {
        string lower = relPath.ToLowerInvariant();
        return lower.Contains("/icons/") ||
               lower.Contains("/flags/") ||
               lower.Contains("/agencies/") ||
               lower.Contains("/ui/") ||
               lower.Contains("toolbar") ||
               lower.EndsWith("/icon");
    }

    public IEnumerator RescanForTextures()
    {
        foreach (var tex in loadedTextures.Values)
        {
            if (tex != null)
                UnityEngine.Object.Destroy(tex);
        }
        loadedTextures.Clear();
        managedPartTextures.Clear();
        originalTextureSizes.Clear();

        yield return KSPTextureCompressor.Instance.StartCoroutine(BuildTextureWhitelist());
        UpdateRequiredTextures();

        Debug.Log("[KerbalTextureManager] Rescan complete. Managed textures: " + managedPartTextures.Count);
    }

    public IEnumerator PreCacheAllTextures(Action<string, float> updateCallback)
    {
        isPreCaching = true;
        var keys = managedPartTextures.Keys.ToList();
        int total = keys.Count;
        int count = 0;

        foreach (var texName in keys)
        {
            updateCallback?.Invoke($"Pre-caching ({count}/{total}): {Path.GetFileName(texName)}", (float)count / Mathf.Max(1, total));

            bool alreadyLoaded = loadedTextures.ContainsKey(texName) && loadedTextures[texName] != null;
            Texture2D tex = LoadAndCompressTexture(texName);

            // If not needed in current scene, destroy immediately so we don't run out of RAM
            if (tex != null && !requiredTextures.Contains(texName) && !alreadyLoaded)
            {
                UnityEngine.Object.Destroy(tex);
            }
            else if (tex != null && requiredTextures.Contains(texName))
            {
                loadedTextures[texName] = tex;
            }

            count++;
            if (count % 5 == 0) yield return null;
        }

        Resources.UnloadUnusedAssets();
        updateCallback?.Invoke("Pre-caching complete.", 1f);
        isPreCaching = false;
    }

    private Texture2D DownscaleIfLarge(Texture2D tex)
    {
        int maxDim = Math.Max(tex.width, tex.height);
        if (maxDim > 2048)
        {
            float scale = 2048f / maxDim;
            // Ensure downscaled dimensions remain multiples of 4 for DXT compression
            int newWidth = Math.Max(4, ((int)(tex.width * scale) / 4) * 4);
            int newHeight = Math.Max(4, ((int)(tex.height * scale) / 4) * 4);

            Texture2D downsized = new Texture2D(newWidth, newHeight, TextureFormat.RGBA32, true);
            Color[] pixels = new Color[newWidth * newHeight];
            for (int y = 0; y < newHeight; y++)
            {
                float v = (y + 0.5f) / newHeight;
                for (int x = 0; x < newWidth; x++)
                {
                    float u = (x + 0.5f) / newWidth;
                    pixels[y * newWidth + x] = tex.GetPixelBilinear(u, v);
                }
            }
            downsized.SetPixels(pixels);
            downsized.Apply(true, false);
            UnityEngine.Object.Destroy(tex);
            return downsized;
        }
        return tex;
    }

    public IEnumerator RunGarbageCollection(bool manualRun = false)
    {
        if (manualRun) Debug.Log("[KerbalTextureManager] Manual GC triggered...");
        List<string> texturesToUnload = loadedTextures.Keys.Where(texName => !requiredTextures.Contains(texName)).ToList();

        if (texturesToUnload.Any())
        {
            if (manualRun)
                ScreenMessages.PostScreenMessage($"Boost: Unloaded {texturesToUnload.Count} unused textures.", 3f, ScreenMessageStyle.UPPER_RIGHT);

            int destroyedCount = 0;
            foreach (string texName in texturesToUnload)
            {
                if (loadedTextures.TryGetValue(texName, out Texture2D tex) && tex != null)
                {
                    UnityEngine.Object.Destroy(tex);
                    destroyedCount++;
                    if (destroyedCount % 10 == 0) yield return null;
                }
                loadedTextures.Remove(texName);
            }
            yield return Resources.UnloadUnusedAssets();
            GC.Collect();
        }
        else if (manualRun)
        {
            ScreenMessages.PostScreenMessage("Boost: No unused textures to unload.", 3f, ScreenMessageStyle.UPPER_RIGHT);
        }

        LogMemoryUsage();
    }

    private void LogMemoryUsage()
    {
        long totalMemory = GC.GetTotalMemory(false);
        Debug.Log($"[KerbalTextureManager] Mono heap: {totalMemory / (1024 * 1024)} MB");
    }

    private IEnumerator GarbageCollectionRoutine()
    {
        var wait = new WaitForSeconds(config.GarbageCollectionInterval);
        yield return wait;
        while (true)
        {
            if (isInitialized && !isPreCaching)
            {
                yield return RunGarbageCollection(false);
            }
            yield return new WaitForSeconds(config.GarbageCollectionInterval);
        }
    }

    private string GetMd5Hash(string input)
    {
        using (MD5 md5Hash = MD5.Create())
        {
            byte[] data = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(input));
            StringBuilder sBuilder = new StringBuilder(32);
            for (int i = 0; i < data.Length; i++) sBuilder.Append(data[i].ToString("x2"));
            return sBuilder.ToString();
        }
    }

    private Texture2D ConvertToDXT5nm(Texture2D source)
    {
        Texture2D dxt5nm = new Texture2D(source.width, source.height, TextureFormat.RGBA32, true);
        Color32[] pixels = source.GetPixels32();
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 p = pixels[i];
            p.a = p.r; // Move X normal to Alpha
            p.r = 255;
            p.b = 255;
            pixels[i] = p;
        }
        dxt5nm.SetPixels32(pixels);
        dxt5nm.Apply(true, false);
        dxt5nm.Compress(true);
        UnityEngine.Object.Destroy(source);
        return dxt5nm;
    }

    private static void WriteDdsFile(string path, Texture2D texture)
    {
        try
        {
            if (texture.format != TextureFormat.DXT1 && texture.format != TextureFormat.DXT5)
                return;

            byte[] textureData = texture.GetRawTextureData();
            byte[] ddsHeader = CreateDdsHeader(texture, textureData.Length);
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                writer.Write(ddsHeader);
                writer.Write(textureData);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[KerbalTextureManager] Error writing DDS file for {path}. Error: {e.Message}");
        }
    }

    private static byte[] CreateDdsHeader(Texture2D texture, int linearSize)
    {
        using (MemoryStream ms = new MemoryStream(128))
        using (BinaryWriter writer = new BinaryWriter(ms))
        {
            uint flags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000;
            if (texture.mipmapCount > 1) flags |= 0x20000; // DDSD_MIPMAPCOUNT

            writer.Write(0x20534444); // "DDS "
            writer.Write(124);        // dwSize
            writer.Write(flags);      // dwFlags
            writer.Write(texture.height);
            writer.Write(texture.width);
            writer.Write(linearSize);
            writer.Write(0);          // dwDepth
            writer.Write(texture.mipmapCount);
            for (int i = 0; i < 11; i++) writer.Write(0); // dwReserved1[11]

            // DDS_PIXELFORMAT
            writer.Write(32);         // dwSize
            writer.Write(0x4);        // DDPF_FOURCC
            if (texture.format == TextureFormat.DXT1)
                writer.Write(0x31545844); // "DXT1"
            else
                writer.Write(0x35545844); // "DXT5"
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            uint caps = 0x1000; // DDSCAPS_TEXTURE
            if (texture.mipmapCount > 1) caps |= 0x8 | 0x400000; // DDSCAPS_COMPLEX | DDSCAPS_MIPMAP
            writer.Write(caps);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            return ms.ToArray();
        }
    }

    private struct DdsHeaderInfo
    {
        public int width;
        public int height;
        public int mipMapCount;
        public TextureFormat format;
    }

    private static DdsHeaderInfo ReadDdsHeader(byte[] ddsBytes)
    {
        return new DdsHeaderInfo
        {
            height = BitConverter.ToInt32(ddsBytes, 12),
            width = BitConverter.ToInt32(ddsBytes, 16),
            mipMapCount = Math.Max(1, BitConverter.ToInt32(ddsBytes, 28)),
            format = BitConverter.ToUInt32(ddsBytes, 84) == 0x31545844 ? TextureFormat.DXT1 : TextureFormat.DXT5
        };
    }

    private bool TextureHasAlpha(Texture2D tex)
    {
        Color32[] pixels = tex.GetPixels32();
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].a < 255) return true;
        }
        return false;
    }
}

public class ConfigManager
{
    public float GarbageCollectionInterval { get; private set; } = 30f;
    private readonly List<string> blacklist = new List<string>();
    private List<string> normalMapSuffixes = new List<string> { "_bump", "_nrm", "_normal" };

    public ConfigManager()
    {
        string configPath = Path.Combine(KSPTextureCompressor.ModFolderPath, "settings.cfg");
        if (!File.Exists(configPath)) return;
        try
        {
            ConfigNode node = ConfigNode.Load(configPath);
            if (node == null) return;

            if (float.TryParse(node.GetValue("garbage_collection_interval"), out float interval) && interval > 0f)
                GarbageCollectionInterval = interval;

            foreach (var blackListEntry in node.GetValues("blacklist"))
            {
                if (!string.IsNullOrWhiteSpace(blackListEntry))
                    blacklist.Add(blackListEntry.Trim());
            }

            if (node.HasValue("normal_map_suffixes"))
            {
                normalMapSuffixes = node.GetValue("normal_map_suffixes")
                    .Split(',')
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }
            Debug.Log($"[KerbalTextureManager] Loaded {blacklist.Count} blacklist entries and {normalMapSuffixes.Count} normal map suffixes.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[KerbalTextureManager] Failed to load settings.cfg: {ex}");
        }
    }

    public bool IsBlacklisted(string textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return false;
        foreach (var entry in blacklist)
        {
            if (textureName.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    public bool IsNormalMap(string textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return false;
        foreach (var suffix in normalMapSuffixes)
        {
            if (textureName.IndexOf(suffix, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }
}
