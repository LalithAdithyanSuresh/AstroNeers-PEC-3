using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;

public class GenerationManager : MonoBehaviour
{
    public enum RunResult { Running, Success, Crash, TippedOver, Timeout }

    [System.Serializable]
    public struct TestResult
    {
        public int runIndex;
        public int seed;
        public float terrainDifficulty;
        public RunResult result;
        public float timeTaken;
        public float timeLimit; // Store the limit used for this run
        public float score;
        public float pathWidth;
        public float curvature;
    }

    [Header("--- MODE SELECTION ---")]
    [Tooltip("If TRUE, runs in Evaluation Mode (Randomized Terrains, One Agent, Metrics). If FALSE, runs Training Mode.")]
    public bool evaluationMode = false;

    [Header("Evaluation Settings")]
    public int totalEvaluationRuns = 100;

    [Tooltip("Extra time added to the calculated theoretical travel time.")]
    public float timeBuffer = 60f;

    [Header("Evaluation Randomization Ranges")]
    public Vector2 terrainSizeRange = new Vector2(400, 600);
    public Vector2 pathWidthRange = new Vector2(20, 45);
    public Vector2 curvatureRange = new Vector2(30, 80);
    public Vector2 noiseScaleRange = new Vector2(30, 60);
    public Vector2Int rockCountRange = new Vector2Int(200, 600);

    [Header("Training Settings (Original)")]
    public float generationDuration = 120f;
    public bool resetOnTimeLimit = true;
    public bool randomizeTerrainOnlyOnSuccess = false;

    [Header("Visual Styles")]
    public Material leaderMaterial;
    public Material normalMaterial;

    [Header("References")]
    public TerrainGenerator terrainGenerator;

    // --- Runtime State ---
    private float timer;
    private float leaderboardTimer;
    private int generationCount = 1;
    private bool pendingTerrainReset = false;
    private List<MoonRoverAgent> agents = new List<MoonRoverAgent>();
    private float bestAllTimeScore = 0;
    private MoonRoverAgent currentLeader;

    // Evaluation State
    private int currentTestIndex = 0;
    private float currentRunTimeLimit = 60f; // Calculated per run
    private List<TestResult> testHistory = new List<TestResult>();
    private bool isEvaluationFinished = false;

    // Diagnostics
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
        fps = Mathf.Lerp(fps, 1.0f / dt, 0.05f);

        if (evaluationMode)
        {
            // Evaluation Loop: Check for Timeout using dynamic limit
            if (!isEvaluationFinished && timer >= currentRunTimeLimit)
            {
                RecordTestResult(RunResult.Timeout);
            }
        }
        else
        {
            // Training Loop: Time Limit Reset
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
            isEvaluationFinished = true;
            Debug.Log("<color=green>EVALUATION COMPLETE</color>");
            return;
        }

        currentTestIndex++;
        timer = 0;

        // 1. Randomize Parameters
        float newSize = Random.Range(terrainSizeRange.x, terrainSizeRange.y);
        float newWidth = Random.Range(pathWidthRange.x, pathWidthRange.y);
        float newCurve = Random.Range(curvatureRange.x, curvatureRange.y);
        float newNoise = Random.Range(noiseScaleRange.x, noiseScaleRange.y);
        int newRocks = Random.Range(rockCountRange.x, rockCountRange.y);
        int newSeed = Random.Range(0, 999999);

        // 2. Apply to Terrain
        if (terrainGenerator != null)
        {
            terrainGenerator.SetGenerationParameters(newSize, newWidth, newCurve, newNoise, newRocks);
            terrainGenerator.currentSeed = newSeed;

            System.GC.Collect();
            terrainGenerator.Generate(true);
        }

        // 3. Reset Agent(s) and Calculate Time Limit
        RefreshAgentList();
        Vector3 startPos = terrainGenerator.GetStartPosition();
        Quaternion startRot = terrainGenerator.GetStartRotation();

        MoonRoverAgent activeAgent = null;

        for (int i = 0; i < agents.Count; i++)
        {
            if (agents[i] == null) continue;

            if (i == 0)
            {
                activeAgent = agents[i];
                activeAgent.gameObject.SetActive(true);
                activeAgent.TeleportToStart(startPos, startRot);
                activeAgent.SetBodyMaterial(leaderMaterial);
            }
            else
            {
                agents[i].gameObject.SetActive(false);
            }
        }

        // 4. Calculate Dynamic Time Limit
        if (activeAgent != null && terrainGenerator != null)
        {
            // Time = Distance / Speed
            // We use the generated path length and the rover's max speed
            float theoreticalTime = terrainGenerator.pathLength / activeAgent.maxSpeed;
            currentRunTimeLimit = theoreticalTime + timeBuffer;
        }
        else
        {
            currentRunTimeLimit = timeBuffer; // Fallback
        }
    }

    private void RecordTestResult(RunResult result, MoonRoverAgent agent = null)
    {
        if (isEvaluationFinished) return;

        TestResult r = new TestResult();
        r.runIndex = currentTestIndex;
        r.seed = terrainGenerator.currentSeed;
        r.timeTaken = timer;
        r.timeLimit = currentRunTimeLimit;
        r.result = result;
        r.pathWidth = terrainGenerator.pathWidth;
        r.curvature = terrainGenerator.pathCurvature;

        float difficulty = (100f / r.pathWidth) * (r.curvature / 50f) * (terrainGenerator.rockCount / 200f);
        r.terrainDifficulty = difficulty;

        if (agent != null)
        {
            r.score = agent.GetDistanceMetric();
        }
        else
        {
            r.score = 0;
        }

        testHistory.Add(r);

        Debug.Log($"[CSV],{r.runIndex},{r.seed},{r.result},{r.timeTaken:F2},{r.timeLimit:F2},{r.score:F1},{r.terrainDifficulty:F2}");

        StartNextTestRun();
    }

    public void StartNewGeneration()
    {
        generationCount++;
        timer = 0;
        forceReturnToHome = false;

        bool shouldRegenTerrain = !randomizeTerrainOnlyOnSuccess || pendingTerrainReset;

        if (shouldRegenTerrain && terrainGenerator != null)
        {
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

    private void RefreshAgentList()
    {
        // FindObjectsByType is preferred in Unity 2023+
        agents = FindObjectsByType<MoonRoverAgent>(FindObjectsSortMode.None).ToList();
    }

    private void UpdateLeaderboard()
    {
        if (agents.Count == 0) return;
        var ranked = agents.Where(a => a.gameObject.activeInHierarchy).OrderByDescending(a => a.GetDistanceMetric()).ToList();

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