using UnityEngine;
using System.Collections.Generic;
using System.Linq; 
#if UNITY_EDITOR
using UnityEditor;
#endif

public class TerrainGenerator : MonoBehaviour
{
    [Header("1. Simulation Control")]
    public int currentSeed = 42;
    public bool autoUpdate = false;
    public bool previewObjectsInEditor = false;
    [Tooltip("Resolution (Power of 2). 9 = 513x513.")]
    [Range(5, 12)] public int resolutionScale = 9;

    [Header("2. Landscape")]
    public Terrain terrain;
    public TerrainLayer baseTextureLayer;
    public float terrainSize = 500f;
    public float terrainHeight = 4f;
    public float noiseScale = 50f;

    [Header("3. Path Settings")]
    public float pathLength = 400f;
    public float pathWidth = 30f;
    public float pathFlattenStrength = 15f;
    public float pathCurvature = 50f; 

    [Header("4. Checkpoints & Visuals")]
    public GameObject checkpointPrefab;
    public int checkpointCount = 20;
    public float checkpointWidthMultiplier = 1.2f;
    public float checkpointHeight = 8.0f; 
    public float lineWidth = 0.5f;
    public float lineHoverHeight = 2.0f; 
    public Color safeLineColor = new Color(0, 1, 1, 0.5f);
    
    [Header("5. Spawning")]
    public bool spawnObjects = true;
    public List<GameObject> rockPrefabs;
    public int rockCount = 500;
    public float rockEmbedDepth = 0.5f;
    
    [Tooltip("Standard Safety: How much wider than the path should the safe zone be? (e.g. 1.8)")]
    [Range(1.0f, 3.0f)] public float rockPathBuffer = 1.8f; 

    [Tooltip("Probability (0-1) that a rock can ignore the strict buffer and spawn closer to the edge.")]
    [Range(0f, 1f)] public float rockOverflowChance = 0.15f; 

    [Tooltip("Overflow Safety: If overflowing, how much closer can it get? (e.g. 1.1 = almost touching edge)")]
    [Range(1.0f, 2.0f)] public float rockOverflowBuffer = 1.1f;

    public string rockTag = "Rocks"; 
    
    [Space(10)]
    public GameObject startFlagPrefab;
    public GameObject endFlagPrefab;
    
    [Space(10)]
    public GameObject roverPrefab;
    public int roverSpawnCount = 10;

    [Header("6. Layer Management")]
    [Tooltip("Layer name for spawned Rovers (to hide them from sensors).")]
    public string roverLayerName = "Rovers";
    [Tooltip("Layer name for Checkpoints (to hide them).")]
    public string checkpointLayerName = "Checkpoints";
    [Tooltip("Layer name for Flags/Markers (to hide them).")]
    public string markerLayerName = "Markers";
    [Tooltip("Layer name for Safe Lines (to hide them).")]
    public string safeLineLayerName = "Markers";

    // --- Internal State ---
    private Vector2 p0, p1, p2, p3; 
    private List<Vector2> pathSegments = new List<Vector2>(); 
    
    // Containers
    private Transform propContainer;
    private Transform visualContainer;
    private LineRenderer leftLine, rightLine;
    
    // API
    public List<Transform> ActiveCheckpoints { get; private set; } = new List<Transform>();

    private void Awake()
    {
        if (Application.isPlaying) 
        {
            CleanupAll(); 
            Generate(true);
        }
    }

    private void OnApplicationQuit()
    {
        CleanupAll(); 
    }

    // ==================================================================================
    // PUBLIC API (Required by MoonRoverAgent)
    // ==================================================================================

    public Vector3 GetStartPosition()
    {
        if (terrain == null) return transform.position;
        Vector3 worldPos = GetWorldPos(p0);
        worldPos.y = terrain.SampleHeight(worldPos) + terrain.transform.position.y + 0.5f;
        return worldPos;
    }

    public Quaternion GetStartRotation()
    {
        Vector3 start = GetWorldPos(p0);
        Vector3 next = GetWorldPos(p1);
        Vector3 dir = (next - start).normalized;
        dir.y = 0; 
        if (dir == Vector3.zero) return Quaternion.identity;
        return Quaternion.LookRotation(dir);
    }

    public float GetDistanceFromPath(Vector3 roverPos)
    {
        Vector3 local = roverPos - terrain.transform.position;
        Vector2 normPos = new Vector2(local.x / terrainSize, local.z / terrainSize);

        float minDst = float.MaxValue;
        for (int i = 0; i < pathSegments.Count - 1; i++)
        {
            float dst = DistanceToSegment(normPos, pathSegments[i], pathSegments[i + 1]);
            if (dst < minDst) minDst = dst;
        }
        return minDst * terrainSize;
    }

