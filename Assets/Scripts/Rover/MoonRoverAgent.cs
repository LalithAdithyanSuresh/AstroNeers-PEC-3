using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using System.Collections.Generic;

[RequireComponent(typeof(Rigidbody))]
public class MoonRoverAgent : Agent
{
    [Header("1. Drive System")]
    public List<WheelCollider> wheels;
    public List<Transform> wheelMeshes;
    public MeshRenderer bodyRenderer; 

    public float maxMotorTorque = 500f;
    public float maxSteeringAngle = 30f;
    public float brakeTorque = 1000f;

    [Header("2. Mission Settings")]
    public float maxSpeed = 10f;
    public TerrainGenerator moonTerrain;
    public float checkpointProximity = 5f;
    public int episodeStepLimit = 5000; 
    public bool returnToHomeMode = false; // Flag to override ML logic

    [Header("3. Telemetry & Sensors")]
    public List<GameObject> cameraSensors;
    public List<GameObject> lidarSensors;
    public LayerMask sensorVisibilityMask;
    
    // New YOLO Reference
    public YoloLandmarkDetector yoloDetector;

    [Header("4. LiDAR Settings")]
    public float lidarRayLength = 50f;
    public float lidarRayDegrees = 90f;
    public int lidarRaysPerDirection = 10;
    public float lidarVerticalDegrees = 30f;
    public int lidarVerticalRaysPerDirection = 3;
    public float lidarSphereRadius = 0.2f;

    [Header("5. Safety & Performance Settings")]
    public string obstacleTag = "Rocks";
    public float collisionPenalty = -10.0f;
    public float backtrackingPenalty = -0.05f; 
    
    // Internal State
    private Rigidbody rb;
    private int currentWaypointIndex = 0;
    private Transform currentTarget;
    private Vector3 lastPosition;
    private float bestDistanceToTarget; 
    private float currentPerformanceMetric = 0f;
    private GenerationManager manager;
    
    // Reference to the SLAM Mapper
    public VoxelSlamMapper slamMapper { get; private set; } // Public getter for Manager to access
    
    // Navigation Timer
    private float navTimer = 0;
    private Vector3 nextPathPoint = Vector3.zero;

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
        manager = FindObjectOfType<GenerationManager>();
        slamMapper = GetComponent<VoxelSlamMapper>();
        
        // Auto-setup YOLO
        if (yoloDetector == null) yoloDetector = GetComponent<YoloLandmarkDetector>();
        
        // FIX: Reference 'simCameras' instead of 'yoloCameras' to match the updated script
        if (yoloDetector != null && yoloDetector.simCameras.Count == 0) 
        {
            yoloDetector.AutoAssignCameras();
        }

        if (moonTerrain == null) moonTerrain = FindObjectOfType<TerrainGenerator>();
        
        MaxStep = episodeStepLimit;

