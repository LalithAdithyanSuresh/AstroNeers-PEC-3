using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Linq;

[RequireComponent(typeof(MoonRoverAgent))]
public class VoxelSlamMapper : MonoBehaviour
{
    [Header("Debug Visualization")]
    public bool showLidarGizmos = false;
    public bool showTrajectory = true;
    public bool showCalculatedPath = true;
    public bool showExplorationTarget = true;
    public bool showLandmarks = true;

    [Header("Performance Mode")]
    [Tooltip("If TRUE, system will auto-detect GPU capabilities on Start.")]
    public bool autoDetectGPU = true;
    
    [Tooltip("CRITICAL: False = GPU Instancing (Fast). True = GameObjects (Slow).")]
    public bool useRealGameObjects = false; 

    [Tooltip("How many frames to wait between Lidar scans. Higher = Better FPS.")]
    [Range(1, 10)] public int scanThrottleFrames = 3;

    [Header("3D Map Settings")]
    public float granularity = 0.5f; 
    public Material obstacleMaterial; 
    public Material visitedGroundMaterial; 
    public Material landmarkMaterial; 
    
    [Header("Landmark Visualization")]
    [Tooltip("Mesh to use for Landmarks (e.g., Sphere). Defaults to Cube if null.")]
    public Mesh landmarkMesh; 
    [Tooltip("Multiplier for the size of landmarks relative to grid granularity.")]
    public float landmarkScaleMultiplier = 2.0f; 

    [Header("Fallback Settings")]
    public GameObject voxelPrefab; 
    public GameObject landmarkPrefab; // Prefab for "Real GameObject" mode

    [Header("Layer Settings")]
    public int mapLayer = 9; 
    public LayerMask obstacleMask; 

    [Header("Lidar Overrides")]
    [Range(1f, 90f)]
    public float verticalScanAngle = 15f; 

    [Header("Pathfinding")]
    public int maxPathIterations = 500; 
    
    // --- SLAM DATA ---
    public Vector3 originPosition { get; private set; }
    
    private float maxDistanceFromOrigin = 0f;
    private Vector3Int furthestVoxel = Vector3Int.zero;

    private HashSet<Vector3Int> occupiedVoxels = new HashSet<Vector3Int>(); 
    private HashSet<Vector3Int> visitedGroundVoxels = new HashSet<Vector3Int>(); 
    private HashSet<Vector3Int> landmarkVoxels = new HashSet<Vector3Int>(); 
    
    private List<Vector3> trajectory = new List<Vector3>();
    private List<Vector3> keyLocations = new List<Vector3>(); 
    
    private List<Vector3> currentCalculatedPath = new List<Vector3>();

    // GPU Instancing Storage
    private List<Matrix4x4[]> obstacleBatches = new List<Matrix4x4[]>();
    private List<Matrix4x4> currentObstacleBatch = new List<Matrix4x4>();
    
    private List<Matrix4x4[]> groundBatches = new List<Matrix4x4[]>();
    private List<Matrix4x4> currentGroundBatch = new List<Matrix4x4>();

    private List<Matrix4x4[]> landmarkBatches = new List<Matrix4x4[]>();
    private List<Matrix4x4> currentLandmarkBatch = new List<Matrix4x4>();

    // CPU Storage
    private Transform mapContainer;
    private MoonRoverAgent agent;
    private Mesh cubeMesh; 

    [System.Serializable]
    public class SlamMapData
    {
        public string roverName;
        public Vector3 origin;
        public List<Vector3> trajectory;
        public List<Vector3> obstacles;
        public List<Vector3> safeGround;
        public List<Vector3> keyLocations;
        public List<Vector3> landmarks;
        public float finalScore;
    }

    void Awake()
    {
        if (autoDetectGPU)
        {
            if (SystemInfo.supportsInstancing)
            {
                useRealGameObjects = false;
                Debug.Log($"<color=green>[VoxelSlam] GPU Instancing Supported. Switched to GPU Mode.</color>");
            }
            else
            {
                useRealGameObjects = true;
                Debug.LogWarning($"<color=orange>[VoxelSlam] GPU Instancing NOT Supported. Falling back to CPU GameObjects.</color>");
            }
        }
    }

