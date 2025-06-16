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
using System.Diagnostics; // Ensure this namespace is included
using Debug = UnityEngine.Debug; // Add this line to resolve ambiguity
using System; // Add this namespace at the top of the file

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

    // Settings UI state
    private bool showSettings = false;
    private Texture2D gearIcon;
    private Texture2D closeIcon;

    public static string ModFolderPath { get; private set; }

    private float fps = 0f;
    private float fpsUpdateInterval = 0.5f;
    private float fpsAccum = 0;
    private int fpsFrames = 0;
    private float fpsTimeLeft = 0.5f;
    private float lastRamMB = 0f;
    private float lastCpuPercent = 0f;
    private System.Diagnostics.Process currentProcess;
    private System.DateTime lastCpuSampleTime = System.DateTime.MinValue;
    private long lastCpuSampleTicks = 0;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        ModFolderPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        UnityEngine.Debug.Log("[KerbalTextureManager] Persistent instance created. Waiting for game to be ready...");

        GameEvents.onGUIApplicationLauncherReady.Add(InitializeMod);

        // Load icons
        gearIcon = LoadIcon("gear.png", Color.gray);
        closeIcon = LoadIcon("close.png", Color.red);

        // Initialize FPS and process
        fpsTimeLeft = fpsUpdateInterval;
        currentProcess = System.Diagnostics.Process.GetCurrentProcess();
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
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    tex.SetPixel(x, y, fallback);
            tex.Apply();
        }
        return tex;
    }

    private void InitializeMod()
    {
        GameEvents.onGUIApplicationLauncherReady.Remove(InitializeMod);

        UnityEngine.Debug.Log("[KerbalTextureManager] Game is ready. Initializing...");
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

    void Update()
    {
        // FPS calculation
        fpsTimeLeft -= Time.unscaledDeltaTime;
        fpsAccum += Time.timeScale / Time.unscaledDeltaTime;
        ++fpsFrames;

        if (fpsTimeLeft <= 0.0)
        {
            fps = fpsAccum / fpsFrames;
            fpsTimeLeft = fpsUpdateInterval;
            fpsAccum = 0;
            fpsFrames = 0;

            // RAM usage (MB)
            lastRamMB = (float)(GC.GetTotalMemory(false) + currentProcess.WorkingSet64) / (1024f * 1024f);

            // CPU usage (approximate, Windows only)
            try
            {
                lastCpuPercent = GetCpuUsage();
            }
            catch { lastCpuPercent = 0f; }

            // GPU usage: Not available in .NET/Unity cross-platform, so show N/A
        }
    }

    private float GetCpuUsage()
    {
        var now = System.DateTime.Now;
        var cpuTime = currentProcess.TotalProcessorTime.Ticks;
        if (lastCpuSampleTime == System.DateTime.MinValue)
        {
            lastCpuSampleTime = now;
            lastCpuSampleTicks = cpuTime;
            return 0f;
        }
        double interval = (now - lastCpuSampleTime).TotalMilliseconds;
        double cpuUsed = (cpuTime - lastCpuSampleTicks) / (double)System.TimeSpan.TicksPerMillisecond;
        int cpuCores = System.Environment.ProcessorCount;
        float percent = (float)((cpuUsed / interval) * 100.0 / cpuCores);
        lastCpuSampleTime = now;
        lastCpuSampleTicks = cpuTime;
        return Mathf.Clamp(percent, 0, 100);
    }

    #region UI Methods
    void CreateToolbarButton()
    {
        if (ApplicationLauncher.Instance != null && toolbarButton == null)
        {
            Texture2D buttonTexture = new Texture2D(38, 38, TextureFormat.RGBA32, false);
            string iconPath = Path.Combine(ModFolderPath, "icon.png");
            UnityEngine.Debug.Log("[KerbalTextureManager] Attempting to load toolbar icon from: " + iconPath);

            if (File.Exists(iconPath))
            {
                byte[] fileData = File.ReadAllBytes(iconPath);
                buttonTexture.LoadImage(fileData);
            }
            else
            {
                UnityEngine.Debug.LogWarning("[KerbalTextureManager] icon.png not found. Using fallback color.");
                for (int y = 0; y < 38; y++) for (int x = 0; x < 38; x++) buttonTexture.SetPixel(x, y, Color.cyan);
                buttonTexture.Apply();
            }

            toolbarButton = ApplicationLauncher.Instance.AddModApplication(() => showWindow = true, () => showWindow = false, null, null, null, null, ApplicationLauncher.AppScenes.ALWAYS, buttonTexture);
        }
    }

    void OnGUI()
    {
        if (Loader != null && showWindow)
            windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "Kerbal Texture Manager");
    }

    void DrawWindow(int windowID)
    {
        // Draw settings and close buttons at top right
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        // Settings (gear) button
        if (GUILayout.Button(gearIcon, GUILayout.Width(24), GUILayout.Height(24)))
        {
            showSettings = !showSettings;
        }
        // Close (X) button
        if (GUILayout.Button(closeIcon, GUILayout.Width(24), GUILayout.Height(24)))
        {
            showWindow = false;
        }
        GUILayout.EndHorizontal();

        if (showSettings)
        {
            DrawSettings();
        }
        else
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
                if (GUILayout.Button("Reload Settings")) { Loader.ReloadConfig(); }
            }
            GUILayout.EndVertical();
        }
        GUI.DragWindow();
    }

    void DrawSettings()
    {
        GUILayout.BeginVertical(GUILayout.Width(300));
        GUILayout.Label("<b>Settings</b>");
        // Downscaling toggle
        bool downscale = Loader.DownscalingEnabled;
        bool newDownscale = GUILayout.Toggle(downscale, "Downscale large textures to 2048x2048");
        bool showSettingsNote = false;
        if (newDownscale != downscale)
        {
            Loader.DownscalingEnabled = newDownscale;
            showSettingsNote = true;
        }
        // Pooling toggle
        bool pooling = Loader.PoolingEnabled;
        bool newPooling = GUILayout.Toggle(pooling, "Enable Texture Pooling (experimental)");
        if (newPooling != pooling)
        {
            Loader.PoolingEnabled = newPooling;
            showSettingsNote = true;
        }
        if (showSettingsNote)
        {
            ScreenMessages.PostScreenMessage(
                "Settings changed! Use 'Re-Scan for All Textures' or 'Pre-Cache All Textures' for changes to take effect.",
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
        GUI.Box(barRect, "");
    }
    #endregion
}

public class OnDemandLoader
{
    // Use full relative path (from GameData, no extension, forward slashes) as key
    private Dictionary<string, string> managedPartTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, long> originalTextureSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Texture2D> loadedTextures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> requiredTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private Coroutine garbageCollector;
    private bool isInitialized = false;
    private bool isPreCaching = false;

    private static readonly string cachePath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KerbalTextureManagerCache");
    private ConfigManager config;

    private long lastLoggedMemory = 0; // Track last memory usage for logging

    // Settings toggles
    public bool DownscalingEnabled { get; set; } = false;
    public bool PoolingEnabled { get; set; } = false;

    // Simple texture pool (experimental)
    private List<Texture2D> texturePool = new List<Texture2D>();

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
        UnityEngine.Debug.Log($"[KerbalTextureManager] Loader is active. Managing {managedPartTextures.Count} textures.");
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
    public float GetLoadedTexturesMemoryMB()
    {
        long total = 0;
        foreach (var tex in loadedTextures.Values)
        {
            if (tex != null)
                total += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);
        }
        return total / (1024f * 1024f);
    }
    public void ClearCache() { try { if (Directory.Exists(cachePath)) { Directory.Delete(cachePath, true); Directory.CreateDirectory(cachePath); } } catch (System.Exception e) { UnityEngine.Debug.LogError($"[KerbalTextureManager] Failed to clear cache: {e.Message}"); } }
    public void ReloadConfig()
    {
        config = new ConfigManager();
        UnityEngine.Debug.Log("[KerbalTextureManager] Config reloaded.");
    }

    public void LogCurrentTextureStatus()
    {
        UnityEngine.Debug.Log("[KerbalTextureManager] --- Texture Status Report ---");
        UnityEngine.Debug.Log($"Managed textures: {managedPartTextures.Count}");
        UnityEngine.Debug.Log($"Loaded textures: {loadedTextures.Count}");
        UnityEngine.Debug.Log($"Required (in-use) textures: {requiredTextures.Count}");

        foreach (var texName in requiredTextures)
        {
            bool isLoaded = loadedTextures.ContainsKey(texName) && loadedTextures[texName] != null;
            string status = isLoaded ? "LOADED" : "NOT LOADED";
            long origSize = originalTextureSizes.ContainsKey(texName) ? originalTextureSizes[texName] : 0;
            long memSize = (isLoaded && loadedTextures[texName] != null) ? UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(loadedTextures[texName]) : 0;
            UnityEngine.Debug.Log($"Texture: {texName} | {status} | Orig: {origSize / 1024} KB | RAM: {memSize / 1024} KB");
        }
        UnityEngine.Debug.Log("[KerbalTextureManager] --- End of Report ---");
    }
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

        // EditorLogic.fetch.ship.parts is only valid in the VAB/SPH
        if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch?.ship != null)
        {
            partsToCheck.AddRange(EditorLogic.fetch.ship.Parts);
        }
        // FlightGlobals.ActiveVessel.parts is only valid in flight
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
            foreach (Material material in renderer.materials)
            {
                if (material == null) continue;
                foreach (var texPropName in material.GetTexturePropertyNames())
                {
                    var texture = material.GetTexture(texPropName);
                    if (texture != null)
                    {
                        string texKey = texture.name.Replace('\\', '/');
                        UnityEngine.Debug.Log($"[KerbalTextureManager] Found texture on part '{part.partInfo?.name ?? part.name}': {texKey}");
                        if (managedPartTextures.ContainsKey(texKey))
                        {
                            requiredTextures.Add(texKey);
                            var loaded = GetTexture(texKey);
                            if (loaded != null)
                                UnityEngine.Debug.Log($"[KerbalTextureManager] Loaded and (possibly) compressed: {texKey}");
                            else
                                UnityEngine.Debug.LogWarning($"[KerbalTextureManager] Failed to load/compress: {texKey}");
                        }
                        else
                        {
                            UnityEngine.Debug.LogWarning($"[KerbalTextureManager] Texture not managed: {texKey}");
                        }
                    }
                }
            }
        }
    }

    public Texture2D GetTexture(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string key = name.Replace('\\', '/');
        if (!managedPartTextures.ContainsKey(key)) return null;
        if (loadedTextures.ContainsKey(key) && loadedTextures[key] != null) return loadedTextures[key];

        Texture2D newTexture = LoadAndCompressTexture(key);
        if (newTexture != null)
        {
            loadedTextures[key] = newTexture;
            return newTexture;
        }
        return null;
    }

    private Texture2D LoadAndCompressTexture(string key)
    {
        string filePath = managedPartTextures[key];
        string hashedName = GetMd5Hash(filePath);
        string cachedPath = Path.Combine(cachePath, hashedName + ".dds");

        // If we already have a cached version, try to use that first
        if (File.Exists(cachedPath))
        {
            try
            {
                byte[] ddsBytes = File.ReadAllBytes(cachedPath);
                if (ddsBytes.Length > 128)
                {
                    var header = ReadDdsHeader(ddsBytes);
                    Texture2D tex = GetPooledTexture(header.width, header.height, header.format, true);
                    byte[] pixelData = new byte[ddsBytes.Length - 128];
                    System.Buffer.BlockCopy(ddsBytes, 128, pixelData, 0, pixelData.Length);
                    if (pixelData.Length > 0) { tex.LoadRawTextureData(pixelData); tex.Apply(false, true); return tex; }
                }
                File.Delete(cachedPath);
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[KerbalTextureManager] Error reading cache for {key}, deleting. Error: {e.Message}");
                File.Delete(cachedPath);
            }
        }

        try
        {
            if (!File.Exists(filePath)) 
            { 
                UnityEngine.Debug.LogError($"[KerbalTextureManager] Original file not found: {filePath}"); 
                return null; 
            }

            // FAST PATH FOR DDS FILES - check file extension
            bool isDDS = filePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
            
            if (isDDS)
            {
                // Load DDS directly using built-in Unity texture loader
                Texture2D ddsTexture = new Texture2D(2, 2);
                byte[] ddsData = File.ReadAllBytes(filePath);
                
                if (ddsTexture.LoadImage(ddsData))
                {
                    UnityEngine.Debug.Log($"[KerbalTextureManager] Using DDS directly: {key}");
                    return ddsTexture;
                }
                UnityEngine.Object.Destroy(ddsTexture);
            }

            // Normal path for PNG/JPG or fallback if DDS direct loading failed
            byte[] fileData = File.ReadAllBytes(filePath);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            texture.LoadImage(fileData, true); // Enable mipmaps

            // DON'T SKIP unreadable textures, just don't try to compress them
            if (!texture.isReadable)
            {
                UnityEngine.Debug.Log($"[KerbalTextureManager] Using unreadable texture as-is: {key}");
                return texture; // Return uncompressed but still usable texture
            }

            if (DownscalingEnabled)
                texture = DownscaleIfLarge(texture);

            if (texture.width < 128 && texture.height < 128) return texture;

            bool isNormalMap = config.IsNormalMap(key);
            bool hasAlpha = TextureHasAlpha(texture);
            TextureFormat targetFormat = hasAlpha || isNormalMap ? TextureFormat.DXT5 : TextureFormat.DXT1;

            Texture2D compressed = new Texture2D(texture.width, texture.height, targetFormat, true);
            compressed.SetPixels(texture.GetPixels());
            compressed.Apply(true, true);

            UnityEngine.Object.Destroy(texture);

            WriteDdsFile(cachedPath, compressed);
            compressed.Apply(false, true);
            return compressed;
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogError($"[KerbalTextureManager] Failed to load/compress texture: {key}. Error: {e.Message}");
            return null;
        }
    }

    public IEnumerator BuildTextureWhitelist()
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string texturesDir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");

        var allFiles = Directory.EnumerateFiles(texturesDir, "*.*", SearchOption.AllDirectories)
            .Where(f =>
                f.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".dds", System.StringComparison.OrdinalIgnoreCase));

        foreach (var file in allFiles)
        {
            // Get relative path from GameData, remove extension, use forward slashes
            string relPath = file.Substring(texturesDir.Length + 1)
                .Replace('\\', '/');
            string relPathNoExt = relPath.Substring(0, relPath.LastIndexOf('.'));

            if (!seenNames.Contains(relPathNoExt) && !config.IsBlacklisted(Path.GetFileNameWithoutExtension(file)))
            {
                managedPartTextures[relPathNoExt] = file;
                seenNames.Add(relPathNoExt);
                var fileInfo = new FileInfo(file);
                originalTextureSizes[relPathNoExt] = fileInfo.Length;
            }
            if (managedPartTextures.Count % 100 == 0)
                yield return null;
        }
        UnityEngine.Debug.Log($"[KerbalTextureManager] BuildTextureWhitelist: Found {managedPartTextures.Count} textures.");
    }

    public IEnumerator RescanForTextures()
    {
        // Clear all loaded textures and managed lists
        foreach (var tex in loadedTextures.Values)
        {
            if (tex != null)
                UnityEngine.Object.Destroy(tex);
        }
        loadedTextures.Clear();
        managedPartTextures.Clear();
        originalTextureSizes.Clear();

        // Rebuild the whitelist
        yield return KSPTextureCompressor.Instance.StartCoroutine(BuildTextureWhitelist());

        // Update required textures for current scene/ship
        UpdateRequiredTextures();

        UnityEngine.Debug.Log("[KerbalTextureManager] Rescan complete. Managed textures: " + managedPartTextures.Count);
    }

    public IEnumerator PreCacheAllTextures(System.Action<string, float> updateCallback)
    {
        isPreCaching = true;
        int total = managedPartTextures.Count;
        int count = 0;

        foreach (var texName in managedPartTextures.Keys.ToList())
        {
            updateCallback?.Invoke($"Pre-caching: {texName}", (float)count / total);
            GetTexture(texName); // This will load and compress the texture into RAM and cache
            count++;
            if (count % 10 == 0) yield return null; // Yield to keep UI responsive
        }

        updateCallback?.Invoke("Pre-caching complete.", 1f);
        isPreCaching = false;
    }

    // Downscale logic
    private Texture2D DownscaleIfLarge(Texture2D tex)
    {
        int maxDim = System.Math.Max(tex.width, tex.height);
        if (maxDim > 2048)
        {
            int newWidth = tex.width > 2048 ? 2048 : tex.width;
            int newHeight = tex.height > 2048 ? 2048 : tex.height;
            Texture2D downsized = GetPooledTexture(newWidth, newHeight, tex.format, true);

            // Manual bilinear downscale
            Color[] pixels = new Color[newWidth * newHeight];
            float ratioX = (float)tex.width / newWidth;
            float ratioY = (float)tex.height / newHeight;
            for (int y = 0; y < newHeight; y++)
            {
                for (int x = 0; x < newWidth; x++)
                {
                    pixels[y * newWidth + x] = tex.GetPixelBilinear((x + 0.5f) / newWidth, (y + 0.5f) / newHeight);
                }
            }
            downsized.SetPixels(pixels);
            downsized.Apply(true, true);
            UnityEngine.Object.Destroy(tex);
            return downsized;
        }
        return tex;
    }

    // Pooling logic
    private Texture2D GetPooledTexture(int width, int height, TextureFormat format, bool mipmap)
    {
        if (PoolingEnabled && texturePool.Count > 0)
        {
            // Find a matching texture in the pool
            for (int i = 0; i < texturePool.Count; i++)
            {
                var pooled = texturePool[i];
                if (pooled.width == width && pooled.height == height && pooled.format == format)
                {
                    texturePool.RemoveAt(i);
                    return pooled;
                }
            }
        }
        return new Texture2D(width, height, format, mipmap);
    }

    // When destroying textures, return to pool if pooling enabled
    public IEnumerator RunGarbageCollection(bool manualRun = false)
    {
        if (manualRun) UnityEngine.Debug.Log("[KerbalTextureManager] Manual GC triggered...");
        List<string> texturesToUnload = loadedTextures.Keys.Where(texName => !requiredTextures.Contains(texName)).ToList();
        if (texturesToUnload.Any())
        {
            if (manualRun) ScreenMessages.PostScreenMessage($"Boost: Unloaded {texturesToUnload.Count} textures.", 3f, ScreenMessageStyle.UPPER_RIGHT);

            int destroyedCount = 0;
            foreach (string texName in texturesToUnload)
            {
                if (loadedTextures.ContainsKey(texName) && loadedTextures[texName] != null)
                {
                    if (PoolingEnabled)
                        texturePool.Add(loadedTextures[texName]);
                    else
                        UnityEngine.Object.Destroy(loadedTextures[texName]);
                    destroyedCount++;
                    if (destroyedCount % 10 == 0) yield return null;
                }
                loadedTextures.Remove(texName);
            }
            Resources.UnloadUnusedAssets();
        }
        else { if (manualRun) ScreenMessages.PostScreenMessage("Boost: No textures to unload.", 3f, ScreenMessageStyle.UPPER_RIGHT); }
        LogMemoryUsage();
        yield return null;
    }

    private void LogMemoryUsage()
    {
        long totalMemory = System.GC.GetTotalMemory(false);
        UnityEngine.Debug.Log($"[KerbalTextureManager] Mono heap: {totalMemory / (1024 * 1024)} MB");
        lastLoggedMemory = totalMemory;
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
        catch (System.Exception e) 
        { 
            UnityEngine.Debug.LogError($"[KerbalTextureManager] Error writing DDS file for {path}. Error: {e.Message}"); 
        }
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

    // Helper method to check if a texture has alpha
    private bool TextureHasAlpha(Texture2D tex)
    {
        var pixels = tex.GetPixels32();
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
    private List<string> blacklist = new List<string>();
    private List<string> normalMapSuffixes = new List<string> { "_bump", "_nrm" };

    public ConfigManager()
    {
        string configPath = Path.Combine(KSPTextureCompressor.ModFolderPath, "settings.cfg");
        if (!File.Exists(configPath)) return;
        try
        {
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
            UnityEngine.Debug.Log($"[KerbalTextureManager] Loaded {blacklist.Count} blacklist entries and {normalMapSuffixes.Count} normal map suffixes.");
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[KerbalTextureManager] Failed to load settings.cfg: {ex}");
        }
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
