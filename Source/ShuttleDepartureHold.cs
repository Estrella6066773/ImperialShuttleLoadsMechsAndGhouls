using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 「起飞前等本单位进舱」的统一判定。
    ///
    /// 联动关系（谁在什么时候问这里）：
    ///   ShipJob_Wait.SendAway          -> 穿梭机决定起飞（任务所需人员已就位时立即起飞的那条路径）
    ///   ShipJob_WaitSendable.SendAway  -> 同上，返程用的可派遣穿梭机走的是自己的重写版本
    ///   ShipJob_WaitTime.ShouldEnd     -> 任务给穿梭机设的等待时间到点时，同样先问这里
    ///   ManualLaunch（玩家点起飞）      -> 点击时把 ManualSendInProgress 置位，这里必须立刻放行
    ///
    /// 判定成立必须同时满足四点：
    ///   1. 是任务穿梭机（许可穿梭机与玩家自有穿梭机不归本模组管）；
    ///   2. 装载流程已经启动，也就是玩家点过装载界面的确定；
    ///   3. 任务要的东西都已经在机舱里（AllRequiredThingsLoaded）——这一条用来回避「紧急起飞」：
    ///      任务用 sendAwayIfAnyDespawnedDownedOrDead 之类的规则让穿梭机提前离场时，此条必然不成立，
    ///      于是本模组不会去拦那些本来该走的场次；
    ///   4. 至少还有一个本模组放行的单位（机械族或亚人）在地图上、走得动、够得着穿梭机、且还没进舱。
    ///
    /// 与「手动起飞」的关系：
    ///   设置里开启手动起飞后，原版「装齐就自动飞」那条路径已经被 Patch_ManualLaunch 静音，
    ///   因此这里的判定平时不会触发；它保留下来是为了两种场合：
    ///   (a) 玩家关掉手动起飞、回到原版自动起飞时，本模组仍然在随行单位没进舱前短暂多等一会儿；
    ///   (b) 任务用自己的倒计时或「某个必需单位死了就走」的规则送走穿梭机时，若任务要的东西都已就位，
    ///       本模组同样会多等一会儿。
    ///   玩家主动点起飞时，ManualSendInProgress 会让判定直接返回 false——按钮按下去必须立刻生效，
    ///   不能在背后被这里吞掉。
    ///
    /// 时间上限：从第一次需要等待开始最多多等 MaxHoldTicks，之后无论装载是否完成都放行。
    /// 这是安全阀——被征召、被派去做别的事、或者路被堵住的单位会一直满足不了「进舱」，
    /// 没有上限就会把穿梭机永久扣在地图上。玩家也可以点穿梭机的「取消装载」立刻解除等待。
    ///
    /// 注意：
    /// 1. 没有缓存 Pawn：每次重算时只读当下状态，单位进舱后原版会把它移出 leftToLoad，等待立刻结束。
    /// 2. 判定结果按穿梭机缓存 30 tick：SendAway 在「任务所需人员已就位」那条路径上每 tick 都会被调用，
    ///    而判定里含 CanReach 寻路，必须节流。
    /// 3. 开始等待时会给玩家一条提示（只在每次等待刚开始时发一次），让「穿梭机为什么停着不走」有据可查。
    /// 4. 超过 MaxTrackedShuttles 台时整表清空，属于异常情况下的兜底，避免静态字典随存档无限增长。
    /// </summary>
    internal static class ShuttleDepartureHold
    {
        private const int MaxHoldTicks = 15000;
        private const int EvaluationIntervalTicks = 30;
        private const int MaxTrackedShuttles = 64;

        /// <summary>
        /// 玩家刚刚点了「起飞」。这一瞬间正在执行的 SendAway 是玩家主动发起的，
        /// 任何等待判定都必须让路，否则按钮看起来像坏了。
        /// 只在点击的那一次调用内为真（主线程同步设置与复位）。
        /// </summary>
        public static bool ManualSendInProgress;

        private sealed class HoldState
        {
            public int EvaluatedTick = -1;
            public int StartTick = -1;
            public bool Holding;
            public bool StillBoarding;
        }

        private static readonly Dictionary<int, HoldState> states = new Dictionary<int, HoldState>();
        private static readonly List<CompTransporter> tmpTransporters = new List<CompTransporter>();
        private static readonly List<Pawn> tmpCargo = new List<Pawn>();

        /// <summary>按巡航舰（穿梭机）判断此刻要不要拦下起飞。</summary>
        public static bool ShouldHold(TransportShip ship)
        {
            if (ManualSendInProgress)
            {
                return false;
            }
            CompShuttle shuttle = ship?.ShuttleComp;
            TickManager ticks = Find.TickManager;
            if (shuttle == null || shuttle.parent == null || ticks == null)
            {
                return false;
            }
            int key = shuttle.parent.thingIDNumber;
            int now = ticks.TicksGame;
            if (!states.TryGetValue(key, out HoldState state))
            {
                if (states.Count >= MaxTrackedShuttles)
                {
                    states.Clear();
                }
                state = new HoldState();
                states.Add(key, state);
            }
            if (state.EvaluatedTick >= 0 && now - state.EvaluatedTick < EvaluationIntervalTicks)
            {
                return state.Holding;
            }
            state.EvaluatedTick = now;
            state.StillBoarding = HasCargoStillBoarding(shuttle);
            if (!state.StillBoarding)
            {
                state.StartTick = -1;
                state.Holding = false;
                return false;
            }
            if (state.StartTick < 0)
            {
                state.StartTick = now;
                NotifyHoldStarted(shuttle);
            }
            state.Holding = now - state.StartTick < MaxHoldTicks;
            return state.Holding;
        }

        private static bool HasCargoStillBoarding(CompShuttle shuttle)
        {
            if (!shuttle.AllRequiredThingsLoaded)
            {
                return false;
            }
            CollectStillBoarding(shuttle, tmpCargo);
            return tmpCargo.Count > 0;
        }

        /// <summary>
        /// 把「已被指派上机、但还没进舱」的本模组单位收集到 outPawns，含「此刻走得过来」这一条。
        /// 供等待起飞判定使用（调用频率被节流，可以承担寻路开销）。
        ///
        /// 数据来源是 CompTransporter.leftToLoad（玩家在装载界面点确定后由原版写入的待装载清单）：
        /// 进舱的单位会被原版从这份清单里移除，因此读到的天然就是「还没上机」的那批，
        /// 不依赖任何界面参数，也不会被别人改动过的临时名单影响。
        /// </summary>
        public static void CollectStillBoarding(CompShuttle shuttle, List<Pawn> outPawns)
        {
            Collect(shuttle, outPawns, requiringReachability: true);
        }

        /// <summary>
        /// 同上，但不做寻路，只回答「有没有被指派、还没进舱、且活着站在同一张图上」。
        /// 供装载界面与工具提示这类会被频繁重算的场合使用，避免每帧对每个单位跑一次寻路。
        /// </summary>
        public static void CollectAssignedNotYetAboard(CompShuttle shuttle, List<Pawn> outPawns)
        {
            Collect(shuttle, outPawns, requiringReachability: false);
        }

        private static void Collect(CompShuttle shuttle, List<Pawn> outPawns, bool requiringReachability)
        {
            outPawns.Clear();
            if (shuttle == null || shuttle.parent == null || !CargoPolicy.IsQuestShuttle(shuttle))
            {
                return;
            }
            CompTransporter transporter = shuttle.Transporter;
            if (transporter == null || !transporter.LoadingInProgressOrReadyToLaunch)
            {
                return;
            }
            Map map = shuttle.parent.MapHeld;
            if (map == null)
            {
                return;
            }
            tmpTransporters.Clear();
            if (transporter.groupID < 0)
            {
                tmpTransporters.Add(transporter);
            }
            else
            {
                TransporterUtility.GetTransportersInGroup(transporter.groupID, map, tmpTransporters);
            }
            Thing shuttleThing = shuttle.parent;
            if (requiringReachability)
            {
                CargoPolicy.CollectAssignedCargo(tmpTransporters, pawn => CargoPolicy.IsStillBoarding(pawn, shuttleThing), outPawns);
            }
            else
            {
                CargoPolicy.CollectAssignedCargo(tmpTransporters, pawn => CargoPolicy.IsStillBoardingInGeneral(pawn, shuttleThing), outPawns);
            }
        }

        private static void NotifyHoldStarted(CompShuttle shuttle)
        {
            if (tmpCargo.Count == 0)
            {
                return;
            }
            string names = tmpCargo.Select(pawn => pawn.LabelShort).ToCommaList(useAnd: true);
            Messages.Message("ShuttleCargoBoardingHold".Translate(names), new LookTargets(shuttle.parent), MessageTypeDefOf.NeutralEvent, historical: false);
        }
    }
}
