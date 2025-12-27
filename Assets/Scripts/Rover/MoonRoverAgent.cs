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
    public bool returnToHomeMode = false;

    [Header("3. Telemetry & Sensors")]
    public List<GameObject> cameraSensors;
    public List<GameObject> lidarSensors;
    public LayerMask sensorVisibilityMask;
    
    public YoloLandmarkDetector yoloDetector;

    [Header("4. LiDAR Settings")]
    public float lidarRayLength = 50f;
    public float lidarRayDegrees = 90f;
    public int lidarRaysPerDirection = 5; 
    public float lidarVerticalDegrees = 30f;
    public int lidarVerticalRaysPerDirection = 2;
    public float lidarSphereRadius = 0.2f;

    [Header("5. Safety & Performance")]
    public string obstacleTag = "Rocks";
    public float collisionPenalty = -10.0f;
    
    [Tooltip("Penalty per meter moved away from target.")]
    public float backtrackingPenalty = -1.0f; 
    
    [Tooltip("Penalty applied every step to encourage speed.")]
    public float timePenalty = -0.005f; 
    
    [Tooltip("Reward for simply facing the checkpoint.")]
    public float alignmentReward = 0.02f;

    // Internal State
    private Rigidbody rb;
    private int currentWaypointIndex = 0;
    private Transform currentTarget;
    private Vector3 lastPosition;
    private float bestDistanceToTarget; 
    private float currentPerformanceMetric = 0f;
    private GenerationManager manager;
    
    public VoxelSlamMapper slamMapper { get; private set; } 
    
    private float navTimer = 0;
    private Vector3 nextPathPoint = Vector3.zero;

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
        manager = FindObjectOfType<GenerationManager>();
        slamMapper = GetComponent<VoxelSlamMapper>();
        
        if (yoloDetector == null) yoloDetector = GetComponent<YoloLandmarkDetector>();
        
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
        if (Time.frameCount % 5 != 0) return; 
        
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
        returnToHomeMode = false; 

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

        Vector3 vectorToTarget = currentTarget.position - transform.position;
        Vector3 localTarget = transform.InverseTransformDirection(vectorToTarget);
        sensor.AddObservation(localTarget.normalized); 
        sensor.AddObservation(Mathf.Clamp01(vectorToTarget.magnitude / 150f)); 

        Vector3 localVel = transform.InverseTransformDirection(rb.linearVelocity);
        sensor.AddObservation(localVel / maxSpeed); 
        sensor.AddObservation(rb.angularVelocity.y); 

        sensor.AddObservation(Vector3.Dot(transform.up, Vector3.up)); 
        sensor.AddObservation(transform.InverseTransformDirection(transform.position - lastPosition)); 
        
        Vector3 navDir = Vector3.zero;
        if (slamMapper != null && nextPathPoint != Vector3.zero)
        {
            navDir = (nextPathPoint - transform.position).normalized;
            navDir = transform.InverseTransformDirection(navDir); 
        }
        sensor.AddObservation(navDir); 

        lastPosition = transform.position;
    }

    void FixedUpdate()
    {
        if (slamMapper != null)
        {
            if (moonTerrain != null && moonTerrain.terrain != null)
            {
                slamMapper.ScanTerrainSurface(moonTerrain.terrain);
            }

            navTimer += Time.fixedDeltaTime;
            if (navTimer > 0.2f)
            {
                navTimer = 0;
                
                if (returnToHomeMode)
                    slamMapper.RecalculatePath(transform.position, slamMapper.originPosition);
                else
                    slamMapper.RecalculateExplorationPath(transform.position); 
                
                nextPathPoint = slamMapper.GetNextPathPoint(transform.position);
            }
        }

        if (returnToHomeMode)
        {
            PerformReturnHomeLogic();
        }
        
        if (manager != null && manager.testMode && moonTerrain != null)
        {
            float deviation = moonTerrain.GetDistanceFromPath(transform.position);
            manager.ReportTestMetrics(false, deviation);
            
            if (transform.position.y < -10)
            {
                manager.FailTestRun("Fell off world");
                EndEpisode();
            }
        }
    }

    private void PerformReturnHomeLogic()
    {
        if (nextPathPoint == Vector3.zero) return;

        Vector3 targetDir = (nextPathPoint - transform.position).normalized;
        Vector3 localTarget = transform.InverseTransformDirection(targetDir);
        
        float steer = Mathf.Clamp(localTarget.x * 2.0f, -1f, 1f);
        float motor = 0.5f; 

        if (Mathf.Abs(steer) > 0.5f) motor = 0.2f;

        ApplyPhysics(motor, steer);
        SyncWheelVisuals();
        UpdateVisualPerformance();
        
        if (Vector3.Distance(transform.position, slamMapper.originPosition) < 3.0f)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
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

    private void StopImmediately()
    {
        // 1. Kill Physics Velocity
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // 2. Apply Full Brakes to Wheels
        foreach (var wheel in wheels)
        {
            wheel.motorTorque = 0;
            wheel.brakeTorque = float.MaxValue;
        }
    }

    private void CalculatePerformanceRewards()
    {
        AddReward(timePenalty);

        if (moonTerrain == null || currentTarget == null) return;

        float currentDist = Vector3.Distance(transform.position, currentTarget.position);
        
        if (currentDist < bestDistanceToTarget)
        {
            float progress = bestDistanceToTarget - currentDist;
            AddReward(progress * 1.0f); 
            bestDistanceToTarget = currentDist;
        }
        else if (currentDist > bestDistanceToTarget + 0.1f)
        {
            float backDist = currentDist - bestDistanceToTarget;
            AddReward(backtrackingPenalty * backDist); 
        }

        Vector3 toTarget = (currentTarget.position - transform.position).normalized;
        float alignment = Vector3.Dot(transform.forward, toTarget);
        AddReward(alignment * alignmentReward); 

        float pathDist = moonTerrain.GetDistanceFromPath(transform.position);
        if (pathDist > moonTerrain.pathWidth * 0.5f)
        {
            AddReward(-0.02f); 
        }

        if (transform.up.y < 0.4f)
        {
            AddReward(-5.0f);
            if (manager != null && manager.testMode) manager.FailTestRun("Flipped Over");
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
            
            if (slamMapper != null) slamMapper.AddKeyLocation(transform.position);

            if (isLast)
            {
                // STOP IMMEDIATELY
                StopImmediately();

                AddReward(30.0f); 
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
            if (manager != null && manager.testMode)
            {
                manager.ReportTestMetrics(true, 0); 
                manager.FailTestRun("Hit Obstacle");
            }
            EndEpisode();
        }
    }

    private void SyncWheelVisuals()
    {
        if (Time.frameCount % 2 != 0) return; 
        for (int i = 0; i < wheels.Count; i++)
        {
            if (i >= wheelMeshes.Count) break;
            wheels[i].GetWorldPose(out Vector3 pos, out Quaternion rot);
            wheelMeshes[i].position = pos;
            wheelMeshes[i].rotation = rot;
        }
    }
}