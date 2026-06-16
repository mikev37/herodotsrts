using UnityEngine;

public class SimpleProceduralWalker : MonoBehaviour {
    [System.Serializable]
    public class Leg {
        public Transform foot;
        [HideInInspector] public Vector3 homeOffset;

        [HideInInspector] public Vector3 currentPos;
        [HideInInspector] public Vector3 stepFrom;
        [HideInInspector] public Vector3 targetPos;
        [HideInInspector] public bool stepping;
        [HideInInspector] public float stepT;
    }

    public Leg[] legs;

    [Header("Forward Bounds")]
    public float minForward = 0.2f;
    public float maxForward = 0.8f;

    [Header("Lateral Bounds")]
    public float minLateral = 0.1f;
    public float maxLateral = 0.4f;

    [Header("Speed Reference")]
    public float maxSpeed = 8f;

    [Header("Idle")]
    public float idleThreshold = 0.05f;
    public float idleReturnSpeed = 3f;

    [Header("Step")]
    public float stepHeight = 0.25f;
    public float stepStride = 1f;
    public AnimationCurve stepHeightCurve;

    public int _currentIndex;
    public Vector3 _lastPos;
    public Vector3 _velocity;
    public float _speedT;

    void Start() {
        _lastPos = transform.position;
        _currentIndex = 0;

        foreach (var leg in legs) {
            leg.homeOffset = leg.foot.localPosition;
            leg.currentPos = leg.foot.position;
            leg.stepFrom = leg.currentPos;
            leg.targetPos = leg.currentPos;
            leg.stepping = false;
            leg.stepT = 1f;
        }
    }

    void Update() {
        Vector3 _rawvelocity = (transform.position - _lastPos) / Mathf.Max(Time.deltaTime, 0.0001f);
        if (_rawvelocity.magnitude != 0)
            _velocity = _rawvelocity;
        _lastPos = transform.position;
        _speedT = Mathf.Clamp01(_velocity.magnitude / maxSpeed);

        ProcessLegs();
    }

    void ProcessLegs() {
        if (_velocity.magnitude < idleThreshold) {
            foreach (var leg in legs) {
                leg.stepping = false;
                if (leg.foot != null) {
                    leg.foot.localPosition = Vector3.Lerp(leg.foot.localPosition, leg.homeOffset, Time.deltaTime * idleReturnSpeed);
                    leg.currentPos = leg.foot.position;
                }
            }
            _currentIndex = 0;
        } else {
            foreach (var leg in legs) {
                if (!leg.stepping) continue;

                float distanceTraveled = Mathf.Max(_velocity.magnitude, idleThreshold) * Time.deltaTime;
                leg.stepT += distanceTraveled / stepStride;
                float t = Mathf.Clamp01(leg.stepT);

                Vector3 pos = Vector3.Lerp(leg.stepFrom, leg.targetPos, t);
                pos.y += stepHeightCurve.Evaluate(t) * stepHeight;
                leg.currentPos = pos;

                if (leg.stepT >= 1f) {
                    leg.stepping = false;
                    leg.currentPos = leg.targetPos;
                }
            }

            foreach (var leg in legs)
                if (leg.foot != null)
                    leg.foot.position = leg.currentPos;
        }

        Vector3 forward = _velocity.magnitude > 0.01f ? _velocity.normalized : transform.forward;
        int forwardCount = 0;
        foreach (var leg in legs)
            if (Vector3.Dot(leg.targetPos - (transform.TransformPoint(leg.homeOffset) + _velocity * Time.deltaTime), forward) > minForward)
                forwardCount++;

        if (forwardCount >= Mathf.CeilToInt(legs.Length / 2f)) return;

        Leg candidate = legs[_currentIndex];
        if (candidate.stepping) return;

        float fwdExtent = Mathf.Lerp(minForward, maxForward, _speedT);
        float latExtent = Mathf.Lerp(minLateral, maxLateral, _speedT);
        float timeToLand = stepStride / Mathf.Max(_velocity.magnitude, 0.01f);

        Vector3 worldHome = transform.TransformPoint(candidate.homeOffset) + _velocity * timeToLand;
        if (Vector3.Distance(candidate.currentPos, worldHome) < fwdExtent) return;

        Vector3 target = ComputeTarget(worldHome, _velocity, fwdExtent, latExtent);

        BeginStep(candidate, target);
        _currentIndex = (_currentIndex + 1) % legs.Length;
    }

    void BeginStep(Leg leg, Vector3 target) {
        if (leg.stepping)
            leg.currentPos = new Vector3(leg.currentPos.x, transform.position.y, leg.currentPos.z);

        leg.stepFrom = leg.currentPos;
        leg.targetPos = target;
        leg.stepping = true;
        leg.stepT = 0f;
    }

    Vector3 ComputeTarget(Vector3 worldHome, Vector3 vel, float fwdExtent, float latExtent) {
        Vector3 forward = vel.magnitude > 0.01f ? vel.normalized : transform.forward;
        Vector3 lateral = transform.right;

        float fwdDot = Mathf.Clamp(Vector3.Dot(vel, forward) * fwdExtent, -fwdExtent, fwdExtent);
        float latDot = Mathf.Clamp(Vector3.Dot(vel, lateral) * latExtent, -latExtent, latExtent);

        return worldHome + forward * fwdDot + lateral * latDot;
    }

    float GetTerrainHeight(Vector3 pos) {
        if (Terrain.activeTerrain == null) return pos.y;
        return Terrain.activeTerrain.SampleHeight(pos);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected() {
        if (legs == null) return;
        Vector3 fwd = Application.isPlaying
            ? (_velocity.magnitude > 0.01f ? _velocity.normalized : transform.forward)
            : transform.forward;

        for (int i = 0; i < legs.Length; i++) {
            var leg = legs[i];
            Vector3 worldHome = transform.TransformPoint(leg.homeOffset);

            Gizmos.color = i == _currentIndex ? Color.red : Color.green;
            Gizmos.DrawWireSphere(worldHome, 0.06f);

            if (!Application.isPlaying) continue;
            Gizmos.color = leg.stepping ? Color.yellow : Color.cyan;
            Gizmos.DrawWireSphere(leg.currentPos, 0.05f);
            Gizmos.DrawLine(transform.position, leg.currentPos);
        }

        Gizmos.color = new Color(1f, 1f, 0f, 0.2f);
        Gizmos.DrawRay(transform.position, fwd * maxForward);
    }
#endif
}