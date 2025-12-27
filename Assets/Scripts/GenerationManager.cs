using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.IO;

public class GenerationManager : MonoBehaviour
{
    [Header("Test Mode Settings")]
    public bool testMode = false;
    public string testFileName = "RoverTestResults";

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
    
    private bool forceReturnToHome = false;

    // Advanced Diagnostics
    private float fps = 0;
    private float maxHitchDetected = 0;
    private float hitchTimer = 0;
    private bool isGpuMode = false;

    // --- TEST METRICS ---
    private StreamWriter logWriter;
    private int currentRunCollisions = 0;
    private float totalPathDeviation = 0f;
    private int deviationSamples = 0;
    private bool currentRunSuccess = false;

    void Start()
    {
        RefreshAgentList();
        isGpuMode = SystemInfo.supportsInstancing;

        if (testMode)
        {
            InitializeTestLog();
        }
    }

    private void InitializeTestLog()
    {
        string path = Path.Combine(Application.dataPath, $"{testFileName}_{System.DateTime.Now:MM-dd-HH-mm}.csv");
        logWriter = new StreamWriter(path, true);
        logWriter.WriteLine("RunID,Seed,TimeTaken,Success,Collisions,AvgDeviation,Result");
        logWriter.Flush();
        Debug.Log($"<color=green>Test Mode Active. Logging to: {path}</color>");
    }

    void OnDestroy()
    {
        if (logWriter != null) logWriter.Close();
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        timer += dt;

        fps = Mathf.Lerp(fps, 1.0f / dt, 0.05f);
        if (dt > 0.1f && timer > 1f) 
        {
            maxHitchDetected = dt;
            hitchTimer = 5f; 
        }
        if (hitchTimer > 0) hitchTimer -= dt;

        // Standard Training Time Limit
        if (!testMode && resetOnTimeLimit && timer >= generationDuration)
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
    
    public void ReportTestMetrics(bool crashed, float currentDeviation)
    {
        if (!testMode) return;
        if (crashed) currentRunCollisions++;
        totalPathDeviation += currentDeviation;
        deviationSamples++;
    }

    public void FailTestRun(string reason)
    {
        if (!testMode) return;
        Debug.Log($"Test Run Failed: {reason}");
        currentRunSuccess = false;
        LogAndRestart();
    }

    public void OnRoverReachedEnd()
    {
        if (testMode)
        {
            currentRunSuccess = true;
            LogAndRestart();
        }
        else
        {
            pendingTerrainReset = true;
            StartNewGeneration();
        }
    }

    private void LogAndRestart()
    {
        if (logWriter != null)
        {
            float avgDev = deviationSamples > 0 ? totalPathDeviation / deviationSamples : 0;
            int seed = terrainGenerator != null ? terrainGenerator.currentSeed : 0;
            
            string line = $"{generationCount},{seed},{timer:F2},{currentRunSuccess},{currentRunCollisions},{avgDev:F2},{(currentRunSuccess ? "COMPLETED" : "FAILED")}";
            logWriter.WriteLine(line);
            logWriter.Flush();
        }

        currentRunCollisions = 0;
        totalPathDeviation = 0;
        deviationSamples = 0;
        currentRunSuccess = false;
        
        pendingTerrainReset = true;
        StartNewGeneration();
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

        bool shouldRegenTerrain = testMode || (!randomizeTerrainOnlyOnSuccess || pendingTerrainReset);

        if (shouldRegenTerrain && terrainGenerator != null)
        {
            // Removed GC.Collect() to prevent crashes
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
                Vector3 offset = Vector3.zero;
                // No random offset in test mode for consistency
                if (!testMode) offset = new Vector3(Random.Range(-2f, 2f), 0.5f, Random.Range(-2f, 2f));
                
                agent.TeleportToStart(startPos + offset, startRot);
            }
        }
    }

    private void ExportBestRoverMap()
    {
        if (currentLeader == null || currentLeader.slamMapper == null) return;
        string json = currentLeader.slamMapper.GetSerializationData();
        string filename = $"RoverMap_Gen{generationCount}_{System.DateTime.Now:MM-dd-HH-mm-ss}.json";
        File.WriteAllText(Path.Combine(Application.dataPath, filename), json);
        Debug.Log($"Map Exported: {filename}");
    }

    void OnGUI()
    {
        GUI.Box(new Rect(10, 10, 300, 350), "<b>ASTROBOT COMMAND</b>");
        
        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.normal.textColor = fps > 30 ? Color.green : Color.red;
        GUI.Label(new Rect(25, 35, 250, 20), $"FPS: {fps:F1}", style);

        GUIStyle modeStyle = new GUIStyle(GUI.skin.label);
        modeStyle.normal.textColor = isGpuMode ? Color.cyan : Color.yellow;
        string modeText = isGpuMode ? "GPU MODE" : "CPU MODE";
        GUI.Label(new Rect(120, 35, 180, 20), modeText, modeStyle);

        if (hitchTimer > 0)
        {
            GUIStyle hitchStyle = new GUIStyle(GUI.skin.label);
            hitchStyle.normal.textColor = Color.red;
            hitchStyle.fontStyle = FontStyle.Bold;
            GUI.Label(new Rect(25, 55, 250, 20), $"[ ! ] LAG SPIKE: {maxHitchDetected:F2}s", hitchStyle);
        }

        if (testMode)
        {
             GUI.Label(new Rect(25, 80, 250, 20), $"<color=orange>--- TEST MODE ACTIVE ---</color>");
             GUI.Label(new Rect(25, 100, 250, 20), $"Run ID: {generationCount}");
             GUI.Label(new Rect(25, 120, 250, 20), $"Collisions: {currentRunCollisions}");
             GUI.Label(new Rect(25, 140, 250, 20), $"Avg Deviation: {(deviationSamples > 0 ? (totalPathDeviation/deviationSamples) : 0):F2}m");
        }
        else
        {
            GUI.Label(new Rect(25, 80, 250, 20), $"Generation: {generationCount}");
            GUI.Label(new Rect(25, 100, 250, 20), $"Next Reset: {(generationDuration - timer):F1}s");
            GUI.Label(new Rect(25, 120, 250, 20), $"Best Score: {bestAllTimeScore:F0}");
            
            if (currentLeader != null)
                GUI.Label(new Rect(25, 145, 250, 20), $"Leader: <color=yellow>{currentLeader.name}</color> ({currentLeader.GetCurrentCheckpoint()} CP)");
                
            string status = (randomizeTerrainOnlyOnSuccess && !pendingTerrainReset) ? "LOCKED" : "READY";
            GUI.Label(new Rect(25, 170, 250, 20), $"Terrain Mode: {status}");
        }

        float startY = 200;
        if (GUI.Button(new Rect(25, startY, 120, 25), "FORCE RESET")) StartNewGeneration();
        if (GUI.Button(new Rect(155, startY, 120, 25), "RE-CACHE")) RefreshAgentList();
        
        forceReturnToHome = GUI.Toggle(new Rect(25, startY + 35, 250, 25), forceReturnToHome, " ENABLE RETURN TO HOME");
        testMode = GUI.Toggle(new Rect(25, startY + 60, 250, 25), testMode, " ENABLE TEST MODE");
        
        GUI.color = Color.cyan;
        if (GUI.Button(new Rect(25, startY + 95, 250, 30), "EXPORT BEST SLAM MAP"))
        {
            ExportBestRoverMap();
        }
        GUI.color = Color.white;
    }
}