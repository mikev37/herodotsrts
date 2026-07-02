using UnityEngine;

// On a resource node's VISUAL PREFAB (sibling of UnitView). UnitViewManager pushes
// the remaining fraction (Amounts/Capacity) here each frame, exactly like setHP.
// Wire it to whatever you like: an Animator float that blends full->depleted, and/
// or a full-model / husk-model swap so an emptied node becomes a stump.
public class NodeView : MonoBehaviour
{
    [Tooltip("Optional: receives a 1->0 float as the node depletes.")]
    [SerializeField] private Animator animator;
    [SerializeField] private string fillParam = "Fill";
    [Tooltip("Optional: shown while resources remain.")]
    [SerializeField] private GameObject fullModel;
    [Tooltip("Optional: the husk/stump shown when emptied.")]
    [SerializeField] private GameObject huskModel;

    [Range(0f, 1f)] public float Fill = 1f;

    public void Bind() { Fill = 1f; Apply(); }

    public void SetFill(float f01)   // 1 = untouched, 0 = empty/husk
    {
        Fill = Mathf.Clamp01(f01);
        Apply();
    }

    private void Apply()
    {
        if (animator != null) animator.SetFloat(fillParam, Fill);
        if (fullModel != null) fullModel.SetActive(Fill > 0f);
        if (huskModel != null) huskModel.SetActive(Fill <= 0f);
    }
}