    void Start()
    {
        agent = GetComponent<MoonRoverAgent>();
        InitializeContainer();
        
        // Default Cube Mesh
        GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cubeMesh = temp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(temp);

        // Default Landmark Mesh if not assigned
        if (landmarkMesh == null)
        {
             GameObject tempSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
             landmarkMesh = tempSphere.GetComponent<MeshFilter>().sharedMesh;
             Destroy(tempSphere);
        }

        if (obstacleMaterial) obstacleMaterial.enableInstancing = true;
        if (visitedGroundMaterial) visitedGroundMaterial.enableInstancing = true;
        if (landmarkMaterial) landmarkMaterial.enableInstancing = true;
    }

    private void InitializeContainer()
    {
        if (mapContainer == null)
        {
            GameObject go = new GameObject($"{this.name}_Map_Container");
            mapContainer = go.transform;
        }
    }

    void FixedUpdate()
    {
        // PERFORMANCE: Only scan every N frames
        if (Time.frameCount % scanThrottleFrames == 0)
        {
            Perform3DLidarScan();
            UpdateTrajectory();
        }
    }

    void Update()
    {
        if (!useRealGameObjects)
        {
            // Render Obstacles & Ground (Standard Cube)
            RenderGPU(obstacleBatches, currentObstacleBatch, obstacleMaterial, cubeMesh);
            RenderGPU(groundBatches, currentGroundBatch, visitedGroundMaterial, cubeMesh);
            
            // Render Landmarks (Custom Mesh & Logic)
            RenderGPU(landmarkBatches, currentLandmarkBatch, landmarkMaterial, landmarkMesh);
        }
    }

    public void SetOrigin(Vector3 pos)
    {
        originPosition = pos;
        ResetMap();
        AddKeyLocation(pos);
    }

    public void ResetMap()
    {
        occupiedVoxels.Clear();
        visitedGroundVoxels.Clear();
        landmarkVoxels.Clear();
        trajectory.Clear();
        keyLocations.Clear();
        currentCalculatedPath.Clear();

        maxDistanceFromOrigin = 0f;
        furthestVoxel = Vector3Int.zero;

        obstacleBatches.Clear();
        currentObstacleBatch.Clear();
        groundBatches.Clear();
        currentGroundBatch.Clear();
        landmarkBatches.Clear();
        currentLandmarkBatch.Clear();

        if (mapContainer != null)
        {
            foreach (Transform child in mapContainer) Destroy(child.gameObject);
        }
    }

    public void AddKeyLocation(Vector3 pos)
    {
        keyLocations.Add(pos);
    }

    // ==================================================================================
    // LANDMARK REGISTRATION (Updated)
    // ==================================================================================
    public void RegisterLandmark(Vector3 pos, string label)
    {
        int x = Mathf.FloorToInt(pos.x / granularity);
        int y = Mathf.FloorToInt(pos.y / granularity);
        int z = Mathf.FloorToInt(pos.z / granularity);
        Vector3Int coord = new Vector3Int(x, y, z);

        if (!landmarkVoxels.Contains(coord))
        {
            landmarkVoxels.Add(coord);
            // Center the visual exactly on the grid
            Vector3 centerPos = new Vector3(x, y, z) * granularity + (Vector3.one * (granularity * 0.5f));

            if (useRealGameObjects) 
            {
                SpawnBlock(centerPos, 2); // 2 = Landmark Type
            }
            else 
            {
                // Scale is multiplied here for the matrix
                Matrix4x4 mat = Matrix4x4.TRS(centerPos, Quaternion.identity, Vector3.one * granularity * landmarkScaleMultiplier);
                AddToRenderBatch(mat, currentLandmarkBatch, landmarkBatches);
            }
        }
    }

    private void UpdateTrajectory()
    {
        if (trajectory.Count == 0 || Vector3.Distance(trajectory[trajectory.Count - 1], transform.position) > 0.5f)
        {
            trajectory.Add(transform.position);
        }
    }

    public void ScanTerrainSurface(Terrain terrain)
    {
        if (Time.frameCount % scanThrottleFrames != 0) return;

        if (terrain == null) return;
        int radiusSteps = 2; 
        float checkStep = granularity;

        for (int x = -radiusSteps; x <= radiusSteps; x++)
        {
            for (int z = -radiusSteps; z <= radiusSteps; z++)
            {
                Vector3 checkPos = transform.position + (transform.right * x * checkStep) + (transform.forward * z * checkStep);
                float terrainHeight = terrain.SampleHeight(checkPos) + terrain.transform.position.y;
                
                if (Mathf.Abs(checkPos.y - terrainHeight) < 2.0f)
                {
                    Vector3 surfacePoint = new Vector3(checkPos.x, terrainHeight, checkPos.z);
                    RegisterVoxel(surfacePoint, false); 
                }
            }
        }
    }

