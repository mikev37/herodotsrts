using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// ABILITY FIELD SYSTEM — recipient-side application of ability effects.
//
// Gathers the (few) active AbilityField entities, refreshes hero-anchored ones
// to follow the caster, then a parallel job has each unit test which fields it's
// inside and stamp those fields' modifiers into its ActiveModifier buffer:
//   * PersistentArea: refresh the entry's timer while inside (leaving lets it
//     expire -> "removed on leave"); same field doesn't duplicate (keyed by id).
//   * CastOnce: add a fresh entry (stacks across casts); the field is then
//     destroyed this frame so it stamps exactly once.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SpatialHashSystem))]
public partial struct AbilityFieldSystem : ISystem
{
    private struct FieldData { public AbilityField Field; public int ModStart, ModCount; }

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        float dt = SystemAPI.Time.DeltaTime;
        var xforms = SystemAPI.GetComponentLookup<LocalTransform>(true);

        var fields = new NativeList<FieldData>(8, state.WorldUpdateAllocator);
        var mods = new NativeList<FieldModifier>(32, state.WorldUpdateAllocator);
        var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                           .CreateCommandBuffer(state.WorldUnmanaged);

        foreach (var (fieldRW, buf, entity) in
                 SystemAPI.Query<RefRW<AbilityField>, DynamicBuffer<FieldModifier>>().WithEntityAccess())
        {
            var f = fieldRW.ValueRO;

            // Hero-anchored fields follow their caster.
            if (f.Anchor == AnchorType.Hero && xforms.HasComponent(f.AnchorEntity))
            {
                var t = xforms[f.AnchorEntity];
                f.Center = new float2(t.Position.x, t.Position.z);
                float3 fwd = math.forward(t.Rotation);
                f.Dir = math.normalizesafe(new float2(fwd.x, fwd.z), new float2(0f, 1f));
            }

            int start = mods.Length;
            for (int k = 0; k < buf.Length; k++) mods.Add(buf[k]);
            fields.Add(new FieldData { Field = f, ModStart = start, ModCount = buf.Length });

            // Lifetime: CastOnce stamps this frame then dies; PersistentArea ticks down.
            if (f.Mode == ApplyMode.CastOnce)
            {
                ecb.DestroyEntity(entity);
            }
            else
            {
                f.Lifetime -= dt;
                if (f.Lifetime <= 0f) ecb.DestroyEntity(entity);
                else fieldRW.ValueRW = f;
            }
        }

        if (fields.Length == 0) return;

        state.Dependency = new StampJob
        {
            Fields = fields.AsArray(),
            Mods = mods.AsArray(),
        }.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct StampJob : IJobEntity
    {
        [ReadOnly] public NativeArray<FieldData> Fields;
        [ReadOnly] public NativeArray<FieldModifier> Mods;

        private void Execute(ref DynamicBuffer<ActiveModifier> active, in LocalTransform xform, in Team team)
        {
            float2 pos = new float2(xform.Position.x, xform.Position.z);

            for (int fi = 0; fi < Fields.Length; fi++)
            {
                var fd = Fields[fi];
                var f = fd.Field;

                bool ally = team.Value == f.Team;
                if (f.Affects == AffectFilter.Enemies && ally) continue;
                if (f.Affects == AffectFilter.Allies && !ally) continue;
                if (!InShape(f, pos)) continue;

                for (int s = 0; s < fd.ModCount; s++)
                    StampOrRefresh(ref active, f, s, Mods[fd.ModStart + s]);
            }
        }

        private static bool InShape(in AbilityField f, float2 pos)
        {
            if (f.Shape == ShapeType.Circle)
                return math.distancesq(pos, f.Center) <= f.Radius * f.Radius;

            // Line: a rectangle of f.Length forward along Dir, f.Width wide.
            float2 rel = pos - f.Center;
            float along = math.dot(rel, f.Dir);
            if (along < 0f || along > f.Length) return false;
            float2 side = new float2(-f.Dir.y, f.Dir.x);
            return math.abs(math.dot(rel, side)) <= f.Width * 0.5f;
        }

        private static void StampOrRefresh(ref DynamicBuffer<ActiveModifier> active,
                                           in AbilityField f, int slot, in FieldModifier fm)
        {
            if (f.Mode == ApplyMode.PersistentArea)
            {
                for (int i = 0; i < active.Length; i++)
                {
                    if (active[i].Source == f.FieldId && active[i].Slot == slot)
                    {
                        var m = active[i];
                        m.Remaining = math.max(m.Remaining, f.RefreshWindow);
                        active[i] = m;
                        return;
                    }
                }
            }

            active.Add(new ActiveModifier
            {
                Source = f.FieldId,
                AbilityId = f.AbilityId,
                Slot = slot,
                Target = fm.Target,
                Delta = fm.Delta,
                Mode = fm.Mode,
                Revert = fm.Revert,
                BoolValue = fm.BoolValue,
                CapMode = fm.CapMode,
                CapRef = fm.CapRef,
                CapValue = fm.CapValue,
                Remaining = f.Mode == ApplyMode.PersistentArea ? f.RefreshWindow : fm.Duration,
                Applied = 0,
                Offset = 0f,
            });
        }
    }
}
