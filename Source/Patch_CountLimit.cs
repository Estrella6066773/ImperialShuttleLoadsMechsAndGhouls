using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 人数上限按设置统计（一）：装载界面点确定时的那次检查。
    /// 挂钩 Dialog_LoadTransporters.CheckForErrors。
    ///
    /// 联动关系：
    ///   玩家点确定 -> TryAccept -> CheckForErrors(pawnsFromTransferables)
    ///     -> 若穿梭机是单台成组（max1PerGroup）且任务设了 requiredColonistCount，
    ///        就用「界面上勾选的所有单位」与这个数字比较，超出就报「只允许 N 名殖民者」并拒绝装载。
    ///   名单来自 TransferableUtility.GetPawnsFromTransferables，按界面上的勾选结果逐个列出，
    ///   因此机械族、亚人、奴隶、囚犯原本都会一起被数进去。
    ///
    /// 做法：只在「原版这次一定会因为人数超上限而拒绝」时，把名单收窄成「设置里算作占名额的那些单位」，
    ///   再交给原方法。这样原方法自己的比较就只统计该统计的人。
    ///
    /// 为什么加「原版一定会拒绝」这个前提：
    ///   收窄名单同时也让原方法后面的可达性检查少看几眼（它遍历的正是这份名单）。
    ///   若人数没超上限就不收窄，可达性检查仍覆盖全部勾选单位，行为与原版完全一致；
    ///   只有确实被上限拦住时，才轮到本模组接管，此时“少看几眼”的那几个单位正是被放行随行的单位。
    ///
    /// 注意：
    /// 1. 载重上限、任务要求的人员与物品、物品可达性检查读的是 transferables 与 transporters，不受收窄影响，
    ///    也就是说超载依旧会被拦下。
    /// 2. 参数被换成新列表后，调用方 TryAccept 会把这份名单继续转交给 MakeLordsAsAppropriate；
    ///    装载领主那处补丁刻意不读这个参数，只读 leftToLoad，因此随行单位不会因为这里被收窄而漏掉。
    /// 3. transporters 是界面的私有字段，原版没有公开入口，只能反射取用；字段一旦改名就整段跳过，退化为原版行为。
    /// </summary>
    [HarmonyPatch(typeof(Dialog_LoadTransporters), "CheckForErrors")]
    internal static class Patch_CheckForErrors
    {
        private static FieldInfo transportersField;
        private static bool fieldLookupDone;

        [HarmonyPrefix]
        private static void Prefix(Dialog_LoadTransporters __instance, ref List<Pawn> pawns)
        {
            if (pawns == null || pawns.Count == 0)
            {
                return;
            }
            CompShuttle shuttle = ShuttleOf(__instance);
            if (shuttle == null || shuttle.requiredColonistCount <= 0)
            {
                return;
            }
            if (pawns.Count <= shuttle.requiredColonistCount)
            {
                return;
            }
            if (!CargoPolicy.AnyCategoryLeftOutOfLimit())
            {
                return;
            }
            int kept = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                if (CargoPolicy.CountsTowardShuttleLimit(pawns[i]))
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
                if (CargoPolicy.CountsTowardShuttleLimit(pawns[i]))
                {
                    narrowed.Add(pawns[i]);
                }
            }
            pawns = narrowed;
        }

        /// <summary>取本次装载的穿梭机；不是单台成组的任务穿梭机时返回 null。</summary>
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
            return CargoPolicy.IsQuestShuttle(shuttle) ? shuttle : null;
        }
    }

    /// <summary>
    /// 人数上限按设置统计（二）：右键让单位进舱时的那次检查。
    /// 挂钩 CompShuttle.IsAllowedNow。
    ///
    /// 联动关系：
    ///   右键某个单位选「进入穿梭机」 -> CompShuttle.CompFloatMenuOptions / CompMultiSelectFloatMenuOptions
    ///     -> IsAllowedNow：先问 IsAllowed（能不能上机），再看 maxColonistCount（最多几个人）
    ///        -> 数出「机舱里已有的殖民者」与「全图上正排队进这台穿梭机的单位」，达到上限就返回 false，
    ///           菜单项显示成灰色的「不允许」。
    ///
    /// 与原版的差别只在原版要拒绝的时候：原版数人数时，机舱里只认殖民者，但排队进舱的单位不看类别，
    ///   于是随行的机械族、亚人、奴隶、囚犯只要在排队，就都会挤占这个上限。
    ///   本补丁在原版已经因为上限返回 false 时，按设置重数一遍：设置里「不计入」的类别整类跳过。
    ///
    /// 注意：
    /// 1. 只在原版返回 false、且 IsAllowed 说「能上机」时才改写结果——也就是说拒绝的原因确实是人数。
    ///    其它任何拒绝理由都原样保留。
    /// 2. 设置里四类全都「计入」时不介入，此时重数的结果与原版一致，没有副作用。
    /// 3. 只对任务穿梭机生效；许可穿梭机与玩家自有穿梭机的 maxColonistCount 为 -1，本来就会提前返回。
    /// </summary>
    [HarmonyPatch(typeof(CompShuttle), nameof(CompShuttle.IsAllowedNow))]
    internal static class Patch_CompShuttle_IsAllowedNow
    {
        [HarmonyPostfix]
        private static void Postfix(CompShuttle __instance, Thing t, ref bool __result)
        {
            if (__result)
            {
                return;
            }
            if (__instance?.parent == null || __instance.maxColonistCount <= 0)
            {
                return;
            }
            if (!CargoPolicy.IsQuestShuttle(__instance) || !CargoPolicy.AnyCategoryLeftOutOfLimit())
            {
                return;
            }
            if (!__instance.IsAllowed(t))
            {
                return;
            }
            __result = CountTowardLimit(__instance, t) < __instance.maxColonistCount;
        }

        /// <summary>
        /// 照原版 IsAllowedNow 的中段重数一遍人数，唯一差别是跳过设置里「不计入」的类别。
        ///
        /// 原版口径：机舱里只数殖民者（物品另外算），全图上任何排队或正在执行「进入穿梭机」的单位都数，
        /// 且同一单位有多条排队就算多条——这里逐条对齐，避免与其它 mod 的预期打架。
        /// </summary>
        private static int CountTowardLimit(CompShuttle shuttle, Thing excluded)
        {
            int num = 0;
            ThingOwner container = shuttle.Transporter?.innerContainer;
            if (container != null)
            {
                for (int i = 0; i < container.Count; i++)
                {
                    if (container[i] == excluded)
                    {
                        continue;
                    }
                    if (container[i] is Pawn contained && contained.IsColonist && CargoPolicy.CountsTowardShuttleLimit(contained))
                    {
                        num++;
                    }
                }
            }
            Map map = shuttle.parent.Map;
            if (map == null)
            {
                return num;
            }
            List<Pawn> allPawns = map.mapPawns.AllPawns;
            for (int i = 0; i < allPawns.Count; i++)
            {
                Pawn pawn = allPawns[i];
                if (pawn == excluded || pawn.jobs == null || pawn.jobs.curDriver == null)
                {
                    continue;
                }
                if (!CargoPolicy.CountsTowardShuttleLimit(pawn))
                {
                    continue;
                }
                JobQueue queue = pawn.jobs.jobQueue;
                for (int j = 0; j < queue.Count; j++)
                {
                    if (IsEnteringShuttle(queue[j].job, shuttle.parent))
                    {
                        num++;
                    }
                }
                if (IsEnteringShuttle(pawn.jobs.curJob, shuttle.parent))
                {
                    num++;
                }
            }
            return num;
        }

        private static bool IsEnteringShuttle(Job job, Thing shuttle)
        {
            return job != null
                && typeof(JobDriver_EnterTransporter).IsAssignableFrom(job.def.driverClass)
                && job.GetTarget(TargetIndex.A).Thing == shuttle;
        }
    }
}