        if (GetComponent<DecisionRequester>() == null)
        {
            var dr = gameObject.AddComponent<DecisionRequester>();
            dr.DecisionPeriod = 5; 
            dr.TakeActionsBetweenDecisions = true; 
        }
        ConfigureSensors();
    }

    private void ConfigureSensors()
    {
        int roverLayer = this.gameObject.layer;
        if ((sensorVisibilityMask.value & (1 << roverLayer)) != 0) sensorVisibilityMask &= ~(1 << roverLayer);

        foreach(var obj in lidarSensors)
        {
            if(!obj) continue;
            var lidar = obj.GetComponent<RayPerceptionSensorComponent3D>();
            if (lidar)
            {
                lidar.RayLayerMask = sensorVisibilityMask;
                lidar.RayLength = lidarRayLength;
                lidar.MaxRayDegrees = lidarRayDegrees;
                lidar.RaysPerDirection = lidarRaysPerDirection;
                lidar.SphereCastRadius = lidarSphereRadius;
                lidar.DetectableTags = new List<string>() { obstacleTag, "Untagged" }; 
            }
        }
    }

    public void TeleportToStart(Vector3 pos, Quaternion rot)
    {
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        transform.position = pos;
        transform.rotation = rot;
        currentWaypointIndex = 0;
        
        // Reset Logic
        returnToHomeMode = false;
        
        if (moonTerrain && moonTerrain.ActiveCheckpoints.Count > 0)
        {
            currentTarget = moonTerrain.ActiveCheckpoints[0];
            bestDistanceToTarget = Vector3.Distance(transform.position, currentTarget.position);
        }
        EndEpisode(); 
    }

    public void SetBodyMaterial(Material mat)
    {
        if (bodyRenderer != null && mat != null)
        {
            bodyRenderer.material = mat;
        }
    }

    private void UpdateVisualPerformance()
    {
        if (bodyRenderer == null) return;
        
        // Override color if returning home
        if (returnToHomeMode)
        {
            bodyRenderer.material.color = Color.magenta;
            return;
        }
        
        float speedFactor = Mathf.Clamp01(rb.linearVelocity.magnitude / maxSpeed);
        float alignment = 0;
        if(currentTarget != null)
        {
            alignment = Mathf.Clamp01(Vector3.Dot(transform.forward, (currentTarget.position - transform.position).normalized));
        }

        currentPerformanceMetric = Mathf.Lerp(currentPerformanceMetric, speedFactor * alignment, Time.deltaTime * 3f);
        
        Color perfColor;
        if (currentPerformanceMetric < 0.5f)
            perfColor = Color.Lerp(Color.red, Color.yellow, currentPerformanceMetric * 2f);
        else
            perfColor = Color.Lerp(Color.yellow, Color.green, (currentPerformanceMetric - 0.5f) * 2f);

        bodyRenderer.material.color = perfColor;
        bodyRenderer.material.EnableKeyword("_EMISSION");
        bodyRenderer.material.SetColor("_EmissionColor", perfColor * 0.4f);
    }

    public float GetDistanceMetric()
    {
        float distToCp = (currentTarget != null) ? Vector3.Distance(transform.position, currentTarget.position) : 999f;
        return (currentWaypointIndex * 1000f) + (1000f - distToCp);
    }
    
    public int GetCurrentCheckpoint() => currentWaypointIndex;

    public override void OnEpisodeBegin()
    {
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        currentWaypointIndex = 0;
        returnToHomeMode = false; // Always reset this at start of episode

        // Reset the SLAM Map and Set LOCALIZED ORIGIN
        if (slamMapper != null)
        {
            slamMapper.SetOrigin(transform.position);
        }

        if (moonTerrain != null)
        {
            if (moonTerrain.ActiveCheckpoints.Count > 0)
            {
                currentTarget = moonTerrain.ActiveCheckpoints[0];
                bestDistanceToTarget = Vector3.Distance(transform.position, currentTarget.position);
            }
        }
        lastPosition = transform.position;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (currentTarget == null) { sensor.AddObservation(new float[12 + 3]); return; } 

        // Observations of State
        Vector3 vectorToTarget = currentTarget.position - transform.position;
        Vector3 localTarget = transform.InverseTransformDirection(vectorToTarget);
        sensor.AddObservation(localTarget.normalized); // 3
        sensor.AddObservation(Mathf.Clamp01(vectorToTarget.magnitude / 150f)); // 1

        Vector3 localVel = transform.InverseTransformDirection(rb.linearVelocity);
        sensor.AddObservation(localVel / maxSpeed); // 3
        sensor.AddObservation(rb.angularVelocity.y); // 1

        sensor.AddObservation(Vector3.Dot(transform.up, Vector3.up)); // 1
        sensor.AddObservation(transform.InverseTransformDirection(transform.position - lastPosition)); // 3
        
        // SLAM NAVIGATION HINT (3 Observations)
        Vector3 navDir = Vector3.zero;
        if (slamMapper != null && nextPathPoint != Vector3.zero)
        {
            navDir = (nextPathPoint - transform.position).normalized;
            navDir = transform.InverseTransformDirection(navDir); 
        }
        sensor.AddObservation(navDir); // 3

        lastPosition = transform.position;
    }

    void FixedUpdate()
    {
        // SLAM INTEGRATION:
        if (slamMapper != null)
        {
            // 1. Scan Surface
            if (moonTerrain != null && moonTerrain.terrain != null)
            {
                slamMapper.ScanTerrainSurface(moonTerrain.terrain);
            }

            // 2. Navigation Update (Every 0.2s)
            navTimer += Time.fixedDeltaTime;
            if (navTimer > 0.2f)
            {
                navTimer = 0;
                
                if (returnToHomeMode)
                {
                    // RTH Logic: Target is Origin
                    slamMapper.RecalculatePath(transform.position, slamMapper.originPosition);
                }
                else
                {
                    // Normal Logic: Exploration
                    slamMapper.RecalculateExplorationPath(transform.position); 
                }
                
                nextPathPoint = slamMapper.GetNextPathPoint(transform.position);
            }
        }

        // AUTO-PILOT FOR RETURN TO HOME
        // We override the Neural Network if Return Home is active
        if (returnToHomeMode)
        {
            PerformReturnHomeLogic();
        }
    }

    // Explicit PID controller to drive home
    private void PerformReturnHomeLogic()
    {
        if (nextPathPoint == Vector3.zero) return;

        Vector3 targetDir = (nextPathPoint - transform.position).normalized;
        Vector3 localTarget = transform.InverseTransformDirection(targetDir);
        
        float steer = Mathf.Clamp(localTarget.x * 2.0f, -1f, 1f);
        float motor = 0.5f; // Constant gentle speed

        // If sharp turn, slow down
        if (Mathf.Abs(steer) > 0.5f) motor = 0.2f;

        // Apply
        ApplyPhysics(motor, steer);
        SyncWheelVisuals();
        UpdateVisualPerformance();
        
        // Stop if home
        if (Vector3.Distance(transform.position, slamMapper.originPosition) < 3.0f)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // If returning home, ignore NN actions
        if (returnToHomeMode) return;

        ApplyPhysics(actions.ContinuousActions[0], actions.ContinuousActions[1]);
        SyncWheelVisuals();
        CheckCheckpointLogic();
        CalculatePerformanceRewards();
        UpdateVisualPerformance();
    }

    private void ApplyPhysics(float motorInput, float steerInput)
    {
        float torque = motorInput * maxMotorTorque;
        float steer = steerInput * maxSteeringAngle;

        for (int i = 0; i < wheels.Count; i++)
        {
            wheels[i].motorTorque = torque;
            if (i < 2) wheels[i].steerAngle = steer;
            wheels[i].brakeTorque = Mathf.Abs(motorInput) < 0.05f ? brakeTorque : 0f;
        }
    }

    private void CalculatePerformanceRewards()
    {
        if (moonTerrain == null || currentTarget == null) return;

        float currentDist = Vector3.Distance(transform.position, currentTarget.position);
        
        if (currentDist < bestDistanceToTarget)
        {
            float progress = bestDistanceToTarget - currentDist;
            AddReward(progress * 0.2f); 
            bestDistanceToTarget = currentDist;
        }
        else if (currentDist > bestDistanceToTarget + 0.2f)
        {
            AddReward(backtrackingPenalty); 
        }

        Vector3 toTarget = (currentTarget.position - transform.position).normalized;
        float alignment = Vector3.Dot(transform.forward, toTarget);
        if (alignment < 0) AddReward(-0.01f); 

        float pathDist = moonTerrain.GetDistanceFromPath(transform.position);
        if (pathDist > moonTerrain.pathWidth * 0.5f)
        {
            AddReward(-0.01f);
        }

        if (transform.up.y < 0.4f)
        {
            AddReward(-5.0f);
            EndEpisode();
        }
    }

    private void CheckCheckpointLogic()
    {
        if (currentTarget == null) return;

        if (Vector3.Distance(transform.position, currentTarget.position) < checkpointProximity)
        {
            bool isLast = (currentWaypointIndex >= moonTerrain.ActiveCheckpoints.Count - 1);
            float scaledReward = 2.0f + (currentWaypointIndex * 1.5f);
            
            // SLAM Update: Record this as a key location!
            if (slamMapper != null) slamMapper.AddKeyLocation(transform.position);

            if (isLast)
            {
                AddReward(25.0f); 
                if (manager != null) manager.OnRoverReachedEnd(); 
                EndEpisode();
            }
            else
            {
                AddReward(scaledReward); 
                currentWaypointIndex++;
                currentTarget = moonTerrain.ActiveCheckpoints[currentWaypointIndex];
                bestDistanceToTarget = Vector3.Distance(transform.position, currentTarget.position);
            }
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag(obstacleTag))
        {
            AddReward(collisionPenalty);
            EndEpisode();
        }
    }

    private void SyncWheelVisuals()
    {
        for (int i = 0; i < wheels.Count; i++)
        {
            if (i >= wheelMeshes.Count) break;
            wheels[i].GetWorldPose(out Vector3 pos, out Quaternion rot);
            wheelMeshes[i].position = pos;
            wheelMeshes[i].rotation = rot;
        }
    }
}