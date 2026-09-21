using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 人数上限按设置统计（一）：装载界面点确定时的那次检查。
    /// 挂钩 Dialog_LoadTransporters.CheckForErrors。
    ///
    /// 联动关系：
    ///   玩家点确定 -> TryAccept -> CheckForErrors(pawnsFromTransferables)
    ///     -> 若穿梭机是单台成组且任务设了 requiredColonistCount，
    ///        就用「界面上勾选的所有单位」与这个数字比较，超出就拒绝装载。
    ///
    /// 做法：
    ///   1. 人数限制关闭时：临时把 requiredColonistCount 抬到极大，让原版那条检查直接放过；
    ///   2. 人数限制开启且名单里有不占名额的人（设置排除，或返程囚犯）：把名单收窄成占名额子集再交给原方法。
    ///
    /// 注意：载重检查走 MassCapacity，见 Patch_LoadDialogMassCapacity；任务必需人与物不受影响。
    /// </summary>
    [HarmonyPatch(typeof(Dialog_LoadTransporters), "CheckForErrors")]
    internal static class Patch_CheckForErrors
    {
        private static FieldInfo transportersField;
        private static bool fieldLookupDone;

        [HarmonyPrefix]
        private static void Prefix(Dialog_LoadTransporters __instance, ref List<Pawn> pawns, out int __state)
        {
            __state = -1;
            if (pawns == null || pawns.Count == 0)
            {
                return;
            }
            CompShuttle shuttle = ShuttleOf(__instance);
            if (shuttle == null || shuttle.requiredColonistCount <= 0)
            {
                return;
            }
            if (!CargoPolicy.LimitEnforced)
            {
                // 解除人数限制：连原版「只允许 N 名」一并放过；调用结束后还原字段。
                __state = shuttle.requiredColonistCount;
                shuttle.requiredColonistCount = int.MaxValue;
                return;
            }
            if (pawns.Count <= shuttle.requiredColonistCount)
            {
                return;
            }
            if (!CargoPolicy.AnyPawnLeftOutOfLimit(pawns, shuttle) && !CargoPolicy.AnyCategoryLeftOutOfLimit())
            {
                return;
            }
            int kept = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                if (CargoPolicy.CountsTowardShuttleLimit(pawns[i], shuttle))
                {
                    kept++;
                }
            }
            if (kept == pawns.Count)
            {
                return;
            }
            List<Pawn> narrowed = new List<Pawn>(kept);
            for (int i = 0; i < pawns.Count; i++)
            {
                if (CargoPolicy.CountsTowardShuttleLimit(pawns[i], shuttle))
                {
                    narrowed.Add(pawns[i]);
                }
            }
            pawns = narrowed;
        }

        [HarmonyPostfix]
        private static void Postfix(Dialog_LoadTransporters __instance, int __state)
        {
            if (__state < 0)
            {
                return;
            }
            CompShuttle shuttle = ShuttleOf(__instance);
            if (shuttle != null)
            {
                shuttle.requiredColonistCount = __state;
            }
        }

        /// <summary>取本次装载的穿梭机；不是单台成组的玩家远征用任务穿梭机时返回 null。</summary>
        private static CompShuttle ShuttleOf(Dialog_LoadTransporters dialog)
        {
            if (!fieldLookupDone)
            {
                fieldLookupDone = true;
                transportersField = AccessTools.Field(typeof(Dialog_LoadTransporters), "transporters");
            }
            List<CompTransporter> transporters = transportersField?.GetValue(dialog) as List<CompTransporter>;
            if (transporters == null || transporters.Count == 0)
            {
                return null;
            }
            CompTransporter transporter = transporters[0];
            if (transporter?.parent == null || !transporter.Props.max1PerGroup)
            {
                return null;
            }
            CompShuttle shuttle = transporter.parent.TryGetComp<CompShuttle>();
            return CargoPolicy.IsPlayerExpeditionShuttle(shuttle) ? shuttle : null;
        }
    }

    /// <summary>
    /// 人数上限按设置统计（二）：右键让单位进舱时的那次检查。
    /// 挂钩 CompShuttle.IsAllowedNow。
    ///
    /// 返程囚犯经 CountsTowardShuttleLimit(pawn, shuttle) 直接放行，不占名额。
    /// </summary>
    [HarmonyPatch(typeof(CompShuttle), nameof(CompShuttle.IsAllowedNow))]
    internal static class Patch_CompShuttle_IsAllowedNow
    {
        [HarmonyPostfix]
        private static void Postfix(CompShuttle __instance, Thing t, ref bool __result)
        {
            if (__instance?.parent == null || !CargoPolicy.IsPlayerExpeditionShuttle(__instance))
            {
                return;
            }
            int slots = CargoPolicy.BoardingLimit(__instance);
            if (slots <= 0)
            {
                return;
            }
            if (!__instance.IsAllowed(t))
            {
                return;
            }
            if (!(t is Pawn boarding))
            {
                return;
            }
            if (!CargoPolicy.CountsTowardShuttleLimit(boarding, __instance))
            {
                __result = true;
                return;
            }
            if (!CargoPolicy.BelongsToPlayerSide(boarding))
            {
                return;
            }
            __result = CargoPolicy.QuotaAboard(__instance, t) + CargoPolicy.CountEnteringQuotaPawns(__instance.parent, t) < slots;
        }
    }
}
