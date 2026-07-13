using UnityEngine;

// Lives on a building's VISUAL PREFAB (sibling of UnitView). UnitViewManager.LateUpdate
// pushes the entity's construction completion fraction here each frame (like
// setHP), so the building visibly rises/assembles as it's built and snaps to full
// when Construction is removed. Drive whatever you like off Progress01 (scale a
// "riser" transform, a dissolve shader param, a scaffold reveal, etc.).
public class ConstructionView : MonoBehaviour
{
    [Tooltip("Optional: scaled on Y from 0..1 as the building completes.")]
    [SerializeField] private Transform riser;
    [Range(0f, 1f)] public float Progress01 = 1f;

    public void Bind() { Progress01 = 0f; Apply(); }

    public void SetProgress(float f01)   // 0 = just placed, 1 = finished
    {
        Progress01 = Mathf.Clamp01(f01);
        Apply();
    }

    private void Apply()
    {
        if (riser != null)
        {
            var s = riser.localScale;
            riser.localScale = new Vector3(s.x, Mathf.Max(0.02f, Progress01), s.z);
        }
    }
}