    public void RegenerateTerrainOnly()
    {
        // Use Unity Random here to pick a NEW seed for the next generation
        currentSeed = Random.Range(0, 999999);
        CleanupEnvironmentOnly();
        
        // From this point on, use the seeded PRNG
        System.Random prng = new System.Random(currentSeed);
        SetupTerrainData();
        CalculateBezierPath(prng);
        ApplyHeightmap(prng);
        GeneratePathVisualsAndCheckpoints();
        SpawnRocksAndFlags(prng);
        
        Debug.Log($"<color=yellow>Terrain Regenerated</color> Seed: {currentSeed}");
    }

    public void Generate(bool spawnPhysical)
    {
        if (terrain == null) return;

        CleanupAll(); 
        System.Random prng = new System.Random(currentSeed);

        SetupTerrainData();
        CalculateBezierPath(prng);
        ApplyHeightmap(prng);
        GeneratePathVisualsAndCheckpoints();

        if (spawnPhysical || Application.isPlaying)
        {
            SpawnRocksAndFlags(prng);
            SpawnRovers(prng);
        }
    }

    // ==================================================================================
    // INTERNAL LOGIC
    // ==================================================================================

    private void CalculateBezierPath(System.Random prng)
    {
        float safetyBuffer = (pathWidth * 0.5f + 15f) / terrainSize;
        float maxLen = terrainSize * (1f - 2f * safetyBuffer);
        float actualLen = Mathf.Min(pathLength, maxLen);
        float margin = Mathf.Max((terrainSize - actualLen) / 2f / terrainSize, safetyBuffer);
        float center = 0.5f;

        float z0 = Mathf.Clamp(center + (float)(prng.NextDouble() * 0.2 - 0.1), safetyBuffer, 1f - safetyBuffer);
        p0 = new Vector2(margin, z0);

        float z3 = Mathf.Clamp(center + (float)(prng.NextDouble() * 0.2 - 0.1), safetyBuffer, 1f - safetyBuffer);
        p3 = new Vector2(1f - margin, z3);

        float curve = pathCurvature / terrainSize;
        bool up = prng.Next(0, 2) == 0;
        float dir = up ? 1f : -1f;

        float x1 = Mathf.Lerp(p0.x, p3.x, 0.33f);
        float x2 = Mathf.Lerp(p0.x, p3.x, 0.66f);
        float z1 = Mathf.Clamp(p0.y + (dir * curve), safetyBuffer, 1f - safetyBuffer);
        float z2 = Mathf.Clamp(p3.y + (dir * curve), safetyBuffer, 1f - safetyBuffer);

        p1 = new Vector2(x1, z1);
        p2 = new Vector2(x2, z2);

        pathSegments.Clear();
        for (int i = 0; i <= 100; i++)
            pathSegments.Add(CalculateBezierPoint(i / 100f));
    }

    private void SpawnRocksAndFlags(System.Random prng)
    {
        Vector3 startPos = GetWorldPos(p0);
        Vector3 startDir = (GetWorldPos(p1) - startPos).normalized; startDir.y = 0;
        
        if(startFlagPrefab) 
        {
            GameObject flag = Instantiate(startFlagPrefab, startPos, Quaternion.LookRotation(startDir), propContainer);
            flag.name = "StartFlag";
            AssignLayer(flag, markerLayerName);
        }

        Vector3 endPos = GetWorldPos(p3);
        Vector3 endDir = (endPos - GetWorldPos(p2)).normalized; endDir.y = 0;
        
        if(endFlagPrefab) 
        {
            GameObject flag = Instantiate(endFlagPrefab, endPos, Quaternion.LookRotation(endDir), propContainer);
            flag.name = "EndFlag";
            AssignLayer(flag, markerLayerName);
        }

        if (spawnObjects && rockPrefabs != null && rockPrefabs.Count > 0)
        {
            float strictDistNormalized = (pathWidth / terrainSize) * 0.5f * rockPathBuffer;
            float overflowDistNormalized = (pathWidth / terrainSize) * 0.5f * rockOverflowBuffer;

            for (int i = 0; i < rockCount; i++)
            {
                float u = (float)prng.NextDouble();
                float v = (float)prng.NextDouble();

                float dist = GetClosestDistanceToPath(new Vector2(u, v));
                float currentBuffer = strictDistNormalized;
                
                if (prng.NextDouble() < rockOverflowChance)
                {
                    currentBuffer = overflowDistNormalized;
                }

                if (dist <= currentBuffer) continue;

                Vector3 pos = GetWorldPos(new Vector2(u, v));
                pos.y -= rockEmbedDepth;

                GameObject prefab = rockPrefabs[prng.Next(rockPrefabs.Count)];
                GameObject rock = Instantiate(prefab, pos, Quaternion.Euler(0, (float)prng.NextDouble() * 360f, 0), propContainer);
                
                rock.transform.localScale = Vector3.one * Mathf.Lerp(0.5f, 2.0f, (float)prng.NextDouble());
                
                if (rock.tag == "Untagged") 
                {
                    try { rock.tag = rockTag; } catch {}
                }
            }
        }
    }