    // ==================================================================================
    // LIDAR & CORE (Updated for Landmark Identification)
    // ==================================================================================

    private void Perform3DLidarScan()
    {
        if (agent == null || agent.lidarSensors == null) return;
        float maxDist = agent.lidarRayLength;
        float hAngleTotal = agent.lidarRayDegrees;
        int hRays = (agent.lidarRaysPerDirection * 2) + 1;
        float hStep = hRays > 1 ? (hAngleTotal * 2) / (hRays - 1) : 0;
        
        float vAngleTotal = verticalScanAngle;
        int vRays = (agent.lidarVerticalRaysPerDirection * 2) + 1;
        float vStep = vRays > 1 ? (vAngleTotal * 2) / (vRays - 1) : 0;

        foreach (var sensorObj in agent.lidarSensors)
        {
            if (!sensorObj) continue;
            Vector3 origin = sensorObj.transform.position;
            Quaternion rot = sensorObj.transform.rotation;
            
            for (int v = 0; v < vRays; v++)
            {
                float vAngle = -vAngleTotal + (v * vStep);
                Quaternion vRot = Quaternion.AngleAxis(vAngle, Vector3.right); 
                for (int h = 0; h < hRays; h++)
                {
                    float hAngle = -hAngleTotal + (h * hStep);
                    Quaternion hRot = Quaternion.AngleAxis(hAngle, Vector3.up);
                    Vector3 dir = rot * hRot * vRot * Vector3.forward;
                    
                    if (Physics.Raycast(origin, dir, out RaycastHit hit, maxDist, obstacleMask))
                    {
                        // --- IDENTIFICATION LOGIC ---
                        // "Cheat" using Tags to identify if this voxel is a specific landmark
                        if (hit.collider.CompareTag(agent.obstacleTag))
                        {
                            RegisterLandmark(hit.point, "Rock");
                        }
                        else
                        {
                            // Standard Obstacle (e.g., Walls, Debris)
                            RegisterVoxel(hit.point, true);
                        }
                    }
                }
            }
        }
    }

    private void RegisterVoxel(Vector3 pos, bool isObstacle)
    {
        int x = Mathf.FloorToInt(pos.x / granularity);
        int y = Mathf.FloorToInt(pos.y / granularity);
        int z = Mathf.FloorToInt(pos.z / granularity);
        Vector3Int coord = new Vector3Int(x, y, z);

        HashSet<Vector3Int> targetSet = isObstacle ? occupiedVoxels : visitedGroundVoxels;
        if (isObstacle && visitedGroundVoxels.Contains(coord)) return; 
        if (!isObstacle && occupiedVoxels.Contains(coord)) return;

        // Don't overwrite existing landmarks with generic obstacles
        if (landmarkVoxels.Contains(coord)) return;

        if (!targetSet.Contains(coord))
        {
            targetSet.Add(coord);
            Vector3 centerPos = new Vector3(x, y, z) * granularity + (Vector3.one * (granularity * 0.5f));

            if (useRealGameObjects) 
            {
                SpawnBlock(centerPos, isObstacle ? 1 : 0);
            }
            else 
            {
                // GPU INSTANCING
                Matrix4x4 mat = Matrix4x4.TRS(centerPos, Quaternion.identity, Vector3.one * granularity);
                if (isObstacle) AddToRenderBatch(mat, currentObstacleBatch, obstacleBatches);
                else AddToRenderBatch(mat, currentGroundBatch, groundBatches);
            }
        
            if (!isObstacle)
            {
                float distSq = (pos - originPosition).sqrMagnitude;
                if (distSq > maxDistanceFromOrigin)
                {
                    maxDistanceFromOrigin = distSq;
                    furthestVoxel = coord;
                }
            }
        }
    }

