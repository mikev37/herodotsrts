using UnityEngine;

public class SimpleProceduralWalker : MonoBehaviour {
    [System.Serializable]
    public class Leg {
        public Transform foot;
        public Vector3 homeOffset;

        public Vector3 currentPos;
        public Vector3 stepFrom;
        public Vector3 targetPos;
        public bool stepping;
        public float stepT;
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

    [Header("Step")]
    public float stepHeight = 0.25f;
    public float stepSpeedMultiplier = 3f;
    public float minStepSpeed = 2f;

    public float stepStride = 1;

    public int _currentIndex;
    public Vector3 _lastPos;
    public Vector3 _velocity;
    public Vector3 _planarVel;
    public float _speedT;

    void Start() {
        _lastPos = transform.position;
        _currentIndex = 0;

        foreach (var leg in legs) {
            leg.currentPos = transform.TransformPoint(leg.homeOffset);
            leg.stepFrom = leg.currentPos;
            leg.targetPos = leg.currentPos;
            leg.stepping = false;
            leg.stepT = 1f;
        }
    }

    void Update() {
        _velocity = (transform.position - _lastPos) / Mathf.Max(Time.deltaTime, 0.0001f);
        _lastPos = transform.position;
        _planarVel = new Vector3(_velocity.x, 0f, _velocity.z);
        _speedT = Mathf.Clamp01(_planarVel.magnitude / maxSpeed);

        ProcessLegs();
    }

    void ProcessLegs() {
        // Advance all active steps
        foreach (var leg in legs) {
            if (!leg.stepping) continue;

            float distanceTraveled = _planarVel.magnitude * Time.deltaTime;
            leg.stepT += distanceTraveled / stepStride;
            float t = Mathf.Clamp01(leg.stepT);

            Vector3 pos = Vector3.Lerp(leg.stepFrom, leg.targetPos, t);
            pos.y += Mathf.Sin(t * Mathf.PI) * stepHeight;
            leg.currentPos = pos;

            if (leg.stepT >= 1f) {
                leg.stepping = false;
                leg.currentPos = leg.targetPos;
            }
        }

        foreach (var leg in legs)
            if (leg.foot != null)
                leg.foot.position = leg.currentPos;

        // Is any foot in front of the body relative to velocity?
        Vector3 forward = _planarVel.magnitude > 0.01f ? _planarVel.normalized : transform.forward;
        int forwardCount = 0;
        foreach (var leg in legs)
            if (Vector3.Dot(leg.targetPos - (transform.position + _planarVel * Time.deltaTime), forward) > minForward)
                forwardCount++;

        if (forwardCount > 0) return;


        // Begin step on current candidate
        Leg candidate = legs[_currentIndex];
        if (candidate.stepping) return;

        float fwdExtent = Mathf.Lerp(minForward, maxForward, _speedT);
        float latExtent = Mathf.Lerp(minLateral, maxLateral, _speedT);
        float stepDistance = fwdExtent;
        float timeToLand = stepStride / Mathf.Max(_planarVel.magnitude, 0.01f);
        Vector3 worldHome = transform.TransformPoint(candidate.homeOffset) + _planarVel * timeToLand;
        if (Vector3.Distance(candidate.currentPos, worldHome) < stepDistance) return;


        
        Vector3 target = ComputeTarget(worldHome + _planarVel * timeToLand, _planarVel, fwdExtent, latExtent);


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
        Vector3 lateral = Vector3.Cross(Vector3.up, forward).normalized;

        float fwdDot = Mathf.Clamp(Vector3.Dot(vel.normalized, forward) * fwdExtent, -fwdExtent, fwdExtent);
        float latDot = Mathf.Clamp(Vector3.Dot(vel.normalized, lateral) * latExtent, -latExtent, latExtent);

        return worldHome + forward * fwdDot + lateral * latDot;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected() {
        if (legs == null) return;
        Vector3 fwd = Application.isPlaying
            ? (_planarVel.magnitude > 0.01f ? _planarVel.normalized : transform.forward)
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