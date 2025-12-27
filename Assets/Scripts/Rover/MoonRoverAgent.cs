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

    [Header("3. Sensors")]
    public List<GameObject> lidarSensors;
    public LayerMask sensorVisibilityMask;
    public YoloLandmarkDetector yoloDetector;

    [Header("4. LiDAR Settings")]
    public float lidarRayLength = 50f;
    public float lidarRayDegrees = 90f;
    public int lidarRaysPerDirection = 5;
    public float lidarSphereRadius = 0.2f;

    // FIX: Restored these fields which are required by VoxelSlamMapper
    public float lidarVerticalDegrees = 30f;
    public int lidarVerticalRaysPerDirection = 2;

    [Header("5. Safety & Rewards")]
    public string obstacleTag = "Rocks";
    public float collisionPenalty = -10.0f;
    public float backtrackingPenalty = -0.05f;

    // Internal State
    private Rigidbody rb;
    private int currentWaypointIndex = 0;
    private Transform currentTarget;
    private Vector3 lastPosition;
    private float bestDistanceToTarget;
    // FIX: Removed unused 'currentPerformanceMetric'
    private GenerationManager manager;

    public VoxelSlamMapper slamMapper { get; private set; }
    private Vector3 nextPathPoint = Vector3.zero;

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
        // FIX: Updated to FindFirstObjectByType (Unity 2023+)
        manager = FindFirstObjectByType<GenerationManager>();
        slamMapper = GetComponent<VoxelSlamMapper>();

        if (yoloDetector == null) yoloDetector = GetComponent<YoloLandmarkDetector>();
        // FIX: Updated to FindFirstObjectByType
        if (moonTerrain == null) moonTerrain = FindFirstObjectByType<TerrainGenerator>();

        MaxStep = episodeStepLimit;
        ConfigureSensors();
    }

    private void ConfigureSensors()
    {
        int roverLayer = this.gameObject.layer;
        // Exclude rover's own layer from sensors
        if ((sensorVisibilityMask.value & (1 << roverLayer)) != 0) sensorVisibilityMask &= ~(1 << roverLayer);
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

        // Manual reset logic for Eval mode to prevent double resets
        if (manager != null && !manager.evaluationMode)
        {
            EndEpisode();
        }
        else
        {
            lastPosition = transform.position;
            if (slamMapper) slamMapper.SetOrigin(pos);
        }
    }

    public void SetBodyMaterial(Material mat)
    {
        if (bodyRenderer != null && mat != null) bodyRenderer.material = mat;
    }

    public float GetDistanceMetric()
    {
        float distToCp = (currentTarget != null) ? Vector3.Distance(transform.position, currentTarget.position) : 999f;
        return (currentWaypointIndex * 1000f) + (1000f - distToCp);
    }

    public override void OnEpisodeBegin()
    {
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        currentWaypointIndex = 0;
        returnToHomeMode = false;

        if (slamMapper != null) slamMapper.SetOrigin(transform.position);

        if (moonTerrain != null && moonTerrain.ActiveCheckpoints.Count > 0)
        {
            currentTarget = moonTerrain.ActiveCheckpoints[0];
            bestDistanceToTarget = Vector3.Distance(transform.position, currentTarget.position);
        }
        lastPosition = transform.position;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (currentTarget == null) { sensor.AddObservation(new float[15]); return; }

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

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (returnToHomeMode) return;

        ApplyPhysics(actions.ContinuousActions[0], actions.ContinuousActions[1]);
        SyncWheelVisuals();
        CheckCheckpointLogic();
        CalculatePerformanceRewards();
    }

    void FixedUpdate()
    {
        if (slamMapper != null)
        {
            if (moonTerrain != null && moonTerrain.terrain != null) slamMapper.ScanTerrainSurface(moonTerrain.terrain);
            if (Time.fixedTime % 0.2f < Time.fixedDeltaTime)
            {
                if (returnToHomeMode) slamMapper.RecalculatePath(transform.position, slamMapper.originPosition);
                else slamMapper.RecalculateExplorationPath(transform.position);
                nextPathPoint = slamMapper.GetNextPathPoint(transform.position);
            }
        }
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

        // TIPPING CHECK
        if (transform.up.y < 0.4f)
        {
            AddReward(-5.0f);
            if (manager) manager.OnRoverFailure(this, GenerationManager.RunResult.TippedOver);
            EndEpisode();
            return;
        }

        // Distance Reward
        float currentDist = Vector3.Distance(transform.position, currentTarget.position);
        if (currentDist < bestDistanceToTarget)
        {
            AddReward((bestDistanceToTarget - currentDist) * 0.2f);
            bestDistanceToTarget = currentDist;
        }
        else if (currentDist > bestDistanceToTarget + 0.2f)
        {
            AddReward(backtrackingPenalty);
        }

        // Path Alignment Penalty
        float pathDist = moonTerrain.GetDistanceFromPath(transform.position);
        if (pathDist > moonTerrain.pathWidth * 0.5f) AddReward(-0.01f);
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
                AddReward(25.0f);
                if (manager != null) manager.OnRoverReachedEnd(this);
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
            if (manager) manager.OnRoverFailure(this, GenerationManager.RunResult.Crash);
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