    private void SpawnRovers(System.Random prng)
    {
        if (!roverPrefab || roverSpawnCount <= 0) return;
        
        GameObject pool = new GameObject("Rover_Pool");
        Vector3 startPos = GetWorldPos(p0);
        Quaternion startRot = GetStartRotation();

        for (int i = 0; i < roverSpawnCount; i++)
        {
            Vector3 offset = new Vector3((float)prng.NextDouble() * 10 - 5, 2f, (float)prng.NextDouble() * -10);
            GameObject r = Instantiate(roverPrefab, startPos + offset, startRot, pool.transform);
            r.name = $"Rover_{i}";
            AssignLayer(r, roverLayerName);
        }
    }

    // --- Helpers ---

    private void AssignLayer(GameObject obj, string layerName)
    {
        int layerId = LayerMask.NameToLayer(layerName);
        if (layerId > -1)
        {
            SetLayerRecursively(obj, layerId);
        }
    }

    private void SetLayerRecursively(GameObject obj, int newLayer)
    {
        if (obj == null) return;
        obj.layer = newLayer;
        foreach (Transform child in obj.transform)
        {
            SetLayerRecursively(child.gameObject, newLayer);
        }
    }

    private void CleanupAll() 
    { 
        CleanupEnvironmentOnly(); 
        CleanupRovers(); 
    }

    private void SafeDestroy(GameObject obj)
    {
        if (obj == null) return;
        if (!obj) return; 

        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }

    private void DestroyByName(string name)
    {
        var allObjects = Resources.FindObjectsOfTypeAll<GameObject>()
            .Where(obj => obj.name == name && obj.scene.IsValid()) 
            .ToList();

        foreach (var obj in allObjects)
        {
            SafeDestroy(obj);
        }
    }

    private void CleanupEnvironmentOnly()
    {
        DestroyByName("Props_Container");
        DestroyByName("Visuals_Container");

        if (propContainer) SafeDestroy(propContainer.gameObject);
        if (visualContainer) SafeDestroy(visualContainer.gameObject);

        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (child.name.Contains("Props") || child.name.Contains("Visuals"))
            {
                SafeDestroy(child.gameObject);
            }
        }

        propContainer = new GameObject("Props_Container").transform;
        propContainer.parent = transform;
        visualContainer = new GameObject("Visuals_Container").transform;
        visualContainer.parent = transform;
        
        ActiveCheckpoints.Clear();
        
