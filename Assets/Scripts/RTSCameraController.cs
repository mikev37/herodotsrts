using UnityEngine;

/// <summary>
/// RTS Camera Controller
/// - Scroll wheel             : Zoom (FOV), pinned on the point under the cursor
/// - Mouse near edge          : Pan along XZ plane
/// - Middle mouse drag        : Pan along XZ plane
/// - Left + Right mouse drag  : Orbit (yaw + pitch)
///
/// Orbit requires BOTH mouse buttons so a plain right-drag is free for unit
/// orders (formation width in PlayerCommander).
///
/// Every mouse-to-world raycast goes through TryScreenPointToGroundPoint, which
/// rejects non-finite / off-viewport cursor positions BEFORE handing them to the
/// camera. Unity's Camera.ScreenPointToRay logs "Screen position out of view
/// frustum" for such input (Input.mousePosition returns (inf,-inf) when the
/// cursor is outside the Game view or focus was just lost); the guard keeps the
/// log clean and the camera stable.
/// </summary>
[RequireComponent(typeof(Camera))]
public class RTSCameraController : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Zoom
    // -------------------------------------------------------------------------
    [Header("Zoom (FOV)")]
    public float zoomSpeed       = 5f;
    [Tooltip("Zoom easing responsiveness. Higher = snappier. Used as 1/smoothTime for a critically-damped SmoothDamp (eases in and out, no overshoot).")]
    public float zoomSmoothing   = 8f;
    [Tooltip("Maximum FOV change rate (degrees/second). Caps how fast a large zoom traverses, so no single frame gets a big FOV step (hence no big cursor-pin pan).")]
    public float maxZoomSpeed    = 90f;
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
    // Orbit (Left + Right Mouse)
    // -------------------------------------------------------------------------
    [Header("Orbit (Left + Right Mouse)")]
    [Tooltip("Degrees of rotation per pixel of mouse movement.")]
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
    private float    _zoomVel;            // SmoothDamp velocity for the FOV ease
    private Vector3  _targetPosition;
    private float    _targetYaw;
    private float    _targetPitch;

    // middle-mouse drag
    private bool     _dragging;
    private Vector3  _dragGroundOrigin;   // world point on ground under cursor at drag start

    // orbit (left + right mouse)
    private bool     _orbiting;
    private Vector3  _orbitFocusPoint;    // world point we orbit around
    private float    _orbitRadius;
    private Vector3  _lastMousePos;       // for frame-to-frame orbit deltas

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
    // Zoom (SmoothDamp FOV, pinned on the cursor)
    // =========================================================================
    void HandleZoom()
    {
        // Accumulate scroll into the target FOV (ignored while a drag/orbit owns the mouse).
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.001f && !_dragging && !_orbiting)
            _targetFov = Mathf.Clamp(_targetFov - scroll * zoomSpeed * _targetFov, fovMin, fovMax);

        // Settled: snap exactly and skip the per-frame raycasts (also avoids feeding
        // an off-view cursor to the camera while idle).
        if (Mathf.Abs(_cam.fieldOfView - _targetFov) < 0.001f && Mathf.Abs(_zoomVel) < 0.001f)
        {
            _cam.fieldOfView = _targetFov;
            _zoomVel = 0f;
            return;
        }

        // Derive smoothTime from the inspector's zoomSmoothing (rate -> seconds) so the
        // existing value keeps its meaning. SmoothDamp eases in AND out and is velocity-
        // capped (maxZoomSpeed), so no single frame gets a large FOV step — which is what
        // keeps the cursor-pin pan below from ever lurching.
        float smoothTime = 1f / Mathf.Max(0.01f, zoomSmoothing);

        // Zoom toward the cursor: sample the ground point under the cursor, ease the FOV
        // one frame, sample again, then shift the camera by the difference so that point
        // stays pinned. The shift is derived from THIS frame's ACTUAL FOV change (not the
        // whole pending jump), so the pan tracks the zoom exactly with no swim/overshoot,
        // and it's applied to the live position (with the target kept in sync) so it isn't
        // double-smoothed. If the cursor is off-view / unusable, the pin is skipped and we
        // simply zoom about the screen centre.
        bool    canPin = !_orbiting && !_dragging;
        Vector3 before = default;
        bool    pin    = canPin && TryScreenPointToGroundPoint(Input.mousePosition, out before);

        _cam.fieldOfView = Mathf.SmoothDamp(_cam.fieldOfView, _targetFov, ref _zoomVel,
                                            smoothTime, maxZoomSpeed, Time.deltaTime);

        if (pin && TryScreenPointToGroundPoint(Input.mousePosition, out Vector3 after))
        {
            Vector3 shift = before - after;
            if (IsFinite(shift))
            {
                transform.position = ClampXZ(transform.position + shift);
                _targetPosition    = ClampXZ(_targetPosition + shift);
            }
        }
    }

    // =========================================================================
    // Orbit (Left + Right Mouse)
    // =========================================================================
    void HandleOrbit()
    {
        // Orbit requires BOTH mouse buttons held — leaves a plain right-drag free
        // for unit orders. Either button releasing ends the orbit.
        bool bothDown = Input.GetMouseButton(0) && Input.GetMouseButton(1);

        if (!_orbiting && bothDown)
        {
            _orbiting = true;

            // Re-sync the yaw/pitch targets to the LIVE transform BEFORE we
            // reconstruct the orbit position from them below. If the targets had
            // drifted from the actual rotation (smoothing lag, prior edits), the
            // first frame would snap the camera onto the recomputed orbit sphere —
            // that is the "jerk on right-click" / teleport.
            Vector3 e    = transform.eulerAngles;
            _targetYaw   = e.y;
            _targetPitch = e.x;

            _orbitFocusPoint = GetGroundPoint();
            _orbitRadius     = Vector3.Distance(transform.position, _orbitFocusPoint);

            // Seed the delta tracker so the opening frame rotates by zero.
            _lastMousePos = Input.mousePosition;
        }
        else if (_orbiting && !bothDown)
        {
            _orbiting = false;
        }

        if (!_orbiting) return;

        // Raw screen-space delta. We deliberately avoid Input.GetAxis("Mouse X/Y")
        // here: its built-in smoothing carries residual velocity that fires on the
        // first frame after a click, which is the other half of the start jerk.
        // Seeded above, so frame 1 contributes a zero delta.
        Vector3 mousePos = Input.mousePosition;
        Vector2 delta    = (Vector2)(mousePos - _lastMousePos);
        _lastMousePos    = mousePos;

        _targetYaw   += delta.x * orbitSensitivity;   // degrees per pixel
        _targetPitch -= delta.y * orbitSensitivity;
        _targetPitch  = Mathf.Clamp(_targetPitch, pitchMin, pitchMax);

        // Reconstruct the camera position from yaw/pitch/radius around the focus.
        Quaternion rot    = Quaternion.Euler(_targetPitch, _targetYaw, 0f);
        Vector3    offset = rot * new Vector3(0f, 0f, -_orbitRadius);
        _targetPosition   = ClampXZ(_orbitFocusPoint + offset);
    }

    // =========================================================================
    // Middle Mouse Drag Pan
    // =========================================================================
    void HandleMiddleMouseDrag()
    {
        if (Input.GetMouseButtonDown(2))
        {
            // Only begin the drag if we got a valid world point under the cursor.
            if (TryScreenPointToGroundPoint(Input.mousePosition, out Vector3 origin))
            {
                _dragging = true;
                _dragGroundOrigin = origin;   // the world point we keep under the cursor
            }
        }

        if (Input.GetMouseButtonUp(2))
            _dragging = false;

        if (!_dragging) return;

        // Where is the ground under the cursor now? If the cursor left the view this
        // frame, skip cleanly (no garbage move, no warning) and resume when it returns.
        if (TryScreenPointToGroundPoint(Input.mousePosition, out Vector3 current))
        {
            Vector3 delta = _dragGroundOrigin - current;
            _targetPosition = ClampXZ(_targetPosition + delta);
        }
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
        if (!IsFinite(mousePos)) return;   // cursor off-view: nothing to pan toward
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

        _targetPosition = ClampXZ(_targetPosition + panDir * speed * Time.deltaTime);
    }

    // =========================================================================
    // Apply smooth transform
    // =========================================================================
    void ApplyTransform()
    {
        float posLerp = (_orbiting ? orbitSmoothing : panSmoothing) * Time.deltaTime;

        transform.position = Vector3.Lerp(transform.position, _targetPosition, posLerp);

        Quaternion targetRot = Quaternion.Euler(_targetPitch, _targetYaw, 0f);
        transform.rotation   = Quaternion.Slerp(
            transform.rotation, targetRot, Time.deltaTime * orbitSmoothing);
    }

    // =========================================================================
    // Helpers
    // =========================================================================
    private static bool IsFinite(float v) => !(float.IsNaN(v) || float.IsInfinity(v));

    private static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);

    /// <summary>Clamp only the X/Z of a position into the pan bounds (leaves Y alone).</summary>
    private Vector3 ClampXZ(Vector3 p)
    {
        p.x = Mathf.Clamp(p.x, panBoundsMin.x, panBoundsMax.x);
        p.z = Mathf.Clamp(p.z, panBoundsMin.y, panBoundsMax.y);
        return p;
    }

    private void ClampPosition() => _targetPosition = ClampXZ(_targetPosition);

    /// <summary>Casts a ray from the camera along its forward to the Y=0 plane.</summary>
    Vector3 GetGroundPoint()
    {
        Ray   ray   = new Ray(transform.position, transform.forward);
        Plane plane = new Plane(Vector3.up, Vector3.zero);

        if (plane.Raycast(ray, out float dist))
        {
            Vector3 p = ray.GetPoint(dist);
            if (IsFinite(p)) return p;
        }

        // Fallback: project straight down from current position
        return new Vector3(transform.position.x, 0f, transform.position.z);
    }

    /// <summary>
    /// Casts a ray from the camera through a screen-space pixel to the Y=0 ground
    /// plane. Returns false (without touching the camera) when the screen point is
    /// non-finite or outside the camera's pixel rect — Camera.ScreenPointToRay logs
    /// "Screen position out of view frustum" for such input, which is exactly the
    /// (inf,-inf) Input.mousePosition produced when the cursor is off the Game view.
    /// Uses the camera's ACTUAL current transform so drag/zoom math is consistent.
    /// </summary>
    bool TryScreenPointToGroundPoint(Vector3 screenPos, out Vector3 point)
    {
        point = default;

        if (!IsFinite(screenPos)) return false;

        Rect r = _cam.pixelRect;
        if (screenPos.x < r.xMin || screenPos.x > r.xMax ||
            screenPos.y < r.yMin || screenPos.y > r.yMax) return false;

        Ray   ray   = _cam.ScreenPointToRay(screenPos);
        Plane plane = new Plane(Vector3.up, Vector3.zero);

        if (plane.Raycast(ray, out float dist))
        {
            Vector3 p = ray.GetPoint(dist);
            if (IsFinite(p)) { point = p; return true; }
        }
        return false;
    }
}
