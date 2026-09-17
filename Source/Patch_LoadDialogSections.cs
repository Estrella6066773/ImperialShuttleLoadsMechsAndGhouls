using System;
using System.Collections.Generic;
using System.Reflection;
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
    /// 3. pawnsTransfer / transferables / transporters 都是界面的私有字段，原版没有公开入口，只能反射取用；
    ///    字段一旦改名，这里会只报一次错误并跳过，不会把装载界面拖垮。
    /// </summary>
    [HarmonyPatch(typeof(Dialog_LoadTransporters), "CalculateAndRecacheTransferables")]
    internal static class Patch_LoadDialogSections
    {
        [HarmonyPostfix]
        private static void Postfix(Dialog_LoadTransporters __instance)
        {
            LoadDialogSections.TryAddCargoSections(__instance);
        }
    }

    /// <summary>
    /// 装载界面私有字段的读取与分区补写，供 Patch_LoadDialogSections 调用。
    ///
    /// 联动关系：本类不改变任何原版状态，只往界面组件里追加两个分区；
    /// 分区的行内容由原版 TransferableOneWayWidget.DoRow 绘制，勾选结果照常写回 TransferableOneWay，
    /// 因此「勾了几个」「质量够不够」这些计算全部沿用原版逻辑。
    ///
    /// 注意：
    /// 1. 三个字段名与 1.6 原版一致；改版后若失效，日志里会出现一次中文报错，功能退化为「界面不显示这两类单位」，
    ///    此时放行补丁仍然有效（右键让单位进入穿梭机依旧可行）。
    /// 2. 读取到的 transferables 是界面当前的候选清单，本类只读不改，不持有其引用。
    /// </summary>
    internal static class LoadDialogSections
    {
        private static FieldInfo widgetField;
        private static FieldInfo transferablesField;
        private static FieldInfo transportersField;
        private static bool missingFieldReported;

        public static void TryAddCargoSections(Dialog_LoadTransporters dialog)
        {
            if (dialog == null)
            {
                return;
            }
            EnsureFields();
            if (widgetField == null || transferablesField == null || transportersField == null)
            {
                if (!missingFieldReported)
                {
                    missingFieldReported = true;
                    Log.Error("[帝国穿梭机可装载机械族与食尸鬼] 装载界面 Dialog_LoadTransporters 上找不到 pawnsTransfer / transferables / transporters 字段，界面里不会显示机械族与亚人的分区。游戏更新后字段改名时会出现此提示。");
                }
                return;
            }
            List<CompTransporter> transporters = transportersField.GetValue(dialog) as List<CompTransporter>;
            List<TransferableOneWay> transferables = transferablesField.GetValue(dialog) as List<TransferableOneWay>;
            TransferableOneWayWidget widget = widgetField.GetValue(dialog) as TransferableOneWayWidget;
            if (transporters == null || transferables == null || widget == null || transporters.Count == 0)
            {
                return;
            }
            CompShuttle shuttle = transporters[0].parent.TryGetComp<CompShuttle>();
            if (!CargoPolicy.IsQuestShuttle(shuttle))
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

        private static void EnsureFields()
        {
            if (widgetField != null && transferablesField != null && transportersField != null)
            {
                return;
            }
            Type type = typeof(Dialog_LoadTransporters);
            try
            {
                widgetField = AccessTools.Field(type, "pawnsTransfer");
                transferablesField = AccessTools.Field(type, "transferables");
                transportersField = AccessTools.Field(type, "transporters");
            }
            catch (Exception ex)
            {
                Log.Error("[帝国穿梭机可装载机械族与食尸鬼] 读取装载界面私有字段时出错：" + ex);
            }
        }
    }
}
