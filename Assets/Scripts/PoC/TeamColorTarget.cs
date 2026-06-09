using UnityEngine;

// ---------------------------------------------------------------------------
// Put this on a VIEW PREFAB to mark which renderers (or specific material slots)
// should be recolored to the team/commander color — e.g. cloth, banners, trim.
// Everything you DON'T list (skin, steel, leather) keeps its authored material
// untouched.
//
// Tinting is done with a MaterialPropertyBlock: we never assign `.material`
// (which would instantiate/replace the material) — we only push a color
// override onto the chosen slots. So shared materials are preserved and there's
// no per-unit material leak. If no slots are listed, nothing is touched.
// ---------------------------------------------------------------------------
public class TeamColorTarget : MonoBehaviour
{
    [System.Serializable]
    public struct Slot
    {
        public Renderer renderer;
        [Tooltip("Material slot to tint, or -1 for the whole renderer (all submeshes).")]
        public int materialIndex;
    }

    [Tooltip("Renderers / material slots that take the team color (cloth, flags, trim). " +
             "Leave skin/steel/leather out so they keep their own material.")]
    public Slot[] slots;

    [Tooltip("Shader color properties to set. URP uses _BaseColor, built-in uses _Color; " +
             "both are set by default and a missing one is harmless.")]
    public string[] colorProperties = { "_BaseColor", "_Color" };

    private MaterialPropertyBlock _mpb;

    public void Apply(Color color)
    {
        if (slots == null || slots.Length == 0) return;
        _mpb ??= new MaterialPropertyBlock();

        foreach (var s in slots)
        {
            if (s.renderer == null) continue;
            bool perSlot = s.materialIndex >= 0;

            // Read existing block first so we don't clobber other overrides.
            if (perSlot) s.renderer.GetPropertyBlock(_mpb, s.materialIndex);
            else         s.renderer.GetPropertyBlock(_mpb);

            for (int i = 0; i < colorProperties.Length; i++)
                _mpb.SetColor(colorProperties[i], color);

            if (perSlot) s.renderer.SetPropertyBlock(_mpb, s.materialIndex);
            else         s.renderer.SetPropertyBlock(_mpb);
        }
    }
}