    // type: 0 = Safe, 1 = Obstacle, 2 = Landmark
    private void SpawnBlock(Vector3 pos, int type)
    {
        InitializeContainer();
        GameObject block;
        
        // Handle custom prefabs
        if (type == 2 && landmarkPrefab != null) block = Instantiate(landmarkPrefab, pos, Quaternion.identity, mapContainer);
        else if (voxelPrefab != null) block = Instantiate(voxelPrefab, pos, Quaternion.identity, mapContainer);
        else 
        { 
            // Primitives
            PrimitiveType pType = (type == 2) ? PrimitiveType.Sphere : PrimitiveType.Cube;
            block = GameObject.CreatePrimitive(pType); 
            block.transform.position = pos; 
            block.transform.parent = mapContainer; 
        }
        
        // Scale logic
        float scale = granularity;
        if (type == 2) scale *= landmarkScaleMultiplier;
        
        block.transform.localScale = Vector3.one * scale;
        block.layer = mapLayer; 
        
        Material matToUse = visitedGroundMaterial; // Default
        if (type == 1) matToUse = obstacleMaterial;
        if (type == 2) matToUse = landmarkMaterial;

        if (matToUse != null) { var rend = block.GetComponent<Renderer>(); if (rend) rend.material = matToUse; }
        
        Collider col = block.GetComponent<Collider>();
        if (col) Destroy(col);
        block.SetActive(true);
    }

    private void AddToRenderBatch(Matrix4x4 mat, List<Matrix4x4> currentList, List<Matrix4x4[]> batchList)
    {
        currentList.Add(mat);
        if (currentList.Count >= 1023) { batchList.Add(currentList.ToArray()); currentList.Clear(); }
    }

    // Updated RenderGPU to accept specific Mesh
    private void RenderGPU(List<Matrix4x4[]> batches, List<Matrix4x4> current, Material mat, Mesh meshToDraw)
    {
        if (mat == null || meshToDraw == null) return;
        foreach (var batch in batches) Graphics.DrawMeshInstanced(meshToDraw, 0, mat, batch, batch.Length, null, UnityEngine.Rendering.ShadowCastingMode.Off, true, mapLayer);
        if (current.Count > 0) Graphics.DrawMeshInstanced(meshToDraw, 0, mat, current, null, UnityEngine.Rendering.ShadowCastingMode.Off, true, mapLayer);
    }

    // ==================================================================================
    // PATHFINDING & UTILS
    // ==================================================================================

    public Vector3 GetNextPathPoint(Vector3 currentPos)
    {
        if (currentCalculatedPath == null || currentCalculatedPath.Count == 0) return Vector3.zero;

        while(currentCalculatedPath.Count > 0 && (currentPos - currentCalculatedPath[0]).sqrMagnitude < 6.25f) 
        {
            currentCalculatedPath.RemoveAt(0);
        }

        if (currentCalculatedPath.Count > 0) return currentCalculatedPath[0];
        return Vector3.zero;
    }

    public void RecalculateExplorationPath(Vector3 startPos)
    {
        if (visitedGroundVoxels.Count == 0 || furthestVoxel == Vector3Int.zero) return;
        Vector3 targetPos = GridToWorld(furthestVoxel);
        currentCalculatedPath = FindPathAStar(startPos, targetPos);
    }

    public void RecalculatePath(Vector3 startPos, Vector3 targetPos)
    {
        currentCalculatedPath = FindPathAStar(startPos, targetPos);
    }

