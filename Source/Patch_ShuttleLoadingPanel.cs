using HarmonyLib;
using RimWorld;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 补出被原版藏掉的装载面板：挂钩 CompShuttle.ShowLoadingGizmos 属性读取器。
    ///
    /// 联动关系：
    ///   玩家选中穿梭机 -> CompTransporter.CompGetGizmosExtra
    ///     -> 开头一句 if (Shuttle != null && !Shuttle.ShowLoadingGizmos) yield break;
    ///        于是这台穿梭机「装载 / 卸载 / 取消装载 / 装载设置」整排按钮一次性消失，
    ///        带载重条的装载面板 Dialog_LoadTransporters 也就打不开；
    ///   CompShuttle.CompGetGizmosExtra 画「自动装载」开关时同样先问 ShowLoadingGizmos。
    ///
    /// 原版为什么会答「不」：ShowLoadingGizmos 先看当前船务有没有允许玩家操作
    ///   （TransportShip.ShowGizmos 就是 curJob.ShowGizmos），再看穿梭机的阵营是不是玩家。
    ///   穿梭机停在地图上、但船务队列已经空掉的时候 curJob 为 null，ShowGizmos 随即为 false，
    ///   面板与按钮同时消失。文化 + 王权的「古代遗迹」任务就是这样：穿梭机落地后队列是空的，
    ///   要等玩家装完货、任务发出 SendShuttleAway 信号才开始排队去程与返程的船务。
    ///   原版在这种场合只能靠右键让单位自己走进机舱，装载面板根本打不开。
    ///
    /// 做法：只在原版返回 false 时改写，并且只对「任务交给玩家自己装载」的穿梭机改写，
    ///   当前船务刻意藏按钮时（卸载中、加冕典礼）同样放过，判定全部在 QuestShuttleControls 里。
    ///
    /// 注意：
    /// 1. 本补丁只放行按钮，不改动任何装载判定：谁能上机仍由 CompShuttle.IsAllowed 决定，
    ///    载重、可达性、任务要求的人员与物品一律照原版。
    /// 2. 面板打开之后，设置里的人数上限（Patch_CountLimit）与界面分区（Patch_LoadDialogSections）
    ///    照常生效，机械族与亚人因此在这类任务里也能勾选、也能随行。
    /// 3. 设置项「任务穿梭机始终显示装载面板」关闭时本补丁完全不介入，退回原版口径。
    /// </summary>
    [HarmonyPatch(typeof(CompShuttle), "get_ShowLoadingGizmos")]
    internal static class Patch_CompShuttle_ShowLoadingGizmos
    {
        [HarmonyPostfix]
        private static void Postfix(CompShuttle __instance, ref bool __result)
        {
            if (__result || !QuestShuttleControls.LoadingPanelEnabled)
            {
                return;
            }
            if (QuestShuttleControls.ForceLoadingPanel(__instance))
            {
                __result = true;
            }
        }
    }
}
