using RimWorld;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 「补出装载面板」与「补出起飞按钮」共用的判定与按钮构造。
    ///
    /// 联动关系（谁在什么时候问这里）：
    ///   Patch_CompShuttle_ShowLoadingGizmos -> ForceLoadingPanel：这台穿梭机该不该让玩家装载
    ///   Patch_CompShuttle_CompGetGizmosExtra-> BuildLaunchCommand：这颗起飞按钮要不要出现、按下去做什么
    ///   ManualLaunch（等待类船务那颗起飞按钮）-> 共用同一套「现在能不能飞」的说明文字
    ///
    /// 原版为什么会让玩家找不到装载面板与起飞按钮：
    ///   两个按钮都由原版自己生成，而它们都挂在「当前船务」上：
    ///     装载按钮：CompTransporter.CompGetGizmosExtra 开头就问 CompShuttle.ShowLoadingGizmos；
    ///     起飞按钮：CompShuttle.CompGetGizmosExtra 画的是 shipParent.curJob.GetJobGizmos()。
    ///   穿梭机停在地图上但船务队列已经空了的时候（TransportShip.curJob 为 null），
    ///   TransportShip.ShowGizmos 跟着为 false，于是两个按钮同时消失。
    ///   文化 + 王权的「古代遗迹」任务正是这种：穿梭机落地后队列是空的，
    ///   要等玩家装完货、任务发出 SendShuttleAway 信号才开始排队去程与返程的船务。
    ///
    /// 注意：
    /// 1. 所有判定都只在原版说「不」的时候才介入，属于「只补不夺」的改动。
    /// 2. 只认「玩家殖民者远征」的穿梭机（CargoPolicy.IsPlayerExpeditionShuttle）：
    ///    加冕典礼、坠机救援这类「帝国自己负责上下客」的穿梭机，以及款待任务那类
    ///    「来接走暂住客人」的穿梭机一律放过。
    /// 3. 设置对象取不到时一律当作「关闭」，退回原版行为，绝不因设置文件损坏而改变游戏玩法。
    /// </summary>
    internal static class QuestShuttleControls
    {
        /// <summary>设置里是否开启了「任务穿梭机始终显示装载面板」。</summary>
        public static bool LoadingPanelEnabled => ImperialShuttleLoadsMod.Settings?.alwaysShowLoadingPanel ?? false;

        /// <summary>设置里是否开启了「任务穿梭机改为手动起飞」。</summary>
        public static bool LaunchButtonEnabled => ImperialShuttleLoadsMod.Settings?.manualLaunchQuestShuttles ?? false;

        /// <summary>
        /// 该不该替玩家把装载面板放出来。
        ///
        /// 三个条件：任务把装载交给了玩家；穿梭机此刻确实停在地图上；当前船务没有刻意藏按钮。
        ///
        /// 为什么要看当前船务：卸载中的穿梭机（ShipJob_Unload）本来就该藏起装载按钮，
        ///   加冕典礼的穿梭机也用 showGizmos: false 把按钮整条藏掉，离开时机由任务脚本安排。
        ///   这两种场合放行按钮，只会让玩家在错误的时刻往机舱里塞东西。
        /// </summary>
        public static bool ForceLoadingPanel(CompShuttle shuttle)
        {
            if (!IsSpawnedPlayerLoadableQuestShuttle(shuttle))
            {
                return false;
            }
            ShipJob job = shuttle.shipParent.curJob;
            return job == null || job.ShowGizmos;
        }

        /// <summary>
        /// 当前没有船务时，替穿梭机补一颗「起飞」按钮；不该补时返回 null。
        ///
        /// 为什么只在没有船务时补：有船务时按钮由那条船务自己出——
        ///   等待类船务由 ManualLaunch.Augment 补（它知道那条船务的目的地与等待规则），
        ///   原版自己的按钮也由那里出。两边同时出现会变成两颗按钮。
        ///
        /// 为什么要求穿梭机带任务标签：按下按钮的动作是给任务发一条信号，
        ///   没有任务标签的穿梭机没人听这条信号，按钮按下去不会有任何反应。
        /// </summary>
        public static Command_Action BuildLaunchCommand(CompShuttle shuttle)
        {
            if (!LaunchButtonEnabled)
            {
                return null;
            }
            if (!IsSpawnedPlayerLoadableQuestShuttle(shuttle))
            {
                return null;
            }
            if (shuttle.shipParent.curJob != null)
            {
                return null;
            }
            Thing ship = shuttle.parent;
            if (ship.questTags.NullOrEmpty())
            {
                return null;
            }
            Command_Action command = new Command_Action
            {
                defaultLabel = "CommandSendShuttle".Translate(),
                defaultDesc = "ShuttleCargoRequestDepartureDesc".Translate(),
                icon = CompLaunchable.LaunchCommandTex,
                alsoClickIfOtherInGroupClicked = false,
                action = delegate
                {
                    RequestQuestDeparture(ship);
                }
            };
            ManualLaunch.ApplyReadinessNote(command, shuttle);
            return command;
        }

        /// <summary>
        /// 按下「起飞」：把「机舱里进东西了」这件事重新报给任务，让任务自己那套流程判断能不能走。
        ///
        /// 为什么发这条信号：原版 CompTransporter.Notify_ThingAdded 每收进一件东西都会发同一条
        ///   「ThingAdded」任务信号（即 QuestUtility.QuestTargetSignalPart_ShipThingAdded），
        ///   任务的流程链条（检查是否装满 → 检查有没有能驾驶的人 → 发出 SendShuttleAway）
        ///   就是被它推动的。玩家手动要求起飞时重发一次，等于请任务再算一遍，
        ///   既不跳过任务的任何检查，也不用本模组去猜某个任务的信号名与目的地。
        ///
        /// 注意：任务要求的人或物没到齐时按钮是灰的（ApplyReadinessNote），
        ///   因此走到这里时装载一定已经完成；任务若还有别的条件（例如机上没有能骇入的人），
        ///   它会自己弹出选择框，玩家在原版那条路上怎么选，这里就怎么走。
        /// </summary>
        private static void RequestQuestDeparture(Thing ship)
        {
            QuestUtility.SendQuestTargetSignals(ship.questTags, QuestUtility.QuestTargetSignalPart_ShipThingAdded, ship.Named("SUBJECT"));
        }

        private static bool IsSpawnedPlayerLoadableQuestShuttle(CompShuttle shuttle)
        {
            if (shuttle?.parent == null || shuttle.shipParent == null)
            {
                return false;
            }
            if (!CargoPolicy.IsPlayerExpeditionShuttle(shuttle))
            {
                return false;
            }
            return shuttle.parent.Spawned && shuttle.Transporter != null;
        }
    }
}
