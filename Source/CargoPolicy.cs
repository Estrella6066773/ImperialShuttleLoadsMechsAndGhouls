using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 本模组所有补丁共用的判定集合。
    ///
    /// 联动关系（谁在什么时候问这里）：
    ///   CompShuttle.IsAllowed            -> 原版问「这件东西能不能上机」，放行机械族与亚人
    ///   CaravanUIUtility.AddPawnsSections-> 原版装载界面分区，判断哪些单位原版自己就会显示
    ///   TransporterUtility.MakeLordsAsAppropriate -> 装载领主该收哪些单位
    ///   LoadTransportersJobUtility.HasJobOnTransporter -> 谁去搬运、谁只是登机
    ///   ShipJob_Wait.SendAway            -> 起飞放行判定，看还有没有本单位在往机舱里走
    ///
    /// 注意：
    /// 1. 「本模组的货物」只认两类：玩家阵营的机械族（生物科技）、玩家阵营的亚人（异象，即食尸鬼系列）。
    ///    判定沿用原版属性 IsColonyMech / IsColonySubhuman，不自己解析 Def，避免和别的模组的突变体设定打架。
    /// 2. IsColonyMech 要求目标没有精神崩溃；正在发狂的机械族不会被放行，与原版对殖民者的口径一致。
    /// 3. 所有判定都必须能在战斗与界面绘制线程之外被安全调用（只读 Pawn / Thing 的公开状态），不缓存 Pawn 引用。
    /// </summary>
    internal static class CargoPolicy
    {
        /// <summary>是否为本模组要放行的登机者（玩家阵营的机械族或亚人）。</summary>
        public static bool IsOurCargo(Thing thing)
        {
            if (!(thing is Pawn pawn))
            {
                return false;
            }
            if (pawn.Faction != Faction.OfPlayer)
            {
                return false;
            }
            return IsPlayerMech(pawn) || IsPlayerSubhuman(pawn);
        }

        /// <summary>玩家阵营的机械族（含未被监督的自主机械族）。</summary>
        public static bool IsPlayerMech(Pawn pawn)
        {
            return ModsConfig.BiotechActive && pawn.IsColonyMech;
        }

        /// <summary>玩家阵营的亚人，即异象资料片的食尸鬼系列突变体。</summary>
        public static bool IsPlayerSubhuman(Pawn pawn)
        {
            return ModsConfig.AnomalyActive && pawn.IsColonySubhuman;
        }

        /// <summary>
        /// 这个单位此刻能不能自己走进机舱。
        ///
        /// 原版只用这套健康判定拦住设置了「只收健康单位」的穿梭机（PawnIsHealthyEnoughForShuttle），
        /// 本模组把同一套判定用在放行条件上，好处是：被放行的单位一定走得动，
        /// 于是「起飞前等待装载完成」那条规则永远不会被一个瘫倒在地的单位永久卡住。
        /// 这里不要求 Manipulation（不是让他们搬东西，只是自己走进去）。
        /// </summary>
        public static bool CanBoardOnFoot(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Downed || pawn.InMentalState)
            {
                return false;
            }
            if (!pawn.health.capacities.CanBeAwake)
            {
                return false;
            }
            return pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving);
        }

        /// <summary>
        /// 是否为本模组要处理的任务穿梭机。
        ///
        /// 原版能载机械族与亚人的穿梭机本来就有两种：许可呼叫的穿梭机（permitShuttle，
        /// IsAllowed 直接对玩家阵营放行）与玩家自有的穿梭机（Odyssey 的个人穿梭机，
        /// IsAllowed 开头就返回 true）。这两种不需要也不应该被本模组改动，
        /// 否则会把「哪些东西需要本模组操心」的边界搅浑。
        /// 剩下的就是任务脚本空投来的穿梭机，也就是本模组唯一的目标。
        /// </summary>
        public static bool IsQuestShuttle(CompShuttle shuttle)
        {
            if (shuttle == null)
            {
                return false;
            }
            if (shuttle.permitShuttle)
            {
                return false;
            }
            return !shuttle.IsPlayerShuttle;
        }

        /// <summary>
        /// 原版装载界面的分区会不会收纳这个单位。
        ///
        /// 这里逐条照抄 CaravanUIUtility.AddPawnsSections 里的筛选条件，
        /// 用途是：本模组只补「原版看不见的那几类」，原版已经会显示的（例如已受监督的机械族）
        /// 绝不能重复加一个分区，否则装载界面会出现两行同一个单位。
        /// 注意分区是否创建还取决于资料片开关，所以这里也要按同样的开关判断。
        /// </summary>
        public static bool HasVanillaPawnSection(Pawn pawn)
        {
            if (pawn.IsFreeNonSlaveColonist)
            {
                return true;
            }
            if (ModsConfig.IdeologyActive && pawn.IsSlave)
            {
                return true;
            }
            if (pawn.IsPrisoner)
            {
                return true;
            }
            if (pawn.Downed && CaravanUtility.ShouldAutoCapture(pawn, Faction.OfPlayer))
            {
                return true;
            }
            if (pawn.IsAnimal)
            {
                return true;
            }
            if (ModsConfig.BiotechActive && pawn.IsColonyMech && pawn.OverseerSubject != null
                && pawn.OverseerSubject.State == OverseerSubjectState.Overseen)
            {
                return true;
            }
            if (ModsConfig.AnomalyActive && pawn.IsColonySubhuman && pawn.mutant != null
                && pawn.mutant.Def.canTravelInCaravan)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// 该单位是否要被算进任务穿梭机的「人数上限」。
        ///
        /// 联动关系（谁会来问这里）：
        ///   Dialog_LoadTransporters.CheckForErrors -> 点确定时的人数上限检查，见 Patch_CountLimit
        ///   CompShuttle.IsAllowedNow               -> 右键让单位进舱时的上限检查，见 Patch_CountLimit
        ///
        /// 语义：返回 true 表示「占名额」，也就是在这两处检查里与其他殖民者一样计数。
        ///   机械族与亚人默认返回 false，于是可以随行而不挤占任务要的人数；
        ///   奴隶（原版 IsColonist 对奴隶为真）与囚犯默认返回 true，与原版口径一致；
        ///   其余单位（自由的殖民者、动物等）一律返回 true，本模组不碰。
        ///
        /// 注意：
        /// 1. 每次现读设置，玩家改完设置立即生效；设置对象取不到时一律返回 true，退回原版口径。
        /// 2. 这里不判断「能不能上机」。能否登机由 CompShuttle.IsAllowed 决定（本模组另有放行补丁）。
        /// 3. 一个单位若同时属于多类（例如被奴役的亚人），按「机械族 -> 亚人 -> 奴隶 -> 囚犯」的顺序取第一个匹配项，
        ///    也就是优先听从本模组自己那两类设置。
        /// </summary>
        public static bool CountsTowardShuttleLimit(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            ImperialShuttleLoadsSettings settings = ImperialShuttleLoadsMod.Settings;
            if (settings == null)
            {
                return true;
            }
            if (IsPlayerMech(pawn))
            {
                return settings.mechsCountTowardLimit;
            }
            if (IsPlayerSubhuman(pawn))
            {
                return settings.subhumansCountTowardLimit;
            }
            if (pawn.IsSlave)
            {
                return settings.slavesCountTowardLimit;
            }
            if (pawn.IsPrisoner)
            {
                return settings.prisonersCountTowardLimit;
            }
            return true;
        }

        /// <summary>是否有任何一类单位被排除在上限之外；全为「计入」时补丁可以完全不介入。</summary>
        public static bool AnyCategoryLeftOutOfLimit()
        {
            ImperialShuttleLoadsSettings settings = ImperialShuttleLoadsMod.Settings;
            return settings != null && settings.AnythingLeftOutOfLimit;
        }

        /// <summary>
        /// 是否可以把该单位拉进「装载并登机」领主。
        /// 与原版 TransporterUtility.MakeLordsAsAppropriate 对殖民者的口径一致：倒在地上的、不在图上的不收。
        /// </summary>
        public static bool CanJoinLoadingLord(Pawn pawn)
        {
            return IsOurCargo(pawn) && !pawn.Downed && pawn.Spawned;
        }

        /// <summary>
        /// 该单位是否还在地图上、且正在等着走进这台穿梭机。
        /// 用于「起飞前等待装载完成」：只有真正走得过去、还没进舱的单位才会让穿梭机多等一会儿，
        /// 已经进舱（已不在图上）、已倒地、已死亡、被墙围住的单位一律不再计入，
        /// 这样任何异常情况都不会把穿梭机永久扣在地图上。
        /// </summary>
        public static bool IsStillBoarding(Pawn pawn, Thing shuttle)
        {
            if (!IsStillBoardingInGeneral(pawn, shuttle))
            {
                return false;
            }
            return pawn.CanReach(shuttle, PathEndMode.Touch, Danger.Deadly);
        }

        /// <summary>
        /// IsStillBoarding 的低成本版本：只查状态，不做寻路。
        /// 供装载工具提示一类「会被频繁重算」的场合使用，避免每帧对每个单位跑一次寻路。
        /// </summary>
        public static bool IsStillBoardingInGeneral(Pawn pawn, Thing shuttle)
        {
            if (!IsOurCargo(pawn) || !CanBoardOnFoot(pawn) || !pawn.Spawned)
            {
                return false;
            }
            if (shuttle == null || shuttle.Destroyed || !shuttle.Spawned || pawn.Map != shuttle.MapHeld)
            {
                return false;
            }
            return true;
        }

        /// <summary>机舱里已有的自由殖民者数量，口径与原版 CompShuttle 内部的人数统计一致。</summary>
        public static int ColonistsAboard(CompShuttle shuttle)
        {
            ThingOwner container = shuttle?.Transporter?.innerContainer;
            if (container == null)
            {
                return 0;
            }
            int num = 0;
            for (int i = 0; i < container.Count; i++)
            {
                if (container[i] is Pawn pawn && pawn.IsFreeColonist)
                {
                    num++;
                }
            }
            return num;
        }

        /// <summary>
        /// 这台穿梭机此刻要求机舱里至少有多少名自由殖民者，口径照抄原版 AllRequiredThingsLoaded：
        ///   任务指定的人数（requiredColonistCount）与「地图上所有自由殖民者」两者取大者。
        /// requireAllColonistsOnMap 为真时，后者还包括子地图（口袋地图）里的殖民者。
        /// </summary>
        public static int RequiredColonistCount(CompShuttle shuttle)
        {
            if (shuttle == null)
            {
                return 0;
            }
            int required = shuttle.requiredColonistCount;
            if (!shuttle.requireAllColonistsOnMap || shuttle.parent == null)
            {
                return required;
            }
            Map map = shuttle.parent.Map;
            if (map == null)
            {
                return required;
            }
            int onMap = map.mapPawns.FreeColonistsCount;
            foreach (Map pocket in map.ChildPocketMaps)
            {
                onMap += pocket.mapPawns.FreeColonistsCount;
            }
            return onMap > required ? onMap : required;
        }

        /// <summary>
        /// 把「已被指派上机、但还没进舱」的本模组货物收集到 outPawns。
        ///
        /// 数据来源是 CompTransporter.leftToLoad（玩家在装载界面点确定后由原版写入的待装载清单），
        /// 而不是界面传进来的临时名单：前者更贴近「订单」，也不会被别人改动过的参数影响。
        /// 进舱的单位会被原版从 leftToLoad 里移除，所以这里读到的天然就是「还没上机」的那批。
        /// </summary>
        public static void CollectAssignedCargo(List<CompTransporter> transporters, Func<Pawn, bool> filter, List<Pawn> outPawns)
        {
            outPawns.Clear();
            if (transporters == null)
            {
                return;
            }
            for (int i = 0; i < transporters.Count; i++)
            {
                List<TransferableOneWay> left = transporters[i]?.leftToLoad;
                if (left == null)
                {
                    continue;
                }
                for (int j = 0; j < left.Count; j++)
                {
                    if (left[j].CountToTransfer <= 0)
                    {
                        continue;
                    }
                    List<Thing> things = left[j].things;
                    for (int k = 0; k < things.Count; k++)
                    {
                        if (things[k] is Pawn pawn && !outPawns.Contains(pawn) && filter(pawn))
                        {
                            outPawns.Add(pawn);
                        }
                    }
                }
            }
        }
    }
}
