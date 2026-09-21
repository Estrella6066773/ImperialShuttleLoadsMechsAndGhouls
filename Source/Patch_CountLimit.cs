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
    /// 总开关（CargoPolicy.LimitEnforced）关闭时整段不执行：界面按原版口径计数，本模组不干预。
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
    /// 4. 只对「玩家殖民者远征」的任务穿梭机生效（CargoPolicy.IsPlayerExpeditionShuttle）；
    ///    款待任务那类接送暂住客人的穿梭机连界面都不归本模组管。
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
            if (!CargoPolicy.LimitEnforced)
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
    /// 联动关系：
    ///   右键某个单位选「进入穿梭机」 -> CompShuttle.CompFloatMenuOptions / CompMultiSelectFloatMenuOptions
    ///     -> IsAllowedNow：先问 IsAllowed（能不能上机），再看 maxColonistCount（最多几个人）
    ///        -> 数出「机舱里已有的殖民者」与「全图上正排队进这台穿梭机的单位」，达到上限就返回 false，
    ///           菜单项显示成灰色的「不允许」。
    ///   抱着单位走到穿梭机旁（FloatMenuOptionProvider）也走同一个判定。
    ///
    /// 原版为什么形同无物：多数任务只给穿梭机设 requiredColonistCount（点名要带几名殖民者），
    ///   而 maxColonistCount 留空为 -1，IsAllowedNow 开头就因此直接返回 true——
    ///   于是装载界面会拦住「只允许 N 名殖民者」，右键却可以一个接一个往机舱里塞，
    ///   人数上限在右键这条路上完全失效。
    ///
    /// 做法：原版的结果只用 maxColonistCount 作比较；本补丁改成统一按 CargoPolicy.BoardingLimit 比较
    ///   （任务名额与「任务要求的人数」里较大的那个），并且：
    ///     - 不占名额的类别（默认设置下的机械族与亚人）直接放行——它们进舱不改变既有人数，
    ///       任务要的殖民者一装满就把随行单位挡在机舱外，正是本模组要避免的；
    ///     - 占名额的单位按「机舱里已有的 + 全图上正走过来的」一起算，与原版的口径一致，只是跳过两类：
    ///       不计入的类别，以及不属于玩家这一边的单位（任务自带的帝国乘客，见 CargoPolicy.BelongsToPlayerSide）；
    ///     - 要上机的单位本身如果就是任务自带的乘客，名额一概不管，原版判什么就是什么；
    ///     - 物品不重算，原版怎么判就怎么判。
    ///
    /// 注意：
    /// 1. 只在 IsAllowed 说「能上机」时才改写结果：健康、阵营、类别这些拒绝理由一律原样保留。
    /// 2. 任务没给这台穿梭机设名额、也没有点名要带人数（上限为 0）时完全不介入，退回原版；
    ///    设置里关掉「任务穿梭机的人数限制」时同样完全不介入。
    /// 3. 只对「玩家殖民者远征」的任务穿梭机生效（CargoPolicy.IsPlayerExpeditionShuttle）；
    ///    许可穿梭机与玩家自有穿梭机的 maxColonistCount 为 -1 本来就会提前返回，
    ///    款待任务那类接送暂住客人的穿梭机则整类不在范围内。
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
                // 只对「单位」重算名额；其它东西（物品、尸体等）保持原版判定，免得名额把它们一起挡在门外。
                return;
            }
            if (!CargoPolicy.CountsTowardShuttleLimit(boarding))
            {
                // 要上机的单位本身不占名额（默认设置下的机械族与亚人）：它能进舱并不改变占名额的人数，
                // 因此不论机舱里已计入上限的人有多少，上限都不该拦它。
                __result = true;
                return;
            }
            if (!CargoPolicy.BelongsToPlayerSide(boarding))
            {
                // 任务自带的乘客（帝国使团、押送人员）不算在玩家这边，名额一概不管他们，原版怎么判就怎么判。
                return;
            }
            __result = CargoPolicy.QuotaAboard(__instance, t) + CargoPolicy.CountEnteringQuotaPawns(__instance.parent, t) < slots;
        }
    }
}
