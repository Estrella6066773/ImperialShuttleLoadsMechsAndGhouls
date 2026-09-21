using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 修好装载界面的载重口径：挂钩 Dialog_LoadTransporters 的 MassUsage 属性读取器。
    ///
    /// 联动关系：
    ///   打开装载界面 -> DoWindowContents
    ///     -> CaravanUIUtility.DrawCaravanInfo(MassUsage, MassCapacity, ...)：顶上那条「质量 已装/上限」统计条；
    ///   TransferableOneWayWidget.DoRow 每画一行都会问 MassCapacity - MassUsage，用来标出「M&lt; / M&gt;」这些停止点；
    ///   玩家点确定 -> onAcceptButton -> CheckForErrors -> MassUsage > MassCapacity 时拒绝装载并闪红。
    ///   本补丁只往 MassUsage 的结果上加一个补正值，上面三处因此同时改用补正后的数字。
    ///
    /// 原版为什么算少：Dialog_LoadTransporters.MassUsage 里传给 CollectionsMassCalculator 的参数是
    ///   includePawnsMass: compShuttle == null || compShuttle.requiredColonistCount == 0，
    ///   而任务穿梭机的 requiredColonistCount 正是任务点名要带的殖民者数量（王权任务普遍大于 0）。
    ///   于是整船单位在这条统计条里只按「装备 + 随身物品」计重，自身体重被完全豁免：
    ///   210 公斤的机械族只记 20 公斤随身物，食尸鬼、奴隶、囚犯、动物同样只记随身物。
    ///   载重上限因此形同虚设，玩家把一整队机械族塞进 2000 公斤的机舱也不会看到统计条变色。
    ///   同一界面的每行重量是 TransferableOneWayWidget 按 includePawnsMassInMassUsage: true 画的（机械族显示 210），
    ///   所以有「行里 210、总数只加了 20」的矛盾。
    ///
    /// 补正口径：只要原版这次调用确实豁免了体重，就把「该计自身体重的单位」的 (自身体重 - 装备与随身物重) 补回去。
    ///   机械族、亚人（食尸鬼）、奴隶、囚犯、动物、访客等原版本来就该按体重计重的单位因此恢复原样；
    ///   两类刻意保持豁免：
    ///     1. 自由殖民者——任务点名要带的就是他们，豁免体重是任务穿梭机「装得下任务要带的人」的前提，
    ///        算上体重会让大殖民地执行「地图上所有殖民者都要上机」的任务时被载重卡死；
    ///     2. 任务点名的人与物（CompShuttle.IsRequired）——任务要求不能被体重挡住。
    ///   这两类恰好就是原版豁免本意覆盖的范围，因此「任务要带的人照旧、随行单位按实际体重算」。
    ///
    /// 注意：
    /// 1. requiredColonistCount 为 0 时原版本来就把所有单位按体重计重（includePawnsMass 为 true），
    ///    本补丁直接返回 0，绝无重复计算；许可穿梭机与玩家自有穿梭机走的就是这条路。
    /// 2. 这里只改「界面与点确定时的数字」。谁能上机仍由 CompShuttle.IsAllowed 决定，
    ///    能不能起飞仍由 AllRequiredThingsLoaded 决定，原版任务流程一步没动。
    /// 3. 补正值按当前勾选实时计算，不缓存：界面每次问 MassUsage 都是最新数字，
    ///    玩家勾选或取消机械族时统计条立刻跟着动。
    /// </summary>
    [HarmonyPatch(typeof(Dialog_LoadTransporters), "get_MassUsage")]
    internal static class Patch_LoadDialogMassUsage
    {
        [HarmonyPostfix]
        private static void Postfix(Dialog_LoadTransporters __instance, ref float __result)
        {
            __result += LoadDialogMass.ExtraPawnMass(__instance);
        }
    }

    /// <summary>
    /// 补正值的计算，供 Patch_LoadDialogMassUsage 调用。
    ///
    /// 注意：本类不改变任何原版状态，只读界面当前的候选清单与穿梭机的任务要求。
    /// </summary>
    internal static class LoadDialogMass
    {
        /// <summary>
        /// 界面当前勾选的单位里，被原版漏掉的那部分体重之和；不需要补正时返回 0。
        /// </summary>
        public static float ExtraPawnMass(Dialog_LoadTransporters dialog)
        {
            CompShuttle shuttle = LoadDialogAccess.QuestShuttleOf(dialog);
            if (shuttle == null || shuttle.requiredColonistCount == 0)
            {
                // 原版这次已经按体重计重（includePawnsMass 为 true），没有漏算，补正会让数字翻倍。
                return 0f;
            }
            List<TransferableOneWay> transferables = LoadDialogAccess.TransferablesOf(dialog);
            if (transferables == null)
            {
                return 0f;
            }
            float extra = 0f;
            for (int i = 0; i < transferables.Count; i++)
            {
                TransferableOneWay transferable = transferables[i];
                int count = transferable.CountToTransfer;
                if (count <= 0 || !(transferable.AnyThing is Pawn pawn))
                {
                    continue;
                }
                if (IsMassExempt(shuttle, pawn))
                {
                    continue;
                }
                extra += (pawn.GetStatValue(StatDefOf.Mass) - MassUtility.GearAndInventoryMass(pawn)) * (float)count;
            }
            return extra;
        }

        /// <summary>
        /// 这个单位的自身体重是否仍旧豁免（沿用原版口径，不补正）。
        /// 自由殖民者与任务点名要求的人就是这样两类，理由见 Patch_LoadDialogMassUsage 的说明。
        /// </summary>
        private static bool IsMassExempt(CompShuttle shuttle, Pawn pawn)
        {
            if (pawn.IsFreeColonist)
            {
                return true;
            }
            return shuttle.IsRequired(pawn);
        }
    }
}
