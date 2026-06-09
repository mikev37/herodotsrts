using UnityEngine;

/// <summary>
/// RTS Camera Controller
/// - Scroll wheel        : Zoom (FOV)
/// - Mouse near edge     : Pan along XZ plane
/// - Middle mouse drag   : Pan along XZ plane
/// - Right mouse drag    : Orbit (yaw + pitch)
/// </summary>
[RequireComponent(typeof(Camera))]
public class RTSCameraController : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Zoom
    // -------------------------------------------------------------------------
    [Header("Zoom (FOV)")]
    public float zoomSpeed       = 5f;
    public float zoomSmoothing   = 8f;
    public float fovMin          = 20f;
    public float fovMax          = 80f;

    // -------------------------------------------------------------------------
    // Edge Pan
    // -------------------------------------------------------------------------
    [Header("Edge Pan")]
    public float edgePanSpeed        = 20f;
    [Range(0f, 0.1f)]
    public float edgeThreshold       = 0.02f;   // fraction of screen width/height
    public bool  edgePanEnabled      = true;
    public float panSmoothing        = 8f;

    // -------------------------------------------------------------------------
    // Middle Mouse Drag Pan  (no tuning needed — tracks mouse exactly)
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Right Click Orbit
    // -------------------------------------------------------------------------
    [Header("Orbit (Right Mouse)")]
    public float orbitSensitivity    = 0.25f;
    public float pitchMin            = 10f;
    public float pitchMax            = 80f;
    public float orbitSmoothing      = 10f;

    // -------------------------------------------------------------------------
    // XZ Pan Bounds
    // -------------------------------------------------------------------------
    [Header("Pan Bounds (XZ world space, set equal to disable)")]
    public Vector2 panBoundsMin      = new Vector2(-500f, -500f);
    public Vector2 panBoundsMax      = new Vector2( 500f,  500f);

    // =========================================================================
    // Private state
    // =========================================================================
    private Camera   _cam;
    private float    _targetFov;
    private Vector3  _targetPosition;
    private float    _targetYaw;
    private float    _targetPitch;

    // middle-mouse drag
    private bool     _dragging;
    private Vector3  _dragGroundOrigin;  // world point on ground under cursor at drag start

    // right-mouse orbit
    private bool     _orbiting;
    private Vector3  _orbitFocusPoint;   // world point we orbit around
    private float    _orbitRadius;

    // =========================================================================
    void Awake()
    {
        _cam = GetComponent<Camera>();

        _targetFov      = _cam.fieldOfView;
        _targetPosition = transform.position;

        Vector3 angles  = transform.eulerAngles;
        _targetYaw      = angles.y;
        _targetPitch    = angles.x;
    }

    void Update()
    {
        HandleZoom();
        HandleOrbit();
        HandleMiddleMouseDrag();
        HandleEdgePan();
        ApplyTransform();
    }

    // =========================================================================
    // Zoom
    // =========================================================================
    void HandleZoom()
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.001f && !_dragging)
        {
            _targetFov -= scroll * zoomSpeed * _targetFov; // proportional feel
            _targetFov  = Mathf.Clamp(_targetFov, fovMin, fovMax);
        }

        _cam.fieldOfView = Mathf.Lerp(_cam.fieldOfView, _targetFov, Time.deltaTime * zoomSmoothing);
    }

    // =========================================================================
    // Right Mouse Orbit
    // =========================================================================
    void HandleOrbit()
    {
        if (Input.GetMouseButtonDown(1))
        {
            _orbiting = true;
            // Pick a focus point on the ground plane (y=0) beneath the camera
            _orbitFocusPoint = GetGroundPoint();
            _orbitRadius     = Vector3.Distance(transform.position, _orbitFocusPoint);
        }

        if (Input.GetMouseButtonUp(1))
        {
            _orbiting = false;
        }

        if (!_orbiting) return;

        float dx = Input.GetAxis("Mouse X");
        float dy = Input.GetAxis("Mouse Y");

        _targetYaw   += dx * orbitSensitivity * 100f * Time.deltaTime;
        _targetPitch -= dy * orbitSensitivity * 100f * Time.deltaTime;
        _targetPitch  = Mathf.Clamp(_targetPitch, pitchMin, pitchMax);

        // Recompute camera position from yaw/pitch/radius around focus
        Quaternion rot    = Quaternion.Euler(_targetPitch, _targetYaw, 0f);
        Vector3    offset = rot * new Vector3(0f, 0f, -_orbitRadius);
        _targetPosition   = _orbitFocusPoint + offset;

        // Keep focus within bounds
        _targetPosition.x = Mathf.Clamp(_targetPosition.x, panBoundsMin.x, panBoundsMax.x);
        _targetPosition.z = Mathf.Clamp(_targetPosition.z, panBoundsMin.y, panBoundsMax.y);
    }

    // =========================================================================
    // Middle Mouse Drag Pan
    // =========================================================================
    void HandleMiddleMouseDrag()
    {
        if (Input.GetMouseButtonDown(2))
        {
            _dragging = true;
            // Record the world point on the ground that is currently under the cursor.
            // Every subsequent frame we translate the camera so that same ground point
            // stays under the cursor — no speed tuning required, no Time.deltaTime.
            _dragGroundOrigin = ScreenPointToGroundPoint(Input.mousePosition);
        }

        if (Input.GetMouseButtonUp(2))
        {
            _dragging = false;
        }

        if (!_dragging) return;

        // Where would the ground point be under the cursor right now,
        // if the camera had NOT moved yet this frame?
        // We raycast from the camera's current (not target) position so the
        // ground point is computed in the same space as _dragGroundOrigin.
        Vector3 currentGroundPoint = ScreenPointToGroundPoint(Input.mousePosition);

        // Shift the target so the origin ground point returns under the cursor.
        Vector3 delta = _dragGroundOrigin - currentGroundPoint;
        _targetPosition += delta;

        ClampPosition();
    }

    // =========================================================================
    // Edge Pan
    // =========================================================================
    void HandleEdgePan()
    {
        if (!edgePanEnabled)   return;
        if (_orbiting)         return;
        if (_dragging)         return;

        Vector3 mousePos = Input.mousePosition;
        float   w        = Screen.width;
        float   h        = Screen.height;

        Vector3 panDir = Vector3.zero;

        if (mousePos.x < w * edgeThreshold)            panDir.x -= 1f;
        else if (mousePos.x > w * (1f - edgeThreshold)) panDir.x += 1f;

        if (mousePos.y < h * edgeThreshold)            panDir.z -= 1f;
        else if (mousePos.y > h * (1f - edgeThreshold)) panDir.z += 1f;

        if (panDir == Vector3.zero) return;

        // Rotate pan direction by current yaw so panning is screen-relative
        panDir = Quaternion.Euler(0f, _targetYaw, 0f) * panDir.normalized;

        // Scale by FOV so panning feels consistent when zoomed
        float speed = edgePanSpeed * (_cam.fieldOfView / fovMax);

        _targetPosition += panDir * speed * Time.deltaTime;
        ClampPosition();
    }

    // =========================================================================
    // Apply smooth transform
    // =========================================================================
    void ApplyTransform()
    {
        if (!_orbiting)
        {
            transform.position = Vector3.Lerp(
                transform.position, _targetPosition, Time.deltaTime * panSmoothing);

            Quaternion targetRot = Quaternion.Euler(_targetPitch, _targetYaw, 0f);
            transform.rotation   = Quaternion.Slerp(
                transform.rotation, targetRot, Time.deltaTime * orbitSmoothing);
        }
        else
        {
            // During orbit snap position tightly; smoothing applied via target update
            transform.position = Vector3.Lerp(
                transform.position, _targetPosition, Time.deltaTime * orbitSmoothing);

            Quaternion targetRot = Quaternion.Euler(_targetPitch, _targetYaw, 0f);
            transform.rotation   = Quaternion.Slerp(
                transform.rotation, targetRot, Time.deltaTime * orbitSmoothing);
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================
    void ClampPosition()
    {
        _targetPosition.x = Mathf.Clamp(_targetPosition.x, panBoundsMin.x, panBoundsMax.x);
        _targetPosition.z = Mathf.Clamp(_targetPosition.z, panBoundsMin.y, panBoundsMax.y);
    }

    /// <summary>Casts a ray from the camera to the Y=0 plane and returns the hit point.</summary>
    Vector3 GetGroundPoint()
    {
        Ray   ray   = new Ray(transform.position, transform.forward);
        Plane plane = new Plane(Vector3.up, Vector3.zero);

        if (plane.Raycast(ray, out float dist))
            return ray.GetPoint(dist);

        // Fallback: project straight down from current position
        return new Vector3(transform.position.x, 0f, transform.position.z);
    }

    /// <summary>
    /// Casts a ray from the camera through a screen-space pixel to the Y=0 ground plane.
    /// Uses the camera's actual current transform (not the smoothed target) so drag
    /// calculations are always in a consistent space.
    /// </summary>
    Vector3 ScreenPointToGroundPoint(Vector3 screenPos)
    {
        Ray   ray   = _cam.ScreenPointToRay(screenPos);
        Plane plane = new Plane(Vector3.up, Vector3.zero);

        if (plane.Raycast(ray, out float dist))
            return ray.GetPoint(dist);

        // Fallback: same height-0 position as the camera's XZ
        return new Vector3(transform.position.x, 0f, transform.position.z);
    }
}
