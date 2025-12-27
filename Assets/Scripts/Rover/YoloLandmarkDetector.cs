using UnityEngine;
using System.Collections.Generic;

// Standard YOLO Result Structure
[System.Serializable]
public struct YoloResult
{
    public string label;        // e.g. "Rock"
    public float confidence;    // e.g. 0.85
    public Rect boundingBox;    // 0-1 Normalized Viewport Coordinates (x, y, w, h)
}

public class YoloLandmarkDetector : MonoBehaviour
{
    [Header("Mode Selection")]
    [Tooltip("If TRUE, waits for external script to call HandleRealDetections. If FALSE, cheats using Tags.")]
    public bool useRealInference = false; 

    [Header("Inference Settings (Real Mode)")]
    public Camera inferenceCamera; // The camera feeding the neural network
    public float depthRayRadius = 0.2f; // Thickness of ray to find the object depth

    [Header("Simulation Settings (Cheat Mode)")]
    public float detectionRange = 25f;
    public float detectionRate = 0.2f; 
    public LayerMask detectionMask;
    public List<Camera> simCameras; // Cameras to check in simulation mode

    [Header("References")]
    public VoxelSlamMapper slamMapper;

    private float timer;
    private List<GameObject> detectableObjects = new List<GameObject>();

    void Start()
    {
        if (slamMapper == null) slamMapper = GetComponent<VoxelSlamMapper>();
        if (inferenceCamera == null) inferenceCamera = GetComponentInChildren<Camera>();

        // Cache objects for cheat mode only
        if (!useRealInference)
        {
            GameObject[] rocks = GameObject.FindGameObjectsWithTag("Rocks");
            foreach(var r in rocks) detectableObjects.Add(r);
        }
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (timer >= detectionRate)
        {
            if (!useRealInference)
            {
                PerformSimulatedDetection();
            }
            // In Real Mode, we do nothing here. We wait for your Neural Network script 
            // to finish processing the frame and call HandleRealDetections().
            timer = 0;
        }
    }

    // ==================================================================================
    // PART 1: THE REAL INTEGRATION (Call this from your Model Script)
    // ==================================================================================

    /// <summary>
    /// Feed this method the results from your YOLO model.
    /// It converts 2D Bounding Boxes on screen -> 3D SLAM Landmarks.
    /// </summary>
    /// <param name="results">List of detections from Sentis/Barracuda/OpenCV</param>
    public void HandleRealDetections(List<YoloResult> results)
    {
        if (inferenceCamera == null)
        {
            Debug.LogError("YoloLandmarkDetector: No Inference Camera assigned!");
            return;
        }

        foreach (var result in results)
        {
            if (result.confidence < 0.5f) continue;

            // 1. Get the Center of the Bounding Box (0-1 format)
            Vector2 center = result.boundingBox.center;
            
            // NOTE: Ensure your YOLO output Y-axis matches Unity's Viewport (0 is bottom, 1 is top).
            // If your model returns 0 as top, use: center.y = 1f - center.y;
            Vector3 viewportPoint = new Vector3(center.x, center.y, 0); 
            
            // 2. Cast a Ray from the Camera through that pixel
            Ray ray = inferenceCamera.ViewportPointToRay(viewportPoint);

            // 3. Find Depth (Simulating Stereo Depth or Lidar Fusion)
            // We cast a sphere-ray to see what physical object matches that visual bounding box.
            if (Physics.SphereCast(ray, depthRayRadius, out RaycastHit hit, detectionRange, detectionMask))
            {
                // Verify we didn't just hit the ground immediately in front of us
                if (hit.distance > 0.5f) 
                {
                    // 4. Register the 3D point in the SLAM Map
                    slamMapper.RegisterLandmark(hit.point, result.label);

                    // Debug: Draw a line from camera to the detected object
                    Debug.DrawLine(inferenceCamera.transform.position, hit.point, Color.magenta, 0.5f);
                }
            }
        }
    }

    // ==================================================================================
    // PART 2: THE SIMULATION (Cheat Mode)
    // ==================================================================================

    void PerformSimulatedDetection()
    {
        if (slamMapper == null) return;

        foreach (var obj in detectableObjects)
        {
            if (obj == null) continue;
            if (IsVisibleToAnyCamera(obj))
            {
                // Simulate a high-confidence YOLO result
                slamMapper.RegisterLandmark(obj.transform.position, "Rock_Landmark");
            }
        }
    }

    private bool IsVisibleToAnyCamera(GameObject target)
    {
        Vector3 targetPos = target.transform.position;
        if (Vector3.Distance(transform.position, targetPos) > detectionRange) return false;

        List<Camera> camsToCheck = (simCameras != null && simCameras.Count > 0) ? simCameras : new List<Camera>{ inferenceCamera };

        foreach (var cam in camsToCheck)
        {
            if (cam == null) continue;
            Vector3 vp = cam.WorldToViewportPoint(targetPos);
            
            // Check if inside screen bounds
            if (vp.z > 0 && vp.x > 0 && vp.x < 1 && vp.y > 0 && vp.y < 1)
            {
                // Check Line of Sight
                Vector3 dir = targetPos - cam.transform.position;
                if (Physics.Raycast(cam.transform.position, dir, out RaycastHit hit, detectionRange, detectionMask))
                {
                    if (hit.collider.gameObject == target || hit.collider.transform.root == target.transform.root)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public void AutoAssignCameras()
    {
        simCameras = new List<Camera>(GetComponentsInChildren<Camera>());
        if (simCameras.Count > 0) inferenceCamera = simCameras[0];
    }
}