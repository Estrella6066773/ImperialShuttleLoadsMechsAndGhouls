using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 保住原版的「自动装载」开关：挂钩 CompShuttle 的 Autoloadable 属性读取器。
    ///
    /// 联动关系：
    ///   CompShuttle.CompGetGizmosExtra 只有在 Autoloadable 为真时才画出「自动装载」开关；
    ///   Autoloadable 的意思是「这台穿梭机上所有原版认为可装载的东西，都是任务点名要的」——
    ///   它靠 TransporterUtility.AllSendablePawns 逐个检查，遇到一个非任务要求的就返回 false。
    ///
    /// 为什么本模组会影响它：Patch_CompShuttle_IsAllowed 把机械族与亚人也放进了可装载名单，
    /// 于是玩家殖民地只要有一个食尸鬼或一台自主机械族，Autoloadable 就永远为假，
    /// 原版本来会出现的自动装载开关会凭空消失。
    ///
    /// 做法：后缀在原方法已经返回 false 时，用同一套逻辑重算一遍，但跳过本模组放行的货物，
    /// 相当于把「多出来的机械族与亚人」从这道判断题里摘出去，让开关的可见性与原版保持一致。
    ///
    /// 注意：
    /// 1. 只在任务穿梭机上重算；玩家自有穿梭机的 Autoloadable 原本就因 IsPlayerShuttle 为 false，保持不动。
    /// 2. 重算完全复用原版方法（AllSendablePawns / AllSendableItems / IsRequired），不自己解释什么是「被要求的」。
    /// 3. 自动装载一旦开启，原版会清空并重写 leftToLoad，只保留任务要求的物品，
    ///    此时玩家手动勾的机械族与亚人会被原版清掉——这是原版自动装载本身的行为，本模组不介入。
    /// </summary>
    [HarmonyPatch(typeof(CompShuttle), "get_Autoloadable")]
    internal static class Patch_CompShuttle_Autoloadable
    {
        private static readonly List<CompTransporter> tmpTransporters = new List<CompTransporter>();

        [HarmonyPostfix]
        private static void Postfix(CompShuttle __instance, ref bool __result)
        {
            if (__result)
            {
                return;
            }
            if (!CargoPolicy.IsQuestShuttle(__instance))
            {
                return;
            }
            if (!__instance.parent.Spawned)
            {
                return;
            }
            Map map = __instance.parent.Map;
            if (map == null || __instance.Transporter == null)
            {
                return;
            }
            __result = AllSendableThingsAreRequired(__instance, map);
        }

        private static bool AllSendableThingsAreRequired(CompShuttle shuttle, Map map)
        {
            tmpTransporters.Clear();
            tmpTransporters.Add(shuttle.Transporter);
            foreach (Pawn pawn in TransporterUtility.AllSendablePawns(tmpTransporters, map))
            {
                if (!CargoPolicy.IsOurCargo(pawn) && !shuttle.IsRequired(pawn))
                {
                    return false;
                }
            }
            foreach (Thing thing in TransporterUtility.AllSendableItems(tmpTransporters, map))
            {
                if (!shuttle.IsRequired(thing))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
