using HarmonyLib;
using RimWorld;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 起飞前等本单位进舱（一）：普通等待类船务任务的立即起飞。
    ///
    /// 联动关系：
    ///   ShipJob_Wait.TickInterval 在每 tick 检查 leaveImmediatelyWhenSatisfied 与 AllRequiredThingsLoaded，
    ///   两者同时成立就调用 SendAway，于是穿梭机在「任务要的人刚进舱」的同一瞬间起飞，
    ///   玩家勾的机械族与亚人即使只差十几秒就能走进来，也会被留在原地。
    ///
    /// 做法：SendAway 是「等待类船务决定离场」的唯一出口，用前缀在这里问一句 ShuttleDepartureHold；
    ///   需要等就整段跳过，本条船务保持工作状态，下一 tick 继续检查，等单位都进去后自然起飞。
    ///
    /// 注意：
    /// 1. 只拦 SendAway，不拦玩家操作：玩家主动起飞（本模组补的起飞按钮，或原版许可穿梭机的发射组按钮）走的是
    ///    直接调用 SendAway / ForceJob(FlyAway)，而手动起飞按钮在调用前置位 ManualSendInProgress，本判定会立刻放行。
    /// 2. 任务脚本主动送走穿梭机（QuestPart_SendShuttleAway 等）同样不经过这里，任务节奏不会被改。
    /// 3. 等待上限写在 ShuttleDepartureHold 里，最多多等 6 小时，之后无论装载是否完成都会放行。
    /// 4. 设置里开启「任务穿梭机改为手动起飞」后，原版「装齐就立刻飞」那条路径已被 Patch_ManualLaunch 静音，
    ///    正常游戏里本条前缀基本不会被触发；它保留下来应付玩家关掉该设置、或任务用自己的倒计时送走穿梭机的情况。
    /// </summary>
    [HarmonyPatch(typeof(ShipJob_Wait), "SendAway")]
    internal static class Patch_ShipJob_Wait_SendAway
    {
        [HarmonyPrefix]
        private static bool Prefix(ShipJob_Wait __instance)
        {
            return !ShuttleDepartureHold.ShouldHold(__instance.transportShip);
        }
    }

    /// <summary>
    /// 起飞前等本单位进舱（二）：返程可派遣穿梭机的立即起飞。
    ///
    /// 联动关系：ShipJob_WaitSendable 重写了 SendAway（会先算出回到哪个玩家据点，再安排 FlyAway），
    ///   而重写方法不调用基类版本，所以上一条针对 ShipJob_Wait.SendAway 的前缀在这里不会被触发，
    ///   必须单独挂一份，两者共用同一个 ShuttleDepartureHold 判定。
    ///
    /// 注意：该船务的 ShouldEnd 恒为 false，也就是说它只可能因为「所需人员已就位」或「目标消失」而离场，
    ///   本补丁拦的是前者；后者（sendAwayIfAllDespawned 一类）在 ShuttleDepartureHold 里已被 AllRequiredThingsLoaded 条件排除。
    ///   返程同样归手动起飞管：玩家在任务地点选中穿梭机点「起飞」时，本模组调的正是 WaitSendable 这版 SendAway，
    ///   因此去程与返程的目的地都由原版逻辑算出，不会被手动起飞打乱。
    /// </summary>
    [HarmonyPatch(typeof(ShipJob_WaitSendable), "SendAway")]
    internal static class Patch_ShipJob_WaitSendable_SendAway
    {
        [HarmonyPrefix]
        private static bool Prefix(ShipJob_WaitSendable __instance)
        {
            return !ShuttleDepartureHold.ShouldHold(__instance.transportShip);
        }
    }

    /// <summary>
    /// 起飞前等本单位进舱（三）：定时等待的倒计时到点。
    ///
    /// 联动关系：
    ///   ShipJob.TickInterval 每 tick 读 ShouldEnd；ShipJob_WaitTime.ShouldEnd 判断从开始等待起是否已经过了 duration，
    ///   一旦为真，本条船务结束，队列里的 ShipJob_FlyAway 接手，穿梭机起飞。
    ///   任务给穿梭机设的等待时间（例如救援任务里的 30000 tick）到点时，玩家的单位可能还在往机舱里走。
    ///
    /// 做法：后缀在原方法已经给出 true 时再问一次 ShuttleDepartureHold，需要等就写回 false，让它下一 tick 继续等。
    ///
    /// 注意：
    /// 1. 这一条不会让倒计时归零后无限等待：同样受 ShuttleDepartureHold 的 6 小时上限约束。
    /// 2. 若任务要的人还没到齐（AllRequiredThingsLoaded 为 false），判定直接不成立，
    ///    倒计时到点就照原版起飞——该走的时候不拦。
    /// </summary>
    [HarmonyPatch(typeof(ShipJob_WaitTime), "get_ShouldEnd")]
    internal static class Patch_ShipJob_WaitTime_ShouldEnd
    {
        [HarmonyPostfix]
        private static void Postfix(ShipJob_WaitTime __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }
            if (ShuttleDepartureHold.ShouldHold(__instance.transportShip))
            {
                __result = false;
            }
        }
    }
}
