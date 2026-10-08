using DungeonRunners.Combat;
using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    public static class SkillEffectTracker
    {
        public static void RecordMonsterSelection(Monster source, Monster target, MonsterActiveSkillRuntime skill, int targetType, int selectedIndex, int candidateCount, uint raw, uint tick)
        {
            if (!ServerDiagnostics.IsEnabled("skillEffectTracking"))
                return;
            Debug.LogError($"[SKILL-EFFECT-TRACK] phase=select lane=monster source={source?.EntityId ?? 0} target={target?.EntityId ?? 0} skill={skill?.Path ?? "none"} targetType={targetType} selectedIndex={selectedIndex} candidates={candidateCount} rngRaw=0x{raw:X8} tick={tick}");
        }

        public static void RecordValidated(string lane, uint sourceEntityId, string skillPath, uint targetEntityId, string effectFamily, uint tick)
        {
            if (!ServerDiagnostics.IsEnabled("skillEffectTracking"))
                return;
            Debug.LogError($"[SKILL-EFFECT-TRACK] phase=validate result=accepted lane={lane ?? "unknown"} source={sourceEntityId} target={targetEntityId} skill={skillPath ?? "none"} family={effectFamily ?? "unknown"} tick={tick}");
        }

        public static void RecordRejected(string lane, uint sourceEntityId, string skillPath, uint targetEntityId, string reason, uint tick)
        {
            if (!ServerDiagnostics.IsEnabled("skillEffectTracking"))
                return;
            Debug.LogError($"[SKILL-EFFECT-TRACK] phase=validate result=rejected lane={lane ?? "unknown"} source={sourceEntityId} target={targetEntityId} skill={skillPath ?? "none"} reason={reason ?? "unknown"} tick={tick}");
        }

        public static void RecordEffectNode(string lane, uint sourceEntityId, uint targetEntityId, string skillPath, string effectPath, int packageEntryId, int siblingOrder, int depth, string family, uint tick)
        {
            if (!ServerDiagnostics.IsEnabled("skillEffectTracking"))
                return;
            Debug.LogError($"[SKILL-EFFECT-TRACK] phase=execute lane={lane ?? "unknown"} source={sourceEntityId} target={targetEntityId} skill={skillPath ?? "none"} effect={effectPath ?? "none"} entry={packageEntryId} sibling={siblingOrder} depth={depth} family={family ?? "unknown"} tick={tick}");
        }

        public static void RecordRessurect(Monster source, Monster target, string effectPath, uint oldHPWire, uint newHPWire, uint oldFlags, uint newFlags, byte oldStockState, byte newStockState, uint tick)
        {
            if (!ServerDiagnostics.IsEnabled("skillEffectTracking"))
                return;
            Debug.LogError($"[SKILL-EFFECT-TRACK] phase=resurrect lane=monster source={source?.EntityId ?? 0} target={target?.EntityId ?? 0} skill={source?.SelectedActiveSkill?.Path ?? "none"} effect={effectPath ?? "none"} hp={oldHPWire}->{newHPWire} flags=0x{oldFlags:X8}->0x{newFlags:X8} stockState={oldStockState}->{newStockState} tick={tick}");
        }

        public static void RecordPlayerGraph(uint sourceEntityId, uint targetEntityId, string skillPath, string targetType, int nodeCount, string result, string reason, uint tick)
        {
            if (!ServerDiagnostics.IsEnabled("skillEffectTracking"))
                return;
            Debug.LogError($"[SKILL-EFFECT-TRACK] phase=player-graph source={sourceEntityId} target={targetEntityId} skill={skillPath ?? "none"} targetType={targetType ?? "unknown"} nodes={nodeCount} result={result ?? "unknown"} reason={reason ?? "none"} tick={tick}");
        }
    }
}