    private List<Vector3> FindPathAStar(Vector3 start, Vector3 end)
    {
        Vector3Int startNode = WorldToGrid(start);
        Vector3Int targetNode = WorldToGrid(end);

        if (!visitedGroundVoxels.Contains(startNode)) startNode = FindClosestVisited(startNode);
        if (!visitedGroundVoxels.Contains(targetNode)) targetNode = FindClosestVisited(targetNode);
        
        if (startNode == targetNode) return new List<Vector3>();

        HashSet<Vector3Int> closedSet = new HashSet<Vector3Int>();
        Dictionary<Vector3Int, Vector3Int> cameFrom = new Dictionary<Vector3Int, Vector3Int>();
        Dictionary<Vector3Int, float> gScore = new Dictionary<Vector3Int, float>();
        List<Vector3Int> openSet = new List<Vector3Int>(); 
        
        openSet.Add(startNode);
        gScore[startNode] = 0;

        Dictionary<Vector3Int, float> fScore = new Dictionary<Vector3Int, float>();
        fScore[startNode] = Vector3.Distance(GridToWorld(startNode), GridToWorld(targetNode));

        int iterations = 0;

        while (openSet.Count > 0)
        {
            if (iterations++ > maxPathIterations) break; 

            openSet.Sort((a, b) => {
                float fa = fScore.ContainsKey(a) ? fScore[a] : float.MaxValue;
                float fb = fScore.ContainsKey(b) ? fScore[b] : float.MaxValue;
                return fa.CompareTo(fb);
            });

            Vector3Int current = openSet[0];
            
            if (current == targetNode) return ReconstructPath(cameFrom, current);

            openSet.RemoveAt(0);
            closedSet.Add(current);

            foreach (Vector3Int neighbor in GetNeighbors(current))
            {
                if (closedSet.Contains(neighbor)) continue;
                if (occupiedVoxels.Contains(neighbor)) continue; 
                // Treat Landmarks as obstacles for navigation
                if (landmarkVoxels.Contains(neighbor)) continue;
                if (!visitedGroundVoxels.Contains(neighbor)) continue; 

                float tentativeG = gScore[current] + 1.0f; 

                if (!openSet.Contains(neighbor)) openSet.Add(neighbor);
                else if (tentativeG >= (gScore.ContainsKey(neighbor) ? gScore[neighbor] : float.MaxValue)) continue;

                cameFrom[neighbor] = current;
                gScore[neighbor] = tentativeG;
                fScore[neighbor] = gScore[neighbor] + Vector3.Distance(GridToWorld(neighbor), GridToWorld(targetNode));
            }
        }

        return new List<Vector3>();
    }

    private List<Vector3> ReconstructPath(Dictionary<Vector3Int, Vector3Int> cameFrom, Vector3Int current)
    {
        List<Vector3> path = new List<Vector3>();
        path.Add(GridToWorld(current));
        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            path.Add(GridToWorld(current));
        }
        path.Reverse();
        return path;
    }

    private IEnumerable<Vector3Int> GetNeighbors(Vector3Int center)
    {
        yield return center + new Vector3Int(1, 0, 0);
        yield return center + new Vector3Int(-1, 0, 0);
        yield return center + new Vector3Int(0, 0, 1);
        yield return center + new Vector3Int(0, 0, -1);
    }

    private Vector3Int FindClosestVisited(Vector3Int p)
    {
        if (visitedGroundVoxels.Count == 0) return p;
        for(int r=1; r<4; r++) {
            for(int x=-r; x<=r; x++) for(int y=-r; y<=r; y++) for(int z=-r; z<=r; z++) {
                Vector3Int n = p + new Vector3Int(x,y,z);
                if(visitedGroundVoxels.Contains(n)) return n;
            }
        }
        return p;
    }

    private Vector3Int WorldToGrid(Vector3 pos) => new Vector3Int(Mathf.FloorToInt(pos.x / granularity), Mathf.FloorToInt(pos.y / granularity), Mathf.FloorToInt(pos.z / granularity));
    private Vector3 GridToWorld(Vector3Int grid) => new Vector3(grid.x * granularity + (granularity * 0.5f), grid.y * granularity + (granularity * 0.5f), grid.z * granularity + (granularity * 0.5f));

    public string GetSerializationData()
    {
        SlamMapData data = new SlamMapData();
        data.roverName = gameObject.name;
        data.origin = originPosition;
        data.finalScore = agent.GetDistanceMetric();
        
        data.obstacles = occupiedVoxels.Select(v => new Vector3(v.x, v.y, v.z) * granularity).ToList();
        data.safeGround = visitedGroundVoxels.Select(v => new Vector3(v.x, v.y, v.z) * granularity).ToList();
        data.landmarks = landmarkVoxels.Select(v => new Vector3(v.x, v.y, v.z) * granularity).ToList();
        data.trajectory = new List<Vector3>(trajectory);
        data.keyLocations = new List<Vector3>(keyLocations);

        return JsonUtility.ToJson(data, true);
    }

    private void OnDrawGizmos()
    {
        if (showCalculatedPath && currentCalculatedPath != null && currentCalculatedPath.Count > 0)
        {
            Gizmos.color = Color.green;
            for(int i=0; i<currentCalculatedPath.Count-1; i++) Gizmos.DrawLine(currentCalculatedPath[i], currentCalculatedPath[i+1]);
        }

        if (showExplorationTarget && furthestVoxel != Vector3Int.zero)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(GridToWorld(furthestVoxel), 1.0f);
        }
    }
}