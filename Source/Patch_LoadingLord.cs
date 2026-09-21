using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 让机械族与亚人在装载界面点确定后自动走向穿梭机：挂钩 TransporterUtility.MakeLordsAsAppropriate。
    ///
    /// 联动关系：
    ///   原版 Dialog_LoadTransporters.TryAccept（玩家点确定）
    ///     -> AssignTransferablesToRandomTransporters：把勾选结果写进 CompTransporter.leftToLoad
    ///       -> MakeLordsAsAppropriate：给被指派的人建一个 LordJob_LoadAndEnterTransporters 领主
    ///         -> LordToil_LoadAndEnterTransporters.UpdateAllDuties 给领主里每个单位上 LoadAndEnterTransporters 职责
    ///           -> 该职责的思考树依次尝试 JobGiver_LoadTransporters（搬东西）与 JobGiver_EnterTransporter（自己进舱）
    ///
    /// 原版漏掉了谁：原版筛选用的是 (IsColonist || IsColonyMechPlayerControlled)，
    /// 于是亚人（食尸鬼一类，IsColonist 为 false）和未被监督的自主机械族（IsColonyMechPlayerControlled 为 false）
    /// 即使被玩家勾上也拿不到职责，只会站在原地。
    ///
    /// 本补丁用「后缀 + 自己算名单」的方式补齐：
    ///   候选名单不是照抄参数里的 pawns，而是直接读 leftToLoad——
    ///   因为 CheckForErrors 那处补丁会改动传进来的参数，而 leftToLoad 才是玩家真正提交的订单。
    ///
    /// 注意：
    /// 1. 原版在同一方法里会先把自己名单之外的单位踢出领主，而机械族与亚人恰好不在原版名单里，
    ///    所以每一轮本补丁都会「先被踢、再加回」。影响仅限于重复点确定时会打断一次走路，不会丢失指派。
    /// 2. 加回领主时照抄原版的三步：先把单位从原领主摘除，再 AddPawn，最后打断当前工作让它立刻接受新职责。
    /// 3. 只认「玩家殖民者远征」的任务穿梭机（CargoPolicy.IsPlayerExpeditionShuttle）；
    ///    许可穿梭机、玩家自有穿梭机，以及来接走暂住客人的款待类穿梭机一律保持原样。
    /// </summary>
    [HarmonyPatch(typeof(TransporterUtility), nameof(TransporterUtility.MakeLordsAsAppropriate))]
    internal static class Patch_MakeLordsAsAppropriate
    {
        private static readonly List<Pawn> tmpAssignedCargo = new List<Pawn>();

        [HarmonyPostfix]
        private static void Postfix(List<CompTransporter> transporters, Map map)
        {
            if (transporters == null || transporters.Count == 0 || map == null)
            {
                return;
            }
            CompShuttle shuttle = transporters[0].parent.TryGetComp<CompShuttle>();
            if (!CargoPolicy.IsPlayerExpeditionShuttle(shuttle))
            {
                return;
            }
            int groupID = transporters[0].groupID;
            CargoPolicy.CollectAssignedCargo(transporters, CargoPolicy.CanJoinLoadingLord, tmpAssignedCargo);
            Lord lord = TransporterUtility.FindLord(groupID, map);
            if (tmpAssignedCargo.Count == 0)
            {
                if (lord != null)
                {
                    RemoveOurCargoNotIn(lord, tmpAssignedCargo);
                }
                return;
            }
            if (lord == null)
            {
                lord = LordMaker.MakeNewLord(Faction.OfPlayer, new LordJob_LoadAndEnterTransporters(groupID), map);
                if (lord == null)
                {
                    return;
                }
            }
            for (int i = 0; i < tmpAssignedCargo.Count; i++)
            {
                Pawn pawn = tmpAssignedCargo[i];
                if (lord.ownedPawns.Contains(pawn))
                {
                    continue;
                }
                pawn.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.ForcedToJoinOtherLord);
                lord.AddPawn(pawn);
                pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
            }
            RemoveOurCargoNotIn(lord, tmpAssignedCargo);
        }

        /// <summary>把本模组的货物里「已经不再被指派」的单位退出装载领主，避免它们留在领主里空转。</summary>
        private static void RemoveOurCargoNotIn(Lord lord, List<Pawn> keep)
        {
            for (int i = lord.ownedPawns.Count - 1; i >= 0; i--)
            {
                Pawn pawn = lord.ownedPawns[i];
                if (CargoPolicy.IsOurCargo(pawn) && !keep.Contains(pawn))
                {
                    lord.Notify_PawnLost(pawn, PawnLostCondition.NoLongerEnteringTransportPods);
                }
            }
        }
    }
}
