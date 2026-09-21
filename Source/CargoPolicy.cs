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
        /// 设置里「任务穿梭机的人数限制」总开关是否打开。
        ///
        /// 联动关系（谁会来问这里）：
        ///   CargoPolicy.BoardingLimit         -> 上限为 0，装载与起飞两处因此都不再数人头
        ///   Patch_CheckForErrors              -> 关闭时不做「按设置收窄名单」这一步
        ///   ManualLaunch.RefuseReason          -> 关闭时不再因为人多或人少而禁用起飞按钮
        ///
        /// 关闭后的效果就是「本模组不碰人数」：装载界面按原版口径判断，右键进舱不数人头，
        ///   起飞按钮不会因为机上有几个占名额的人而变灰。
        /// 但任务自己的硬性要求不受影响：任务点名的必需人员与物品没到齐时，
        ///   AllRequiredThingsLoaded 仍为假，原版那套「缺东西就飞不了」照旧生效——
        ///   那是任务对剧情的安排，本模组不替它放行。
        ///
        /// 注意：设置对象取不到时返回 false（当作关闭），也就是退回「不介入」，绝不因设置文件损坏而改变玩法。
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
        /// 该单位是否要被算进任务穿梭机的「人数上限」。
        ///
        /// 联动关系（谁会来问这里）：
        ///   Dialog_LoadTransporters.CheckForErrors -> 点确定时的人数上限检查，见 Patch_CountLimit
        ///   CompShuttle.IsAllowedNow               -> 右键让单位进舱时的上限检查，见 Patch_CountLimit
        ///   CargoPolicy.QuotaAboard                -> 起飞按钮判断「多带了人」时的计数
        ///
        /// 语义：返回 true 表示「占名额」，也就是在这几处检查里与其他殖民者一样计数。
        ///   机械族与亚人默认返回 false，于是可以随行而不挤占任务要的人数；
        ///   奴隶（原版 IsColonist 对奴隶为真）与囚犯默认返回 true，与原版口径一致；
        ///   其余单位（自由的殖民者、动物等）一律返回 true，本模组不碰。
        ///
        /// 注意：
        /// 1. 每次现读设置，玩家改完设置立即生效；设置对象取不到时一律返回 true，退回原版口径。
        /// 2. 这里只回答「算不算名额」。要不要用名额拦人另看 LimitEnforced——
        ///    总开关关闭时上面三处都会直接跳过，本方法不会被问到。
        /// 3. 这里不判断「能不能上机」。能否登机由 CompShuttle.IsAllowed 决定（本模组另有放行补丁）。
        /// 4. 一个单位若同时属于多类（例如被奴役的亚人），按「机械族 -> 亚人 -> 奴隶 -> 囚犯」的顺序取第一个匹配项，
        ///    也就是优先听从本模组自己那两类设置。
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
                // 总开关关闭（或设置读不到）：一律返回「占名额」，等于原版口径，本模组的区分不生效。
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

        /// <summary>
        /// 该单位是不是「玩家自己这边的人」，也就是会被算进名额的对象。
        ///
        /// 联动关系：QuotaAboard / CountEnteringQuotaPawns 都先问这一句，
        /// 于是起飞按钮与右键进舱的名额判定只统计玩家要带的人。
        ///
        /// 为什么还要这一层：任务穿梭机经常自带乘客（王权使团、帝国卫队、任务要押送的囚犯），
        /// 他们既不是玩家带的人、也不会因为名额被清出机舱，若把他们也数进名额，
        /// 一台本该装满 8 名殖民者的穿梭机会因为机上坐着 3 个帝国兵而永远「超员」、永远无法起飞。
        /// 所以名额只算：玩家阵营的单位、以及殖民地自己的奴隶与囚犯。
        /// </summary>
        public static bool BelongsToPlayerSide(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            return pawn.Faction == Faction.OfPlayer || pawn.IsSlaveOfColony || pawn.IsPrisonerOfColony;
        }

        /// <summary>
        /// 机上最多允许有几个占名额的单位：装载与起飞都用这个数当上限；0 表示不设上限。
        ///
        /// 联动关系：
        ///   Patch_CompShuttle_IsAllowedNow -> 右键进舱时按它拦人；
        ///   ManualLaunch.RefuseReason      -> 起飞按钮按它判断「多带了人」。
        ///
        /// 总开关（LimitEnforced）关闭时直接返回 0：上两处都会因为「没有上限」而完全不介入，
        ///   于是玩家可以随意多带少带，起飞按钮也不会因为人数不正确而变灰。
        ///
        /// 开启时取 QuotaSlots 与 RequiredColonistCount 里的较大者，理由是一个必须避开的死锁：
        ///   有任务同时要求「所有殖民者都上机」与一个较小的人数名额（例如任务生成时殖民者 8 人、
        ///   之后殖民地多出生或收留了几口人）。这时任务要的人数会变成「全部殖民者」，
        ///   若上限还是按名额的 8 算，玩家既被拦住装不下最后几个人，又因为要求没达满而不能起飞，
        ///   这台穿梭机就永远走不了。取较大者保证「任务要求的那几个人一定有位置」，
        ///   超员判定因此永远只在名额确实比任务要求更宽、而玩家又多塞了人的时候才生效。
        /// </summary>
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

        /// <summary>
        /// 任务给这台穿梭机留出的「占名额人数」上限；任务没设名额时返回 0，表示不设限。
        ///
        /// 联动关系（谁会来问这里）：
        ///   CargoPolicy.BoardingLimit  -> 先用这里的数字与任务要求取大者，才是最终上限
        ///   CargoPolicy.QuotaAboard / CountEnteringQuotaPawns -> 都用它作为比较的基准
        ///
        /// 数字怎么来：任务给穿梭机的「人数」有两个字段——
        ///   requiredColonistCount：任务点名要带的殖民者数量，装载界面就是拿它拦人的；
        ///   maxColonistCount：任务允许的上限，原版只在右键进舱时用，绝大多数任务留空（-1）。
        /// 两个都设了的时候取较小的那个：装载界面看的是前一个，右键看的是后一个，
        /// 取小值等于「两边谁更严就按谁算」，玩家在装载界面看到的报错数字因此与实际拦人的口径一致。
        /// 两个都没设（都不大于 0）时返回 0：这台穿梭机没有名额概念，一切照原版。
        /// </summary>
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

        /// <summary>
        /// 机舱里此刻占名额的单位数；可排除一个正在判定的对象（通常是正打算进舱的那一个）。
        ///
        /// 为什么只数单位不数物品：名额说的是「几个人」，货物按载重另算（Dialog_LoadTransporters.CheckForErrors 分开判）。
        /// 两类被跳过：不占名额的类别（默认设置下的机械族与亚人），以及不属于玩家这一边的单位
        /// （任务自带的帝国乘客等，见 BelongsToPlayerSide），后者与装载界面「勾选时谁被一起数进去」口径一致。
        /// </summary>
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
                if (BelongsToPlayerSide(pawn) && CountsTowardShuttleLimit(pawn))
                {
                    num++;
                }
            }
            return num;
        }

        /// <summary>
        /// 全图上正在往这台穿梭机里走、且占名额的单位数（不含 excluded 自己）。
        ///
        /// 口径照抄原版 CompShuttle.IsAllowedNow 的中段：遍历全图单位，它自己的当前工作与排队工作里
        /// 凡是「进入这台穿梭机」的都算一条，同一单位有多条排队就多算——与其它模组的预期保持一致。
        /// 差别只在两处跳过：不占名额的类别，以及不属于玩家这一边的单位（理由见 QuotaAboard）。
        /// </summary>
        public static int CountEnteringQuotaPawns(Thing shuttleThing, Thing excluded = null)
        {
            Map map = shuttleThing?.Map;
            if (map == null)
            {
                return 0;
            }
            int num = 0;
            List<Pawn> allPawns = map.mapPawns.AllPawns;
            for (int i = 0; i < allPawns.Count; i++)
            {
                Pawn pawn = allPawns[i];
                if (pawn == excluded || pawn.jobs == null || pawn.jobs.curDriver == null)
                {
                    continue;
                }
                if (!BelongsToPlayerSide(pawn) || !CountsTowardShuttleLimit(pawn))
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
