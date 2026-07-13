using UnityEngine;

// ===========================================================================
// Lives on a unit/building VISUAL PREFAB (sibling of UnitView), NOT on the entity.
// UnitViewManager.LateUpdate pushes the entity's ResourceBank amounts here each frame,
// exactly as it pushes Health into UnitView.setHP — sim never touches a view.
//
// Wire up whatever readout you like (carry pips on a harvester, a stockpile bar on
// a depot, the capital's totals) to the public fields / SetAmounts hook.
// ===========================================================================
public class ResourceView : MonoBehaviour
{
    [Tooltip("Optional world-space text/bar driven by the amounts (assign in the prefab).")]
    [SerializeField] private TextMesh label;

    // Last pushed amounts, indexed like ResourceType: 0 Gold, 1 Wood, 2 Food.
    public int Gold, Wood, Food;

    public void Bind()   // called when the view is pulled from the pool for reuse
    {
        Gold = Wood = Food = 0;
        if (label == null) label = GetComponentInChildren<TextMesh>();
        Render();
    }

    // Mirrors UnitView.setHP: a plain per-frame push from UnitViewManager.
    public void SetAmounts(int gold, int wood, int food)
    {
        Gold = gold; Wood = wood; Food = food;
        Render();
    }

    private void Render()
    {
        if (label != null)
        {
            // Show only the non-zero slots so a single-type carrier reads cleanly.
            string s = "";
            if (Gold > 0) s += $"G{Gold} ";
            if (Wood > 0) s += $"W{Wood} ";
            if (Food > 0) s += $"F{Food} ";
            label.text = s.TrimEnd();
        }
    }
}
