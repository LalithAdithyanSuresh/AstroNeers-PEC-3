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
    private bool isGpuMode = false;

    void Start()
    {
        RefreshAgentList();
        isGpuMode = SystemInfo.supportsInstancing;

        if (evaluationMode)
        {
            StartNextTestRun();
        }
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

            // Leaderboard Logic (Training Only)
            leaderboardTimer += dt;
            if (leaderboardTimer >= 0.2f)
            {
                UpdateLeaderboard();
                leaderboardTimer = 0;
            }
        }
    }

    public void OnRoverReachedEnd(MoonRoverAgent agent)
    {
        if (evaluationMode)
        {
            RecordTestResult(RunResult.Success, agent);
        }
        else
        {
            pendingTerrainReset = true;
            if (randomizeTerrainOnlyOnSuccess) StartNewGeneration();
        }
    }

    public void OnRoverFailure(MoonRoverAgent agent, RunResult cause)
    {
        if (evaluationMode)
        {
            RecordTestResult(cause, agent);
        }
    }

    private void StartNextTestRun()
    {
        if (currentTestIndex >= totalEvaluationRuns)
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
        if (evaluationMode) DrawEvaluationGUI();
        else DrawTrainingGUI();
    }

    void DrawEvaluationGUI()
    {
        GUI.Box(new Rect(10, 10, 450, 400), "<b>EVALUATION MODE</b>");
        GUIStyle s = new GUIStyle(GUI.skin.label);

        GUI.Label(new Rect(25, 40, 400, 20), $"Run: {currentTestIndex} / {totalEvaluationRuns}");
        GUI.Label(new Rect(25, 60, 400, 20), $"Time: {timer:F1}s / <color=yellow>{currentRunTimeLimit:F1}s</color>");
        GUI.Label(new Rect(25, 80, 400, 20), $"Current Seed: {terrainGenerator.currentSeed}");

        string paramsInfo = $"Size:{terrainGenerator.terrainSize:F0} Width:{terrainGenerator.pathWidth:F0} Curve:{terrainGenerator.pathCurvature:F0}";
        GUI.Label(new Rect(25, 100, 400, 20), paramsInfo);

        GUI.Label(new Rect(25, 130, 400, 20), "<b>Last 5 Results:</b>");

        int start = Mathf.Max(0, testHistory.Count - 5);
        for (int i = start; i < testHistory.Count; i++)
        {
            var r = testHistory[i];
            string color = r.result == RunResult.Success ? "green" : (r.result == RunResult.Timeout ? "yellow" : "red");
            string line = $"#{r.runIndex} | <color={color}>{r.result}</color> | Time: {r.timeTaken:F1}/{r.timeLimit:F0}s | Score: {r.score:F0}";
            GUI.Label(new Rect(25, 150 + (i - start) * 20, 420, 20), line);
        }

        if (testHistory.Count > 0)
        {
            float successRate = (float)testHistory.Count(x => x.result == RunResult.Success) / testHistory.Count * 100f;
            float avgScore = testHistory.Average(x => x.score);
            GUI.Label(new Rect(25, 270, 400, 20), $"<b>Success Rate: {successRate:F1}%</b>");
            GUI.Label(new Rect(25, 290, 400, 20), $"Avg Score: {avgScore:F0}");
        }

        if (isEvaluationFinished)
        {
            GUI.Label(new Rect(25, 330, 400, 30), "<color=cyan>TESTING COMPLETE. CHECK CONSOLE FOR CSV.</color>");
            if (GUI.Button(new Rect(25, 360, 150, 30), "RESTART EVAL"))
            {
                currentTestIndex = 0;
                testHistory.Clear();
                isEvaluationFinished = false;
                StartNextTestRun();
            }
        }
    }

    void DrawTrainingGUI()
    {
        GUI.Box(new Rect(10, 10, 300, 320), "<b>TRAINING MODE</b>");
        GUI.Label(new Rect(25, 35, 250, 20), $"FPS: {fps:F1}");
        GUI.Label(new Rect(25, 60, 250, 20), $"Generation: {generationCount}");
        GUI.Label(new Rect(25, 80, 250, 20), $"Next Reset: {(generationDuration - timer):F1}s");
        GUI.Label(new Rect(25, 100, 250, 20), $"Best Score: {bestAllTimeScore:F0}");

        if (currentLeader != null)
            GUI.Label(new Rect(25, 125, 250, 20), $"Leader: <color=yellow>{currentLeader.name}</color>");

        if (GUI.Button(new Rect(25, 160, 120, 25), "FORCE RESET")) StartNewGeneration();
    }
}