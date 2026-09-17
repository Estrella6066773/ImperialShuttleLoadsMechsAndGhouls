using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 手动起飞（一）：静音原版「任务要的人齐了就立刻起飞」这条路径。
    /// 挂钩 ShipJob_Wait.TickInterval。
    ///
    /// 联动关系：
    ///   TransportShip 每 tick -> curJob.TickInterval
    ///     -> 若 leaveImmediatelyWhenSatisfied 且 AllRequiredThingsLoaded 同时成立，立刻调用 SendAway，
    ///        穿梭机当场飞走；否则只按 60 tick 的节奏检查「某个必需单位没了就走」那套规则。
    ///
    /// 问题所在：AllRequiredThingsLoaded 只数任务要的殖民者（CompShuttle.ContainedColonistCount 只看自由殖民者），
    ///   完全不知道本模组放行的机械族与食尸鬼有没有进舱。于是「殖民者刚进齐、随行单位还在往机舱走」时，
    ///   穿梭机就会在众目睽睽之下起飞，把随行单位丢在地图上。
    ///
    /// 做法：这次调用期间把 leaveImmediatelyWhenSatisfied 临时置为 false，让原方法走它自己那条「不自动起飞」的分支，
    ///   调用结束后立刻还原。这样做的两个好处：
    ///   1. 原方法后半段的「必需单位消失/倒地/死亡就送走」这类规则原封不动地继续生效——那是任务自己的安排，不该被本模组拦掉；
    ///   2. 字段本身没有被改写，存档里的值保持原样，玩家在设置里关掉手动起飞就能回到原版行为。
    ///   之后由 Patch_ShipJob_Wait_GetJobGizmos 补上起飞按钮，起飞时机交给玩家。
    ///
    /// 注意：
    /// 1. 只影响任务穿梭机（许可穿梭机本来就带自己的发射按钮，玩家自有穿梭机不归本模组管）。
    /// 2. 只接管「按钮可见」的船务（ManualLaunch.AppliesTo 里的 showGizmos 一条）：
    ///    加冕典礼的穿梭机把按钮整条藏起来，离开时机由任务脚本安排，本模组不去碰它。
    /// 3. 只拦「装齐就飞」这一条；任务用倒计时（ShipJob_WaitTime）或信号送走穿梭机时不经过这里，照旧执行。
    /// </summary>
    [HarmonyPatch(typeof(ShipJob_Wait), nameof(ShipJob_Wait.TickInterval))]
    internal static class Patch_ShipJob_Wait_TickInterval
    {
        [HarmonyPrefix]
        private static void Prefix(ShipJob_Wait __instance, out bool __state)
        {
            __state = false;
            if (!__instance.leaveImmediatelyWhenSatisfied || !ManualLaunch.AppliesTo(__instance))
            {
                return;
            }
            __instance.leaveImmediatelyWhenSatisfied = false;
            __state = true;
        }

        [HarmonyPostfix]
        private static void Postfix(ShipJob_Wait __instance, bool __state)
        {
            if (__state)
            {
                __instance.leaveImmediatelyWhenSatisfied = true;
            }
        }
    }

    /// <summary>
    /// 手动起飞（二）：给任务穿梭机补一个「起飞」按钮。
    /// 挂钩 ShipJob_Wait.GetJobGizmos 与 ShipJob_WaitSendable.GetJobGizmos。
    ///
    /// 联动关系：
    ///   CompShuttle.CompGetGizmosExtra -> shipParent.curJob.GetJobGizmos
    ///     -> 原版只在 !permitShuttle 且 !leaveImmediatelyWhenSatisfied 时才给「起飞」按钮，
    ///        任务穿梭机正好相反（自动起飞），所以原版这条分支不会产生按钮。
    ///
    /// 为什么挂两处：ShipJob_WaitSendable 重写了 GetJobGizmos，直接返回一个空的按钮集合，
    ///   既不调用基类版本、也不看 leaveImmediatelyWhenSatisfied。王权任务用的正是这种船务，
    ///   只挂基类会让按钮在真正需要它的任务里一个都不出现。
    ///   两个补丁共用同一个 ManualLaunch.Augment，重复挂上也不会出现两颗按钮
    ///   （基类版本对 WaitSendable 根本不会被调用；反过来 WaitSendable 的补丁只会追加一颗）。
    ///
    /// 做法：在原版结果之后追加本模组自己的按钮，
    ///   按钮点击后调用当前船务自己的 SendAway，而不是原版那颗按钮所用的 ForceJob(FlyAway)。
    ///
    /// 为什么不能用原版那颗按钮的写法：ShipJob_WaitSendable 重写了 SendAway，负责算出返航目的地与到达动作；
    ///   而 ForceJob(ShipJobDefOf.FlyAway) 得到的是一条没有目的地的普通飞离指令
    ///   （ShipJob_FlyAway 的 destinationTile 无效时不生成世界对象），装在里面的单位会被一起带走却到不了任何地方。
    ///   直接调用当前船务的 SendAway，等于替玩家按下「任务自己会按的那个按钮」，去程与返程目的地都由原版逻辑决定。
    ///
    /// 注意：
    /// 1. 按钮的显示与否沿用原版那套开关：调用的 CompShuttle 先看 TransportShip.ShowGizmos（也就是本条船务的
    ///    showGizmos），判定里再确认一次，两边口径一致。
    /// 2. 按钮的禁用理由按原版口径分两种：殖民者不够（本模组新增的说明）与其它必需人员物品没到齐（原版原文）。
    /// 3. 按钮说明里会列出「已指派但还没进舱」的单位数量与名字，让玩家在按下之前知道会落下谁。
    /// 4. 按钮只在 AllRequiredThingsLoaded 时才可按下，因此按下后原版 SendLaunchedSignals 依然会发出
    ///    SentSatisfied 信号，任务对自己的倒计时照旧被解除，任务节奏不被改。
    /// </summary>
    [HarmonyPatch(typeof(ShipJob_Wait), nameof(ShipJob_Wait.GetJobGizmos))]
    internal static class Patch_ShipJob_Wait_GetJobGizmos
    {
        [HarmonyPostfix]
        private static void Postfix(ShipJob_Wait __instance, ref IEnumerable<Gizmo> __result)
        {
            ManualLaunch.Augment(__instance, ref __result);
        }
    }

    /// <summary>
    /// 手动起飞（三）：返程用的可派遣船务自己重写了 GetJobGizmos，必须单独挂一份。
    /// 判定与按钮构造全部复用 ManualLaunch，这里只负责把结果接上。
    /// </summary>
    [HarmonyPatch(typeof(ShipJob_WaitSendable), nameof(ShipJob_WaitSendable.GetJobGizmos))]
    internal static class Patch_ShipJob_WaitSendable_GetJobGizmos
    {
        [HarmonyPostfix]
        private static void Postfix(ShipJob_WaitSendable __instance, ref IEnumerable<Gizmo> __result)
        {
            ManualLaunch.Augment(__instance, ref __result);
        }
    }

    /// <summary>
    /// 手动起飞功能的共享实现：判定适用范围、拼按钮、算禁用理由。
    ///
    /// 联动关系：
    ///   Patch_ShipJob_Wait_TickInterval 问 AppliesTo，决定要不要静音自动起飞；
    ///   Patch_ShipJob_Wait_GetJobGizmos 问 AppliesTo 与 BuildSendCommand，决定要不要给按钮；
    ///   按钮点击 -> SendAway -> ShuttleDepartureHold.ManualSendInProgress 置位，让等待判定放行。
    ///
    /// 注意：
    /// 1. 设置对象取不到时一律当作「关闭」，退回原版行为，绝不因设置文件损坏而改变游戏玩法。
    /// 2. 只认任务穿梭机：许可穿梭机本来就有原版的发射组按钮，玩家自有穿梭机由资料片自己管。
    /// </summary>
    internal static class ManualLaunch
    {
        private static readonly List<Pawn> tmpBoarding = new List<Pawn>();
        private static MethodInfo sendAwayMethod;
        private static bool sendAwayLookupFailed;

        /// <summary>设置里是否开启了「任务穿梭机改为手动起飞」。</summary>
        public static bool Enabled => ImperialShuttleLoadsMod.Settings?.manualLaunchQuestShuttles ?? false;

        /// <summary>
        /// 这台船务是否归本模组的手动起飞接管。
        ///
        /// 四个条件缺一不可：
        ///   1. 设置里开启了手动起飞；
        ///   2. 本条船务本来就是「装齐就走」的那种（leaveImmediatelyWhenSatisfied）；
        ///   3. 船务的按钮是显示的（showGizmos）——这一点是硬条件：王权加冕典礼的穿梭机
        ///      用 showGizmos: false 把按钮整条藏掉，它离开的时机完全由任务脚本安排，
        ///      静音它的自动起飞只会让皇家一行人困在一条永远不走的船上，因此必须放过；
        ///   4. 是任务穿梭机（许可穿梭机本来就有原版的发射组按钮，玩家自有穿梭机由资料片自己管）。
        /// </summary>
        public static bool AppliesTo(ShipJob_Wait job)
        {
            if (job == null || !Enabled)
            {
                return false;
            }
            if (!job.leaveImmediatelyWhenSatisfied || !job.showGizmos)
            {
                return false;
            }
            return CargoPolicy.IsQuestShuttle(job.transportShip?.ShuttleComp);
        }

        /// <summary>
        /// 在两个 GetJobGizmos 补丁里共用：把「起飞」按钮追加到原版结果之后。
        ///
        /// 注意：追加而不是替换。WaitSendable 那版原样返回空集合，基类那版在 permitShuttle 分支里
        /// 还会给出「驱逐」按钮，替换会把那些按钮弄丢。
        /// </summary>
        public static void Augment(ShipJob_Wait job, ref IEnumerable<Gizmo> gizmos)
        {
            if (!AppliesTo(job))
            {
                return;
            }
            Command_Action command = BuildSendCommand(job);
            if (command == null)
            {
                return;
            }
            gizmos = Append(gizmos, command);
        }

        private static IEnumerable<Gizmo> Append(IEnumerable<Gizmo> inner, Gizmo extra)
        {
            if (inner != null)
            {
                foreach (Gizmo gizmo in inner)
                {
                    yield return gizmo;
                }
            }
            yield return extra;
        }

        public static Command_Action BuildSendCommand(ShipJob_Wait job)
        {
            CompShuttle shuttle = job.transportShip?.ShuttleComp;
            if (shuttle == null)
            {
                return null;
            }
            Command_Action command = new Command_Action
            {
                defaultLabel = "CommandSendShuttle".Translate(),
                defaultDesc = "CommandSendShuttleDesc".Translate(),
                icon = CompLaunchable.LaunchCommandTex,
                alsoClickIfOtherInGroupClicked = false,
                action = delegate
                {
                    SendAway(job);
                }
            };
            string reason = RefuseReason(shuttle);
            if (!reason.NullOrEmpty())
            {
                command.Disable(reason);
            }
            string note = BoardingNote(shuttle);
            if (!note.NullOrEmpty())
            {
                command.defaultDescPostfix = note;
            }
            return command;
        }

        /// <summary>
        /// 现在还不能起飞的理由；可以起飞时返回 null。
        ///
        /// 先看殖民者：这是任务对「谁必须上机」的硬性要求，缺了就不是「少点什么」而是没人能开船，
        /// 因此单独给一条说明，让玩家一眼看出还差几个人。
        /// 再看原版的 AllRequiredThingsLoaded：任务指定的具体人员与物品没到齐时沿用原版提示。
        /// </summary>
        private static string RefuseReason(CompShuttle shuttle)
        {
            int required = CargoPolicy.RequiredColonistCount(shuttle);
            if (required > 0)
            {
                int aboard = CargoPolicy.ColonistsAboard(shuttle);
                if (aboard < required)
                {
                    return "ShuttleCargoNoRequiredColonists".Translate(required, aboard);
                }
            }
            if (!shuttle.AllRequiredThingsLoaded)
            {
                return "CommandSendShuttleFailMissingRequiredThing".Translate();
            }
            return null;
        }

        private static string BoardingNote(CompShuttle shuttle)
        {
            ShuttleDepartureHold.CollectAssignedNotYetAboard(shuttle, tmpBoarding);
            if (tmpBoarding.Count == 0)
            {
                return null;
            }
            string names = tmpBoarding.Select(pawn => pawn.LabelShort).ToCommaList(useAnd: true);
            return "\n\n" + "ShuttleCargoSendShuttleBoarding".Translate(tmpBoarding.Count, names);
        }

        /// <summary>
        /// 调用当前船务自己的 SendAway。
        ///
        /// 用反射而不是 Harmony 生成的委托：SendAway 是虚方法，ShipJob_WaitSendable 重写了它，
        /// 而 MethodInfo.Invoke 会按运行时类型分派到重写版本；ldftn 式的直接调用会绕过重写，
        /// 让穿梭机丢掉返航目的地，所以这里必须走 Invoke。
        ///
        /// 注意：SendAway 内部会立刻结束本条船务并排队下一条，调用前把 ManualSendInProgress 置位，
        /// 是为了让 SendAway 内部可能触发的等待判定（本模组自己的 ShuttleDepartureHold）直接放行——
        /// 玩家按下按钮就必须立刻起飞，不能被本模组的等待逻辑吞掉。
        /// </summary>
        private static void SendAway(ShipJob_Wait job)
        {
            if (sendAwayLookupFailed)
            {
                return;
            }
            if (sendAwayMethod == null)
            {
                sendAwayMethod = AccessTools.Method(typeof(ShipJob_Wait), "SendAway");
                if (sendAwayMethod == null)
                {
                    sendAwayLookupFailed = true;
                    Log.Error("[帝国穿梭机可装载机械族与食尸鬼] 找不到 ShipJob_Wait.SendAway，任务穿梭机的「起飞」按钮不可用。游戏更新后方法改名时会出现此提示。");
                    return;
                }
            }
            ShuttleDepartureHold.ManualSendInProgress = true;
            try
            {
                sendAwayMethod.Invoke(job, null);
            }
            catch (Exception ex)
            {
                Log.Error("[帝国穿梭机可装载机械族与食尸鬼] 执行起飞时出错：" + ex);
            }
            finally
            {
                ShuttleDepartureHold.ManualSendInProgress = false;
            }
        }
    }
}
