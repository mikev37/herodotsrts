using UnityEditor;

// ===========================================================================
// Inspector for BuildingDefinition: shows ONLY the fields that mean something
// for an Immobile entity. Everything inherited from UnitDefinition that drives
// behavior, locomotion, melee/ranged combat, or hero status is hidden — those
// systems never run on a building (BehaviorSystem/SteeringSystem/SlopeSystem
// all exclude Immobile), so showing the fields would just invite confusion.
// Reset() on BuildingDefinition sets the hidden fields to inert defaults.
// ===========================================================================
[CustomEditor(typeof(BuildingDefinition))]
public class BuildingDefinitionEditor : Editor
{
    private static readonly string[] Shown =
    {
        // identity & visuals
        "displayName", "viewPrefab",
        // footprint & placement
        "footprintX", "footprintZ", "maxHeightDelta",
        // survivability
        "maxHealth", "deathAnimSeconds", "armor", "shield", "receivesAbilities",
        // physical (ramming impacts read the building's mass)
        "mass",
        // abilities a building could grant/cast in the future
        "maxMana", "manaRegen", "abilities",
    };

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        foreach (var name in Shown)
        {
            var prop = serializedObject.FindProperty(name);
            if (prop != null) EditorGUILayout.PropertyField(prop, true);
        }
        serializedObject.ApplyModifiedProperties();
    }
}
