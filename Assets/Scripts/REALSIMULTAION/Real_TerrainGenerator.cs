using UnityEngine;
using System.Collections.Generic;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class Real_TerrainGenerator : MonoBehaviour
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
    public float pathCurvature = 30f; // Reduced slightly for X intersection stability

    [Header("4. Checkpoints & Visuals")]
    public GameObject checkpointPrefab;
    public int checkpointCountPerPath = 10;
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

    [Tooltip("Standard Safety: How much wider than the path should the safe zone be?")]
    [Range(1.0f, 3.0f)] public float rockPathBuffer = 1.8f;

    [Tooltip("Probability (0-1) that a rock can ignore the strict buffer.")]
    [Range(0f, 1f)] public float rockOverflowChance = 0.15f;

    [Tooltip("Overflow Safety: If overflowing, how much closer can it get?")]
    [Range(1.0f, 2.0f)] public float rockOverflowBuffer = 1.1f;

    public string rockTag = "Rocks";

    [Space(10)]
    public GameObject startFlagPrefab;
    public GameObject endFlagPrefab;

    [Space(10)]
    public GameObject roverPrefab;
    // We strictly spawn 2 rovers now (one per path start)

    [Header("6. Layer Management")]
    public string roverLayerName = "Rovers";
    public string checkpointLayerName = "Checkpoints";
    public string markerLayerName = "Markers";
    public string safeLineLayerName = "Markers";

    // --- Internal State ---
    private class PathData
    {
        public Vector2 p0, p1, p2, p3;
        public List<Vector2> segments = new List<Vector2>();
        public List<Vector3> leftBoundaryPts = new List<Vector3>();
        public List<Vector3> rightBoundaryPts = new List<Vector3>();
    }
    
    private List<PathData> paths = new List<PathData>();

    // Containers
    private Transform propContainer;
    private Transform visualContainer;
    
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
    // PUBLIC API
    // ==================================================================================

    // Returns the start position of the FIRST path (Main Path)
    public Vector3 GetStartPosition()
    {
        if (terrain == null || paths.Count == 0) return transform.position;
        Vector3 worldPos = GetWorldPos(paths[0].p0);
        worldPos.y = terrain.SampleHeight(worldPos) + terrain.transform.position.y + 1.0f;
        return worldPos;
    }

    public Quaternion GetStartRotation()
    {
        if (paths.Count == 0) return Quaternion.identity;
        Vector3 start = GetWorldPos(paths[0].p0);
        Vector3 next = GetWorldPos(paths[0].p1);
        Vector3 dir = (next - start).normalized;
        dir.y = 0;
        if (dir == Vector3.zero) return Quaternion.identity;
        return Quaternion.LookRotation(dir);
    }

    public float GetDistanceFromPath(Vector3 roverPos)
    {
        Vector3 local = roverPos - terrain.transform.position;
        Vector2 normPos = new Vector2(local.x / terrainSize, local.z / terrainSize);

        return GetClosestDistanceToAnyPath(normPos) * terrainSize;
    }

    public void Generate(bool spawnPhysical)
    {
        if (terrain == null) return;

        CleanupAll();
        System.Random prng = new System.Random(currentSeed);

        SetupTerrainData();
        CalculateXPaths(prng);
        ApplyHeightmap(prng);
        GeneratePathVisualsAndCheckpoints();

        if (spawnPhysical || Application.isPlaying)
        {
            SpawnRocksAndFlags(prng);
            SpawnRovers(prng);
        }
    }

    // ==================================================================================
    // INTERNAL LOGIC (UPDATED FOR X INTERSECTION)
    // ==================================================================================

    private void CalculateXPaths(System.Random prng)
    {
        paths.Clear();
        float safetyBuffer = (pathWidth * 0.5f + 15f) / terrainSize;
        float margin = 0.1f; // 10% padding from edges

        // --- Path 1: Bottom-Left to Top-Right ---
        PathData path1 = new PathData();
        // Start (Bottom-Left ish)
        path1.p0 = new Vector2(margin, margin); 
        // End (Top-Right ish)
        path1.p3 = new Vector2(1f - margin, 1f - margin);

        // Control points to create slight curvature but maintain crossing
        float curveOffset1 = (float)(prng.NextDouble() - 0.5) * (pathCurvature / terrainSize);
        path1.p1 = Vector2.Lerp(path1.p0, path1.p3, 0.33f) + new Vector2(0, curveOffset1);
        path1.p2 = Vector2.Lerp(path1.p0, path1.p3, 0.66f) - new Vector2(0, curveOffset1);

        GenerateSegments(path1);
        paths.Add(path1);

        // --- Path 2: Top-Left to Bottom-Right ---
        PathData path2 = new PathData();
        // Start (Top-Left ish)
        path2.p0 = new Vector2(margin, 1f - margin);
        // End (Bottom-Right ish)
        path2.p3 = new Vector2(1f - margin, margin);

        // Control points
        float curveOffset2 = (float)(prng.NextDouble() - 0.5) * (pathCurvature / terrainSize);
        path2.p1 = Vector2.Lerp(path2.p0, path2.p3, 0.33f) + new Vector2(curveOffset2, 0);
        path2.p2 = Vector2.Lerp(path2.p0, path2.p3, 0.66f) - new Vector2(curveOffset2, 0);

        GenerateSegments(path2);
        paths.Add(path2);
    }

    private void GenerateSegments(PathData path)
    {
        path.segments.Clear();
        for (int i = 0; i <= 100; i++)
        {
            path.segments.Add(CalculateBezierPoint(i / 100f, path.p0, path.p1, path.p2, path.p3));
        }
    }

    private void SpawnRocksAndFlags(System.Random prng)
    {
        // Flags at Start/End of BOTH paths
        foreach (var path in paths)
        {
            Vector3 startPos = GetWorldPos(path.p0);
            Vector3 startDir = (GetWorldPos(path.p1) - startPos).normalized; startDir.y = 0;

            if (startFlagPrefab)
            {
                GameObject flag = Instantiate(startFlagPrefab, startPos, Quaternion.LookRotation(startDir), propContainer);
                flag.name = "StartFlag";
                AssignLayer(flag, markerLayerName);
            }

            Vector3 endPos = GetWorldPos(path.p3);
            Vector3 endDir = (endPos - GetWorldPos(path.p2)).normalized; endDir.y = 0;

            if (endFlagPrefab)
            {
                GameObject flag = Instantiate(endFlagPrefab, endPos, Quaternion.LookRotation(endDir), propContainer);
                flag.name = "EndFlag";
                AssignLayer(flag, markerLayerName);
            }
        }

        // Rocks
        if (spawnObjects && rockPrefabs != null && rockPrefabs.Count > 0)
        {
            float strictDistNormalized = (pathWidth / terrainSize) * 0.5f * rockPathBuffer;
            float overflowDistNormalized = (pathWidth / terrainSize) * 0.5f * rockOverflowBuffer;

            for (int i = 0; i < rockCount; i++)
            {
                float u = (float)prng.NextDouble();
                float v = (float)prng.NextDouble();

                // Check distance to closest path (Any of the 2)
                float dist = GetClosestDistanceToAnyPath(new Vector2(u, v));
                
                float currentBuffer = strictDistNormalized;
                if (prng.NextDouble() < rockOverflowChance)
                {
                    currentBuffer = overflowDistNormalized;
                }

                if (dist <= currentBuffer) continue; // Too close to either path

                Vector3 pos = GetWorldPos(new Vector2(u, v));
                pos.y -= rockEmbedDepth;

                GameObject prefab = rockPrefabs[prng.Next(rockPrefabs.Count)];
                GameObject rock = Instantiate(prefab, pos, Quaternion.Euler(0, (float)prng.NextDouble() * 360f, 0), propContainer);

                rock.transform.localScale = Vector3.one * Mathf.Lerp(0.5f, 2.0f, (float)prng.NextDouble());

                if (rock.tag == "Untagged")
                {
                    try { rock.tag = rockTag; } catch { }
                }
            }
        }
    }

    private void SpawnRovers(System.Random prng)
    {
        if (!roverPrefab) return;

        GameObject pool = new GameObject("Rover_Pool");
        
        // Spawn 1 rover per path at start
        for (int i = 0; i < paths.Count; i++)
        {
            PathData p = paths[i];
            Vector3 startPos = GetWorldPos(p.p0);
            
            // Calculate rotation facing down the path
            Vector3 nextPos = GetWorldPos(p.segments[1]); // Look a bit ahead
            Vector3 dir = (nextPos - startPos).normalized;
            dir.y = 0;
            Quaternion rot = (dir != Vector3.zero) ? Quaternion.LookRotation(dir) : Quaternion.identity;

            // Lift slightly to avoid clipping
            Vector3 spawnPos = startPos;
            spawnPos.y += 2.0f; 

            GameObject r = Instantiate(roverPrefab, spawnPos, rot, pool.transform);
            r.name = $"Rover_Path_{i+1}";
            AssignLayer(r, roverLayerName);
        }
    }

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

        // Legacy cleanup
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
    }

    private void CleanupRovers()
    {
        DestroyByName("Rover_Pool");
    }

    private LineRenderer CreateLine(string name)
    {
        GameObject obj = new GameObject(name);
        obj.transform.parent = visualContainer;
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

        int pathIndex = 0;
        foreach (var path in paths)
        {
            pathIndex++;
            List<Vector3> leftPts = new List<Vector3>();
            List<Vector3> rightPts = new List<Vector3>();

            int lineSteps = 200;
            for (int i = 0; i <= lineSteps; i++)
            {
                float t = i / (float)lineSteps;
                Vector2 p = CalculateBezierPoint(t, path.p0, path.p1, path.p2, path.p3);
                Vector2 tangent = CalculateBezierTangent(t, path.p0, path.p1, path.p2, path.p3).normalized;
                Vector2 normal = new Vector2(-tangent.y, tangent.x);
                float halfWidth = (pathWidth / terrainSize) * 0.5f;

                Vector2 pLeft = p + normal * halfWidth;
                Vector2 pRight = p - normal * halfWidth;

                Vector3 wL = GetWorldPos(new Vector2(Mathf.Clamp01(pLeft.x), Mathf.Clamp01(pLeft.y)));
                Vector3 wR = GetWorldPos(new Vector2(Mathf.Clamp01(pRight.x), Mathf.Clamp01(pRight.y)));

                leftPts.Add(wL + Vector3.up * lineHoverHeight);
                rightPts.Add(wR + Vector3.up * lineHoverHeight);
            }

            // Create LineRenderers dynamically for this path
            LineRenderer leftLine = CreateLine($"LeftLine_Path{pathIndex}");
            LineRenderer rightLine = CreateLine($"RightLine_Path{pathIndex}");
            UpdateLine(leftLine, leftPts);
            UpdateLine(rightLine, rightPts);

            // Checkpoints
            if (checkpointPrefab != null)
            {
                for (int i = 0; i < checkpointCountPerPath; i++)
                {
                    float t = i / (float)(checkpointCountPerPath - 1);
                    Vector2 p = CalculateBezierPoint(t, path.p0, path.p1, path.p2, path.p3);
                    Vector2 tangent = CalculateBezierTangent(t, path.p0, path.p1, path.p2, path.p3).normalized;
                    Vector3 pos = GetWorldPos(p);

                    GameObject cp = Instantiate(checkpointPrefab, pos, Quaternion.identity, visualContainer);
                    cp.name = $"Checkpoint_P{pathIndex}_{i}";
                    Vector3 tan3D = new Vector3(tangent.x, 0, tangent.y);
                    if (tan3D != Vector3.zero) cp.transform.rotation = Quaternion.LookRotation(tan3D);

                    Vector3 s = cp.transform.localScale;
                    s.x = pathWidth * checkpointWidthMultiplier;
                    s.y = checkpointHeight;
                    cp.transform.localScale = s;
                    AssignLayer(cp, checkpointLayerName);

                    Collider col = cp.GetComponent<Collider>();
                    if (!col) col = cp.AddComponent<BoxCollider>();
                    col.isTrigger = true;

                    ActiveCheckpoints.Add(cp.transform);
                }
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
                
                // Get distance to NEAREST path
                float dist = GetClosestDistanceToAnyPath(new Vector2(u, v));
                
                float flatten = Mathf.InverseLerp(pathRadius, falloff, dist);
                heights[y, x] = Mathf.Lerp(0.2f, h, flatten);
            }
        }
        terrain.terrainData.SetHeights(0, 0, heights);
    }

    public Vector3 GetWorldPos(Vector2 norm) => new Vector3(norm.x * terrainSize, terrain.transform.position.y, norm.y * terrainSize) + terrain.transform.position + new Vector3(0, terrain.SampleHeight(new Vector3(norm.x * terrainSize, 0, norm.y * terrainSize) + terrain.transform.position), 0);
    
    // Bezier math helpers
    private Vector2 CalculateBezierPoint(float t, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3) 
    { 
        float u = 1 - t; 
        return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3; 
    }
    
    private Vector2 CalculateBezierTangent(float t, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3) 
    { 
        float u = 1 - t; 
        return 3 * u * u * (p1 - p0) + 6 * u * t * (p2 - p1) + 3 * t * t * (p3 - p2); 
    }

    private float GetClosestDistanceToAnyPath(Vector2 p)
    {
        float globalMin = float.MaxValue;
        foreach(var path in paths)
        {
            for (int i = 0; i < path.segments.Count - 1; i++)
            {
                float d = DistanceToSegment(p, path.segments[i], path.segments[i + 1]);
                if (d < globalMin) globalMin = d;
            }
        }
        return globalMin;
    }

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