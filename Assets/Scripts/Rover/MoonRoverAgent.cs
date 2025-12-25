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

    [Header("3. Telemetry & Sensors")]
    public List<GameObject> cameraSensors;
    public List<GameObject> lidarSensors;
    public LayerMask sensorVisibilityMask;

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
    public float backtrackingPenalty = -0.05f; // Increased penalty for moving backwards
    
    // Internal State
    private Rigidbody rb;
    private int currentWaypointIndex = 0;
    private Transform currentTarget;
    private Vector3 lastPosition;
    private float bestDistanceToTarget; 
    private float currentPerformanceMetric = 0f;
    private GenerationManager manager;
    
    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
        manager = FindObjectOfType<GenerationManager>();
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
        if (moonTerrain && moonTerrain.ActiveCheckpoints.Count > 0)
        {
            currentTarget = moonTerrain.ActiveCheckpoints[0];
            bestDistanceToTarget = Vector3.Distance(transform.position, currentTarget.position);
        }
        EndEpisode(); 
    }

    /// <summary>
    /// Sets the material of the rover's body. 
    /// Used by the GenerationManager to highlight specific rovers.
    /// </summary>
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
        
        // Calculate performance (0 to 1) based on speed and alignment with target
        float speedFactor = Mathf.Clamp01(rb.linearVelocity.magnitude / maxSpeed);
        float alignment = 0;
        if(currentTarget != null)
        {
            alignment = Mathf.Clamp01(Vector3.Dot(transform.forward, (currentTarget.position - transform.position).normalized));
        }

        // Smooth transition for the performance metric
        currentPerformanceMetric = Mathf.Lerp(currentPerformanceMetric, speedFactor * alignment, Time.deltaTime * 3f);
        
        // Shift color: Red (0) -> Yellow (0.5) -> Green (1.0)
        Color perfColor;
        if (currentPerformanceMetric < 0.5f)
            perfColor = Color.Lerp(Color.red, Color.yellow, currentPerformanceMetric * 2f);
        else
            perfColor = Color.Lerp(Color.yellow, Color.green, (currentPerformanceMetric - 0.5f) * 2f);

        bodyRenderer.material.color = perfColor;
        // Make it glow slightly
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
        // currentTarget is null, add 12 zeroes to match the observation count below
        if (currentTarget == null) { sensor.AddObservation(new float[12]); return; }

        Vector3 vectorToTarget = currentTarget.position - transform.position;
        Vector3 localTarget = transform.InverseTransformDirection(vectorToTarget);
        sensor.AddObservation(localTarget.normalized); // 3
        sensor.AddObservation(Mathf.Clamp01(vectorToTarget.magnitude / 150f)); // 1

        Vector3 localVel = transform.InverseTransformDirection(rb.linearVelocity);
        sensor.AddObservation(localVel / maxSpeed); // 3
        sensor.AddObservation(rb.angularVelocity.y); // 1

        sensor.AddObservation(Vector3.Dot(transform.up, Vector3.up)); // 1
        sensor.AddObservation(transform.InverseTransformDirection(transform.position - lastPosition)); // 3
        
        lastPosition = transform.position;
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
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
        
        // 1. IMPROVED PROGRESS REWARD / BACKTRACKING PENALTY
        if (currentDist < bestDistanceToTarget)
        {
            float progress = bestDistanceToTarget - currentDist;
            AddReward(progress * 0.2f); // Reward for moving forward
            bestDistanceToTarget = currentDist;
        }
        else if (currentDist > bestDistanceToTarget + 0.2f)
        {
            AddReward(backtrackingPenalty); // Penalty for moving backward from best point
        }

        // 2. DIRECTIONAL ALIGNMENT
        Vector3 toTarget = (currentTarget.position - transform.position).normalized;
        float alignment = Vector3.Dot(transform.forward, toTarget);
        if (alignment < 0) AddReward(-0.01f); // Penalty for facing wrong way

        // 3. ROAD ADHERENCE
        float pathDist = moonTerrain.GetDistanceFromPath(transform.position);
        if (pathDist > moonTerrain.pathWidth * 0.5f)
        {
            AddReward(-0.01f);
        }

        // 4. TIP-OVER PROTECTION
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
            
            // SCALED REWARDS: Each checkpoint is more valuable than the last
            float scaledReward = 2.0f + (currentWaypointIndex * 1.5f);
            
            if (isLast)
            {
                AddReward(25.0f); // Massive finish line bonus
                if (manager != null) manager.OnRoverReachedEnd(); // Signal the manager
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