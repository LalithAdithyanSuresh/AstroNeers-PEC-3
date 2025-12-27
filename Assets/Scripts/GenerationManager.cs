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

    // Advanced Diagnostics
    private float fps = 0;
    private float lastFrameTime;
    private float maxHitchDetected = 0;
    private float hitchTimer = 0;

    void Start()
    {
        RefreshAgentList();
    }

    void Update()
    {
        // We use unscaledDeltaTime to ensure the UI and timer stay 1:1 with your wall clock
        float dt = Time.unscaledDeltaTime;
        timer += dt;

        // FPS & Hitch Detection
        fps = Mathf.Lerp(fps, 1.0f / dt, 0.05f);
        if (dt > 0.1f && timer > 1f) // If a frame takes longer than 100ms
        {
            maxHitchDetected = dt;
            hitchTimer = 5f; // Show the warning for 5 seconds
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

        bool shouldRegenTerrain = !randomizeTerrainOnlyOnSuccess || pendingTerrainReset;

        if (shouldRegenTerrain && terrainGenerator != null)
        {
            System.GC.Collect(); // Clear memory before heavy generation
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
        GUI.Box(new Rect(10, 10, 300, 260), "<b>ASTROBOT COMMAND</b>");
        
        // FPS Meter
        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.normal.textColor = fps > 30 ? Color.green : Color.red;
        GUI.Label(new Rect(25, 35, 250, 20), $"FPS: {fps:F1}", style);

        // HITCH ALERT
        if (hitchTimer > 0)
        {
            GUIStyle hitchStyle = new GUIStyle(GUI.skin.label);
            hitchStyle.normal.textColor = Color.red;
            hitchStyle.fontStyle = FontStyle.Bold;
            GUI.Label(new Rect(120, 35, 200, 20), $"[ ! ] LAG SPIKE: {maxHitchDetected:F2}s", hitchStyle);
        }

        GUI.Label(new Rect(25, 60, 250, 20), $"Generation: {generationCount}");
        GUI.Label(new Rect(25, 80, 250, 20), $"Next Reset: {(generationDuration - timer):F1}s (Wall Clock)");
        GUI.Label(new Rect(25, 100, 250, 20), $"Best Score: {bestAllTimeScore:F0}");
        
        if (currentLeader != null)
            GUI.Label(new Rect(25, 125, 250, 20), $"Leader: <color=yellow>{currentLeader.name}</color> ({currentLeader.GetCurrentCheckpoint()} CP)");

        // Map status
        string status = (randomizeTerrainOnlyOnSuccess && !pendingTerrainReset) ? "LOCKED" : "READY";
        GUI.Label(new Rect(25, 150, 250, 20), $"Terrain Mode: {status}");

        if (GUI.Button(new Rect(25, 180, 120, 25), "FORCE RESET")) StartNewGeneration();
        if (GUI.Button(new Rect(155, 180, 120, 25), "RE-CACHE")) RefreshAgentList();
        
        // --- NEW EXPORT BUTTON ---
        GUI.color = Color.cyan;
        if (GUI.Button(new Rect(25, 220, 250, 30), "EXPORT BEST SLAM MAP"))
        {
            ExportBestRoverMap();
        }
        GUI.color = Color.white;
    }
}