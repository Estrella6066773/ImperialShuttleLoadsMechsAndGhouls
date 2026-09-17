using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 让被指派的机械族与亚人「只登机、不干活」：挂钩 LoadTransportersJobUtility.HasJobOnTransporter。
    ///
    /// 联动关系：
    ///   LordToil_LoadAndEnterTransporters 给领主里每个单位发同一个 LoadAndEnterTransporters 职责，
    ///   该职责的思考树顺序是「满足急迫需求 -> JobGiver_LoadTransporters（搬货）-> 清空随身物品 -> JobGiver_EnterTransporter（自己进舱）」。
    ///   JobGiver_LoadTransporters 又只问一句 LoadTransportersJobUtility.HasJobOnTransporter，本补丁就在这里回答「不干」。
    ///
    /// 为什么要拦：机械族有 Manipulation、食尸鬼也能做粗活，原版会让它们先去搬东西——
    /// 包括「把同伴抱上机」（原版对非自由殖民者、非机械族的单位会交给别人抱）。
    /// 结果是它们忙着搬别人，自己的登机时间被无限推后，与「点了确定就该上机」的预期不符。
    /// 拦掉之后，思考树自然落到 JobGiver_EnterTransporter，
    /// 它按 leftToLoad 找到「哪台穿梭机在等自己」，把单位自己走进去。
    ///
    /// 注意：
    /// 1. 只拦「我自己还在等待装载名单里」的情况（JobGiver_EnterTransporter.FindMyTransporter 能找得到自己）。
    ///    一旦进舱，原版会把它从 leftToLoad 移除，本补丁立刻不再生效，此时它已经在机舱里，也够不着外面，谈不上被误伤。
    /// 2. 殖民者、囚犯、动物这些原版会搬运的单位完全不受影响。
    /// 3. 若玩家右键手动命令某个机械族去搬东西，走的是其它工作入口，不经过这里。
    /// </summary>
    [HarmonyPatch(typeof(LoadTransportersJobUtility), nameof(LoadTransportersJobUtility.HasJobOnTransporter))]
    internal static class Patch_HasJobOnTransporter
    {
        private static readonly List<CompTransporter> tmpGroup = new List<CompTransporter>();

        [HarmonyPrefix]
        private static bool Prefix(Pawn pawn, ref bool __result)
        {
            if (!CargoPolicy.IsOurCargo(pawn))
            {
                return true;
            }
            PawnDuty duty = pawn.mindState?.duty;
            if (duty == null || duty.transportersGroup < 0 || pawn.Map == null)
            {
                return true;
            }
            tmpGroup.Clear();
            TransporterUtility.GetTransportersInGroup(duty.transportersGroup, pawn.Map, tmpGroup);
            if (JobGiver_EnterTransporter.FindMyTransporter(tmpGroup, pawn) == null)
            {
                return true;
            }
            __result = false;
            return false;
        }
    }

    // 「装载界面点确定时的人数上限」原先也挂在本文件，现已挪到 Patch_CountLimit.cs，
    // 与右键进舱时的人数上限检查放在一起，两者共用 CargoPolicy.CountsTowardShuttleLimit 这套设置。
}
