using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 读取装载界面（Dialog_LoadTransporters）的私有字段，供本模组几个装载界面补丁共用。
    ///
    /// 联动关系（谁在什么时候问这里）：
    ///   Patch_LoadDialogSections.Postfix -> TryAddCargoSections：补机械族与亚人的分区时需要
    ///     transferables（候选清单）与 pawnsTransfer（界面组件）；
    ///   Patch_LoadDialogMassUsage.Postfix -> LoadDialogMass.ExtraPawnMass：补算被漏掉的载重时需要
    ///     transporters（判断是不是任务穿梭机）与 transferables（看玩家勾了谁）。
    ///
    /// 为什么要反射：三个字段都是私有的，原版没有公开入口。RimWorld 1.6 的字段名依次是
    ///   transporters（正在装载的搬运器列表）、transferables（候选清单）、pawnsTransfer（人员页界面组件）。
    ///
    /// 注意：
    /// 1. 字段一旦改名，这里只报一次中文错误并返回 null；后果是「分区不显示、载重按原版算」，
    ///    放行补丁与起飞按钮不受影响（它们走的是公开的 CompShuttle 接口）。
    /// 2. 返回的都是界面正在使用的同一个列表，调用方只读；本类不持有、也不改动它们。
    /// 3. QuestShuttleOf 只在「界面里是一台玩家远征用的任务穿梭机」时返回实例；打包成多台的运输舱、许可穿梭机、
    ///    玩家自己的穿梭机、以及款待任务那类接送暂住客人的穿梭机一律返回 null，于是两个补丁的适用范围天然一致。
    /// </summary>
    internal static class LoadDialogAccess
    {
        private static FieldInfo widgetField;
        private static FieldInfo transferablesField;
        private static FieldInfo transportersField;
        private static bool missingFieldReported;

        public static List<CompTransporter> TransportersOf(Dialog_LoadTransporters dialog)
        {
            EnsureFields();
            if (dialog == null || transportersField == null)
            {
                return null;
            }
            return transportersField.GetValue(dialog) as List<CompTransporter>;
        }

        public static List<TransferableOneWay> TransferablesOf(Dialog_LoadTransporters dialog)
        {
            EnsureFields();
            if (dialog == null || transferablesField == null)
            {
                return null;
            }
            return transferablesField.GetValue(dialog) as List<TransferableOneWay>;
        }

        public static TransferableOneWayWidget WidgetOf(Dialog_LoadTransporters dialog)
        {
            EnsureFields();
            if (dialog == null || widgetField == null)
            {
                return null;
            }
            return widgetField.GetValue(dialog) as TransferableOneWayWidget;
        }

        /// <summary>
        /// 界面正在装载的那台「玩家远征用」任务穿梭机；不是这种穿梭机（或界面里根本没有穿梭机）时返回 null。
        ///
        /// 判定沿用 CargoPolicy.IsPlayerExpeditionShuttle，与其它补丁完全一致：
        ///   许可穿梭机、玩家自有穿梭机，以及款待任务那类接送暂住客人的穿梭机一律返回 null，
        ///   于是本模组在装载界面里的补丁（补分区、补载重）都不会碰到它们。
        /// </summary>
        public static CompShuttle QuestShuttleOf(Dialog_LoadTransporters dialog)
        {
            List<CompTransporter> transporters = TransportersOf(dialog);
            if (transporters == null || transporters.Count == 0 || transporters[0] == null || transporters[0].parent == null)
            {
                return null;
            }
            CompShuttle shuttle = transporters[0].parent.TryGetComp<CompShuttle>();
            return CargoPolicy.IsPlayerExpeditionShuttle(shuttle) ? shuttle : null;
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
                Log.Error("[更好的“帝国穿梭机”] 读取装载界面私有字段时出错：" + ex);
            }
            if ((widgetField == null || transferablesField == null || transportersField == null) && !missingFieldReported)
            {
                missingFieldReported = true;
                Log.Error("[更好的“帝国穿梭机”] 装载界面 Dialog_LoadTransporters 上找不到 pawnsTransfer / transferables / transporters 字段，界面里不会显示机械族与亚人的分区，穿梭机的载重也按原版口径计算。游戏更新后字段改名时会出现此提示。");
            }
        }
    }
}