        leftLine = CreateLine("LeftBoundary");
        rightLine = CreateLine("RightBoundary");
    }

    private void CleanupRovers()
    {
        DestroyByName("Rover_Pool");
    }

    private LineRenderer CreateLine(string name)
    {
        GameObject obj = new GameObject(name);
        obj.transform.parent = visualContainer;
        
        // ASSIGN THE SAFE LINE LAYER
        AssignLayer(obj, safeLineLayerName);

        LineRenderer lr = obj.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.startWidth = lineWidth;
        lr.endWidth = lineWidth;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = safeLineColor;
        lr.endColor = safeLineColor;
        return lr;
    }

    private void SetupTerrainData()
    {
        TerrainData data = terrain.terrainData;
        data.heightmapResolution = (1 << resolutionScale) + 1;
        data.size = new Vector3(terrainSize, terrainHeight, terrainSize);
        if (baseTextureLayer != null) data.terrainLayers = new TerrainLayer[] { baseTextureLayer };
    }

    private void GeneratePathVisualsAndCheckpoints()
    {
        ActiveCheckpoints.Clear();
        
        List<Vector3> leftPts = new List<Vector3>();
        List<Vector3> rightPts = new List<Vector3>();
        
        int lineSteps = 200; 
        for (int i = 0; i <= lineSteps; i++)
        {
            float t = i / (float)lineSteps;
            Vector2 p = CalculateBezierPoint(t);
            Vector2 tangent = CalculateBezierTangent(t).normalized;
            Vector2 normal = new Vector2(-tangent.y, tangent.x);
            float halfWidth = (pathWidth / terrainSize) * 0.5f;

            Vector2 pLeft = p + normal * halfWidth;
            Vector2 pRight = p - normal * halfWidth;
            
            Vector3 wL = GetWorldPos(new Vector2(Mathf.Clamp01(pLeft.x), Mathf.Clamp01(pLeft.y)));
            Vector3 wR = GetWorldPos(new Vector2(Mathf.Clamp01(pRight.x), Mathf.Clamp01(pRight.y)));
            
            leftPts.Add(wL + Vector3.up * lineHoverHeight);
            rightPts.Add(wR + Vector3.up * lineHoverHeight);
        }
        UpdateLine(leftLine, leftPts);
        UpdateLine(rightLine, rightPts);

        if (checkpointPrefab != null)
        {
            for (int i = 0; i < checkpointCount; i++)
            {
                float t = i / (float)(checkpointCount - 1);
                Vector2 p = CalculateBezierPoint(t);
                Vector2 tangent = CalculateBezierTangent(t).normalized;
                Vector3 pos = GetWorldPos(p);
                
                GameObject cp = Instantiate(checkpointPrefab, pos, Quaternion.identity, visualContainer);
                cp.name = $"Checkpoint_{i}";
                Vector3 tan3D = new Vector3(tangent.x, 0, tangent.y);
                if (tan3D != Vector3.zero) cp.transform.rotation = Quaternion.LookRotation(tan3D);
                
                Vector3 s = cp.transform.localScale;
                s.x = pathWidth * checkpointWidthMultiplier;
                s.y = checkpointHeight; 
                cp.transform.localScale = s;

                // Assign Layer
                AssignLayer(cp, checkpointLayerName);

                Collider col = cp.GetComponent<Collider>();
                if (!col) col = cp.AddComponent<BoxCollider>();
                col.isTrigger = true;

                ActiveCheckpoints.Add(cp.transform);
            }
        }
    }

    private void UpdateLine(LineRenderer lr, List<Vector3> pts)
    {
        if (lr == null) return;
        lr.positionCount = pts.Count;
        lr.SetPositions(pts.ToArray());
    }

    private void ApplyHeightmap(System.Random prng)
    {
        int res = terrain.terrainData.heightmapResolution;
        float[,] heights = new float[res, res];
        float offsetX = prng.Next(-10000, 10000);
        float offsetY = prng.Next(-10000, 10000);
        
        float pathRadius = (pathWidth / terrainSize) * 0.5f;
        float falloff = ((pathWidth + pathFlattenStrength) / terrainSize) * 0.5f;

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float u = x / (float)(res - 1);
                float v = y / (float)(res - 1);
                float h = Mathf.PerlinNoise(u * noiseScale + offsetX, v * noiseScale + offsetY);
                float dist = GetClosestDistanceToPath(new Vector2(u, v));
                float flatten = Mathf.InverseLerp(pathRadius, falloff, dist);
                heights[y, x] = Mathf.Lerp(0.2f, h, flatten);
            }
        }
        terrain.terrainData.SetHeights(0, 0, heights);
    }

    public Vector3 GetWorldPos(Vector2 norm) => new Vector3(norm.x * terrainSize, terrain.transform.position.y, norm.y * terrainSize) + terrain.transform.position + new Vector3(0, terrain.SampleHeight(new Vector3(norm.x * terrainSize, 0, norm.y * terrainSize) + terrain.transform.position), 0);
    private Vector2 CalculateBezierPoint(float t) { float u = 1 - t; return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3; }
    private Vector2 CalculateBezierTangent(float t) { float u = 1 - t; return 3 * u * u * (p1 - p0) + 6 * u * t * (p2 - p1) + 3 * t * t * (p3 - p2); }
    private float GetClosestDistanceToPath(Vector2 p) { float m = float.MaxValue; for(int i=0;i<pathSegments.Count-1;i++) m=Mathf.Min(m, DistanceToSegment(p, pathSegments[i], pathSegments[i+1])); return m; }
    private float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b) { Vector2 pa = p - a, ba = b - a; float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba)); return (pa - h * ba).magnitude; }

    void OnValidate()
    {
        if (autoUpdate && !Application.isPlaying && terrain != null)
        {
#if UNITY_EDITOR
            EditorApplication.delayCall += () => { if (this) Generate(previewObjectsInEditor); };
#endif
        }
    }
}