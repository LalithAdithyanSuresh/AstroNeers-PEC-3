using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.IO;

public class GenerationManager : MonoBehaviour
{
    [Header("Evolution Settings")]
    public float generationDuration = 120f;
    public bool resetOnTimeLimit = true;
    public bool randomizeTerrainOnlyOnSuccess = false; 
    
    [Header("Visual Styles")]
    public Material leaderMaterial;
    public Material normalMaterial;

    [Header("References")]
    public TerrainGenerator terrainGenerator;
    public Transform roverParent; 

    [Header("Performance")]
    public float leaderboardUpdateRate = 5f;

    private float timer;
    private float leaderboardTimer;
    private int generationCount = 1;
    private bool pendingTerrainReset = false;
    private List<MoonRoverAgent> agents = new List<MoonRoverAgent>();
    private float bestAllTimeScore = 0;
    private MoonRoverAgent currentLeader;
    
    // --- NEW: RETURN TO HOME ---
    private bool forceReturnToHome = false;

    // Advanced Diagnostics
    private float fps = 0;
    private float maxHitchDetected = 0;
    private float hitchTimer = 0;
    private bool isGpuMode = false;

    void Start()
    {
        RefreshAgentList();
        // Check GPU Instancing support globally
        isGpuMode = SystemInfo.supportsInstancing;
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        timer += dt;

        // FPS & Hitch Detection
        fps = Mathf.Lerp(fps, 1.0f / dt, 0.05f);
        if (dt > 0.1f && timer > 1f) 
        {
            maxHitchDetected = dt;
            hitchTimer = 5f; 
        }
        if (hitchTimer > 0) hitchTimer -= dt;

        if (resetOnTimeLimit && timer >= generationDuration)
        {
            StartNewGeneration();
        }

        leaderboardTimer += dt;
        if (leaderboardTimer >= (1f / leaderboardUpdateRate))
        {
            UpdateLeaderboard();
            leaderboardTimer = 0;
        }
        
        UpdateReturnToHomeState();
    }
    
    private void UpdateReturnToHomeState()
    {
        foreach(var agent in agents)
        {
            if (agent != null && agent.returnToHomeMode != forceReturnToHome)
            {
                agent.returnToHomeMode = forceReturnToHome;
            }
        }
    }

    public void OnRoverReachedEnd()
    {
        pendingTerrainReset = true;
    }

    private void RefreshAgentList()
    {
        agents = FindObjectsOfType<MoonRoverAgent>().ToList();
    }

    private void UpdateLeaderboard()
    {
        if (agents.Count == 0 || agents.Any(a => a == null)) RefreshAgentList();
        if (agents.Count == 0) return;

        var ranked = agents.OrderByDescending(a => a.GetDistanceMetric()).ToList();
        
        if (ranked.Count > 0)
        {
            MoonRoverAgent newLeader = ranked[0];
            if (newLeader != currentLeader)
            {
                if (currentLeader != null) currentLeader.SetBodyMaterial(normalMaterial);
                newLeader.SetBodyMaterial(leaderMaterial);
                currentLeader = newLeader;
            }
            float topScore = currentLeader.GetDistanceMetric();
            if (topScore > bestAllTimeScore) bestAllTimeScore = topScore;
        }
    }

    public void StartNewGeneration()
    {
        generationCount++;
        timer = 0;
        forceReturnToHome = false;

        bool shouldRegenTerrain = !randomizeTerrainOnlyOnSuccess || pendingTerrainReset;

        if (shouldRegenTerrain && terrainGenerator != null)
        {
            System.GC.Collect(); 
            terrainGenerator.RegenerateTerrainOnly();
            pendingTerrainReset = false; 
        }

        if (terrainGenerator != null)
        {
            Vector3 startPos = terrainGenerator.GetStartPosition();
            Quaternion startRot = terrainGenerator.GetStartRotation();

            foreach (var agent in agents)
            {
                if (agent == null) continue;
                Vector3 offset = new Vector3(Random.Range(-4f, 4f), 2.0f, Random.Range(-4f, 0f));
                agent.TeleportToStart(startPos + offset, startRot);
            }
        }
    }

    private void ExportBestRoverMap()
    {
        if (currentLeader == null || currentLeader.slamMapper == null) 
        {
            Debug.LogWarning("No leader or leader has no SLAM Mapper to export.");
            return;
        }

        string json = currentLeader.slamMapper.GetSerializationData();
        string filename = $"RoverMap_Gen{generationCount}_{System.DateTime.Now:MM-dd-HH-mm-ss}.json";
        string path = Path.Combine(Application.dataPath, filename);
        
        File.WriteAllText(path, json);
        Debug.Log($"<color=green>Map Exported to: {path}</color>");
    }

    void OnGUI()
    {
        GUI.Box(new Rect(10, 10, 300, 320), "<b>ASTROBOT COMMAND</b>");
        
        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.normal.textColor = fps > 30 ? Color.green : Color.red;
        GUI.Label(new Rect(25, 35, 250, 20), $"FPS: {fps:F1}", style);

        // MODE INDICATOR
        GUIStyle modeStyle = new GUIStyle(GUI.skin.label);
        modeStyle.normal.textColor = isGpuMode ? Color.cyan : Color.yellow;
        string modeText = isGpuMode ? "GPU MODE (Instancing)" : "CPU MODE (GameObjects)";
        GUI.Label(new Rect(120, 35, 180, 20), modeText, modeStyle);

        if (hitchTimer > 0)
        {
            GUIStyle hitchStyle = new GUIStyle(GUI.skin.label);
            hitchStyle.normal.textColor = Color.red;
            hitchStyle.fontStyle = FontStyle.Bold;
            GUI.Label(new Rect(25, 55, 250, 20), $"[ ! ] LAG SPIKE: {maxHitchDetected:F2}s", hitchStyle);
        }

        GUI.Label(new Rect(25, 80, 250, 20), $"Generation: {generationCount}");
        GUI.Label(new Rect(25, 100, 250, 20), $"Next Reset: {(generationDuration - timer):F1}s");
        GUI.Label(new Rect(25, 120, 250, 20), $"Best Score: {bestAllTimeScore:F0}");
        
        if (currentLeader != null)
            GUI.Label(new Rect(25, 145, 250, 20), $"Leader: <color=yellow>{currentLeader.name}</color> ({currentLeader.GetCurrentCheckpoint()} CP)");

        string status = (randomizeTerrainOnlyOnSuccess && !pendingTerrainReset) ? "LOCKED" : "READY";
        GUI.Label(new Rect(25, 170, 250, 20), $"Terrain Mode: {status}");

        if (GUI.Button(new Rect(25, 200, 120, 25), "FORCE RESET")) StartNewGeneration();
        if (GUI.Button(new Rect(155, 200, 120, 25), "RE-CACHE")) RefreshAgentList();
        
        forceReturnToHome = GUI.Toggle(new Rect(25, 235, 250, 25), forceReturnToHome, " ENABLE RETURN TO HOME");
        
        GUI.color = Color.cyan;
        if (GUI.Button(new Rect(25, 270, 250, 30), "EXPORT BEST SLAM MAP"))
        {
            ExportBestRoverMap();
        }
        GUI.color = Color.white;
    }
}