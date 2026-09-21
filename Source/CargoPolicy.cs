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
        /// 是否为本模组要处理的「任务穿梭机」——只回答「这不是许可穿梭机、也不是玩家自有穿梭机」。
        ///
        /// 原版能载机械族与亚人的穿梭机本来就有两种：许可呼叫的穿梭机（permitShuttle，
        /// IsAllowed 直接对玩家阵营放行）与玩家自有的穿梭机（Odyssey 的个人穿梭机，
        /// IsAllowed 开头就返回 true）。这两种不需要也不应该被本模组改动，
        /// 否则会把「哪些东西需要本模组操心」的边界搅浑。
        ///
        /// 注意：这一层只是粗筛。真正决定「本模组要不要动手」的是下面那一条
        ///   IsPlayerExpeditionShuttle，各处补丁请优先用它。
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
        /// 这台任务穿梭机是不是「玩家带着自己殖民者出行」的那种——本模组所有改动的总适用范围。
        ///
        /// 联动关系（谁会来问这里，全部补丁都以它为准）：
        ///   Patch_CompShuttle_IsAllowed          -> 机械族与亚人能不能登这台机
        ///   Patch_MakeLordsAsAppropriate         -> 点了确定之后谁走向穿梭机
        ///   Patch_CompShuttle_Autoloadable       -> 「自动装载」开关还画不画
        ///   ShuttleDepartureHold.ShouldHold      -> 起飞前要不要等本单位进舱
        ///   Patch_CountLimit（点确定与右键两处）  -> 人数上限怎么数
        ///   LoadDialogAccess.QuestShuttleOf      -> 装载界面的载重要不要补正
        ///   ManualLaunch.AppliesTo               -> 要不要把自动起飞换成手动起飞
        ///
        /// 三个条件同时成立才算「玩家殖民者远征」：
        ///   1. 是任务穿梭机（见 IsQuestShuttle）；
        ///   2. 任务允许殖民者登机（acceptColonists）；
        ///   3. 任务点名了要带的己方人数（requiredColonistCount 或 requireAllColonistsOnMap）——
        ///      也就是这趟航班本来就指望玩家自己把人和货装上去。
        ///
        /// 为什么必须这么窄：任务穿梭机还有一大类「接送别人家的客人」——
        ///   王权的款待任务（Script_Hospitality_*）、派遣劳工的许可、借出殖民者的任务等等，
        ///   穿梭机是来接走暂住在殖民地的使节、劳工、囚犯的，撤离时机由任务自己的倒计时与信号决定
        ///   （Util_TransportShip_Pickup：requiredPawns 是别人家的单位，既没有 requireColonistCount，
        ///   也没有 acceptColonists）。本模组一旦插手这类航班，就会出现
        ///   「玩家勾的机械族还没走完、任务该撤的人却撤不了」这种干扰，因此整类放过。
        ///
        /// 注意：这里回答的是「任务有没有把装载交给玩家」，不回答「现在能不能装」。
        ///   谁能登机仍由 IsAllowed 决定，能不能起飞仍由 AllRequiredThingsLoaded 决定。
        /// </summary>
        public static bool IsPlayerExpeditionShuttle(CompShuttle shuttle)
        {
            if (!IsQuestShuttle(shuttle))
            {
                return false;
            }
            if (!shuttle.acceptColonists)
            {
                return false;
            }
            return shuttle.requiredColonistCount > 0 || shuttle.requireAllColonistsOnMap;
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
        /// 设置里「按人数限制拦人」总开关是否打开。
        ///
        /// 联动关系：BoardingLimit / Patch_CheckForErrors / ManualLaunch.RefuseReason。
        /// 关闭后连原版「只允许 N 名殖民者」也会放过；任务点名的必需人员与物品仍须齐备。
        /// 设置对象取不到时返回 false（当作关闭）。
        /// </summary>
        public static bool LimitEnforced
        {
            get
            {
                ImperialShuttleLoadsSettings settings = ImperialShuttleLoadsMod.Settings;
                return settings != null && settings.enforcePassengerLimit;
            }
        }

        /// <summary>
        /// 设置里「按载重限制拦货」是否打开。关闭后装载界面 MassCapacity 视为无限。
        /// 设置对象取不到时返回 true，保持原版载重判定。
        /// </summary>
        public static bool MassLimitEnforced
        {
            get
            {
                ImperialShuttleLoadsSettings settings = ImperialShuttleLoadsMod.Settings;
                return settings == null || settings.enforceMassLimit;
            }
        }

        /// <summary>
        /// 是否正在任务地图上装载返程：玩家远征穿梭机且当前地图不是玩家家园。
        /// 返程时囚犯不占名额，便于带回俘虏。
        /// </summary>
        public static bool IsReturnLoading(CompShuttle shuttle)
        {
            if (!IsPlayerExpeditionShuttle(shuttle) || shuttle.parent == null)
            {
                return false;
            }
            Map map = shuttle.parent.Map;
            return map != null && !map.IsPlayerHome;
        }

        /// <summary>
        /// 该单位是否占人数名额（无穿梭机上下文）。返程囚犯请用带 shuttle 的重载。
        /// </summary>
        public static bool CountsTowardShuttleLimit(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            ImperialShuttleLoadsSettings settings = ImperialShuttleLoadsMod.Settings;
            if (settings == null || !settings.enforcePassengerLimit)
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

        /// <summary>同上，带穿梭机上下文：返程时囚犯不占名额。</summary>
        public static bool CountsTowardShuttleLimit(Pawn pawn, CompShuttle shuttle)
        {
            if (pawn != null && pawn.IsPrisoner && !pawn.IsSlave && IsReturnLoading(shuttle))
            {
                return false;
            }
            return CountsTowardShuttleLimit(pawn);
        }

        /// <summary>是否为玩家这边要带的人（名额只统计这些）。</summary>
        public static bool BelongsToPlayerSide(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            return pawn.Faction == Faction.OfPlayer || pawn.IsSlaveOfColony || pawn.IsPrisonerOfColony;
        }

        /// <summary>机上最多允许几个占名额单位；0 表示不设上限。</summary>
        public static int BoardingLimit(CompShuttle shuttle)
        {
            if (!LimitEnforced)
            {
                return 0;
            }
            int slots = QuotaSlots(shuttle);
            int required = RequiredColonistCount(shuttle);
            return required > slots ? required : slots;
        }

        /// <summary>任务给的占名额上限；未设时返回 0。</summary>
        public static int QuotaSlots(CompShuttle shuttle)
        {
            if (shuttle == null)
            {
                return 0;
            }
            int slots = 0;
            if (shuttle.requiredColonistCount > 0)
            {
                slots = shuttle.requiredColonistCount;
            }
            if (shuttle.maxColonistCount > 0)
            {
                slots = slots <= 0 ? shuttle.maxColonistCount : Math.Min(slots, shuttle.maxColonistCount);
            }
            return slots;
        }

        /// <summary>机舱里此刻占名额的单位数。</summary>
        public static int QuotaAboard(CompShuttle shuttle, Thing excluded = null)
        {
            ThingOwner container = shuttle?.Transporter?.innerContainer;
            if (container == null)
            {
                return 0;
            }
            int num = 0;
            for (int i = 0; i < container.Count; i++)
            {
                Thing thing = container[i];
                if (thing == excluded || !(thing is Pawn pawn))
                {
                    continue;
                }
                if (BelongsToPlayerSide(pawn) && CountsTowardShuttleLimit(pawn, shuttle))
                {
                    num++;
                }
            }
            return num;
        }

        /// <summary>全图正在往这台穿梭机走、且占名额的单位数。</summary>
        public static int CountEnteringQuotaPawns(Thing shuttleThing, Thing excluded = null)
        {
            Map map = shuttleThing?.Map;
            if (map == null)
            {
                return 0;
            }
            CompShuttle shuttle = shuttleThing.TryGetComp<CompShuttle>();
            int num = 0;
            List<Pawn> allPawns = map.mapPawns.AllPawns;
            for (int i = 0; i < allPawns.Count; i++)
            {
                Pawn pawn = allPawns[i];
                if (pawn == excluded || pawn.jobs == null || pawn.jobs.curDriver == null)
                {
                    continue;
                }
                if (!BelongsToPlayerSide(pawn) || !CountsTowardShuttleLimit(pawn, shuttle))
                {
                    continue;
                }
                JobQueue queue = pawn.jobs.jobQueue;
                for (int j = 0; j < queue.Count; j++)
                {
                    if (IsEnteringShuttle(queue[j].job, shuttleThing))
                    {
                        num++;
                    }
                }
                if (IsEnteringShuttle(pawn.jobs.curJob, shuttleThing))
                {
                    num++;
                }
            }
            return num;
        }

        private static bool IsEnteringShuttle(Job job, Thing shuttleThing)
        {
            return job != null
                && job.def != null
                && typeof(JobDriver_EnterTransporter).IsAssignableFrom(job.def.driverClass)
                && job.GetTarget(TargetIndex.A).Thing == shuttleThing;
        }

        /// <summary>设置里是否有类别被排除在上限外。</summary>
        public static bool AnyCategoryLeftOutOfLimit()
        {
            ImperialShuttleLoadsSettings settings = ImperialShuttleLoadsMod.Settings;
            return settings != null && settings.AnythingLeftOutOfLimit;
        }

        /// <summary>名单里是否有人不占名额（含返程囚犯）。</summary>
        public static bool AnyPawnLeftOutOfLimit(List<Pawn> pawns, CompShuttle shuttle)
        {
            if (pawns == null)
            {
                return false;
            }
            for (int i = 0; i < pawns.Count; i++)
            {
                if (!CountsTowardShuttleLimit(pawns[i], shuttle))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>是否可拉进装载领主。</summary>
        public static bool CanJoinLoadingLord(Pawn pawn)
        {
            return IsOurCargo(pawn) && !pawn.Downed && pawn.Spawned;
        }

        /// <summary>单位是否还在地图上往这台穿梭机走。</summary>
        public static bool IsStillBoarding(Pawn pawn, Thing shuttle)
        {
            if (!IsStillBoardingInGeneral(pawn, shuttle))
            {
                return false;
            }
            return pawn.CanReach(shuttle, PathEndMode.Touch, Danger.Deadly);
        }

        /// <summary>IsStillBoarding 的低成本版本，不做寻路。</summary>
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

        /// <summary>机舱里已有的自由殖民者数量。</summary>
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
        /// 尚存可用的自由殖民者：已在机舱 + 地图与口袋地图上尚未登机的。
        /// 随行殖民者阵亡后下降，用来给任务要求人数封顶。
        /// </summary>
        public static int AvailableFreeColonists(CompShuttle shuttle)
        {
            int num = ColonistsAboard(shuttle);
            if (shuttle?.parent == null)
            {
                return num;
            }
            Map map = shuttle.parent.Map;
            if (map == null)
            {
                return num;
            }
            num += map.mapPawns.FreeColonistsCount;
            foreach (Map pocket in map.ChildPocketMaps)
            {
                num += pocket.mapPawns.FreeColonistsCount;
            }
            return num;
        }

        /// <summary>
        /// 此刻要求机上至少有多少名自由殖民者。
        /// 在原版口径上与尚存可用人数取小，避免途中阵亡后永远凑不齐。
        /// </summary>
        public static int RequiredColonistCount(CompShuttle shuttle)
        {
            if (shuttle == null)
            {
                return 0;
            }
            int required = shuttle.requiredColonistCount;
            if (shuttle.requireAllColonistsOnMap && shuttle.parent != null)
            {
                Map map = shuttle.parent.Map;
                if (map != null)
                {
                    int onMap = map.mapPawns.FreeColonistsCount;
                    foreach (Map pocket in map.ChildPocketMaps)
                    {
                        onMap += pocket.mapPawns.FreeColonistsCount;
                    }
                    onMap += ColonistsAboard(shuttle);
                    if (onMap > required)
                    {
                        required = onMap;
                    }
                }
            }
            int available = AvailableFreeColonists(shuttle);
            return available < required ? available : required;
        }

        /// <summary>收集已被指派上机但还没进舱的本模组货物。</summary>
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
