using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 在装载界面补出原版看不见的分区：挂钩 Dialog_LoadTransporters.CalculateAndRecacheTransferables。
    ///
    /// 联动关系：
    ///   打开装载界面（或点重置）-> CalculateAndRecacheTransferables
    ///     -> AddPawnsToTransferables：用 TransporterUtility.AllSendablePawns 生成候选清单（此时机械族与亚人已经因为放行补丁在名单里）
    ///       -> CaravanUIUtility.AddPawnsSections：把候选人按「殖民者 / 奴隶 / 囚犯 / 可俘虏 / 动物 / 机械族 / 实体」分成几个分区
    ///         -> TransferableOneWayWidget.FillMainRect：只画有内容的分区，不在任何分区里的单位等于隐身
    ///
    /// 原版漏掉了谁：分区条件比放行条件更窄——
    ///   机械族分区要求「有监督者且状态为已监督」，未被监督的自主机械族不在其中；
    ///   实体分区要求 mutant.Def.canTravelInCaravan 为真，而食尸鬼在原版设定里明确是 false。
    ///   于是这些单位即使能勾（AllSendablePawns 有它们），界面上也没有它们的行。
    ///
    /// 本补丁在界面重建之后补两个分区，只收「原版分区不会显示」的本模组货物，
    /// 用 CargoPolicy.HasVanillaPawnSection 逐条比照原版条件，确保不会出现重复行。
    ///
    /// 注意：
    /// 1. 分区为空就不加，避免 TransferableOneWayWidget 在计算滚动高度时给空标题留出多余空白。
    /// 2. 只对任务穿梭机生效：组建远征队界面（Dialog_FormCaravan）用的是同一个 AddPawnsSections，
    ///    分组补丁挂在装载界面的私有方法上，不会波及远征队，机械族与亚人在远征队里依旧按原版规则显示。
    /// 3. transferables / pawnsTransfer 都是界面的私有字段，读取方式集中在 LoadDialogAccess，
    ///    字段改名时那边会报一次错误并跳过，不会把装载界面拖垮。
    /// </summary>
    [HarmonyPatch(typeof(Dialog_LoadTransporters), "CalculateAndRecacheTransferables")]
    internal static class Patch_LoadDialogSections
    {
        [HarmonyPostfix]
        private static void Postfix(Dialog_LoadTransporters __instance)
        {
            TryAddCargoSections(__instance);
        }

        private static void TryAddCargoSections(Dialog_LoadTransporters dialog)
        {
            if (dialog == null)
            {
                return;
            }
            List<CompTransporter> transporters = LoadDialogAccess.TransportersOf(dialog);
            List<TransferableOneWay> transferables = LoadDialogAccess.TransferablesOf(dialog);
            TransferableOneWayWidget widget = LoadDialogAccess.WidgetOf(dialog);
            CompShuttle shuttle = LoadDialogAccess.QuestShuttleOf(dialog);
            if (transporters == null || transferables == null || widget == null || transporters.Count == 0 || shuttle == null)
            {
                return;
            }
            List<TransferableOneWay> mechs = null;
            List<TransferableOneWay> subhumans = null;
            for (int i = 0; i < transferables.Count; i++)
            {
                TransferableOneWay transferable = transferables[i];
                if (!(transferable.AnyThing is Pawn pawn))
                {
                    continue;
                }
                if (CargoPolicy.HasVanillaPawnSection(pawn))
                {
                    continue;
                }
                if (CargoPolicy.IsPlayerMech(pawn))
                {
                    mechs = mechs ?? new List<TransferableOneWay>();
                    mechs.Add(transferable);
                }
                else if (CargoPolicy.IsPlayerSubhuman(pawn))
                {
                    subhumans = subhumans ?? new List<TransferableOneWay>();
                    subhumans.Add(transferable);
                }
            }
            if (mechs != null)
            {
                widget.AddSection("ShuttleCargoMechsSection".Translate(), mechs);
            }
            if (subhumans != null)
            {
                widget.AddSection("ShuttleCargoSubhumansSection".Translate(), subhumans);
            }
        }
    }
}
