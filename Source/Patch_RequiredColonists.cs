using HarmonyLib;
using RimWorld;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 随行殖民者阵亡后的人数兼容：挂钩 CompShuttle.AllRequiredThingsLoaded。
    ///
    /// 联动关系：
    ///   任务脚本 QuestPart_Filter_AllRequiredThingsLoaded、船务自动起飞、本模组起飞按钮
    ///     都会读 AllRequiredThingsLoaded。原版把 containedColonistCount 与写死的
    ///     requiredColonistCount 比较；途中死了人就永远凑不齐，穿梭机卡在任务地图上。
    ///
    /// 做法：本次读取期间把 requiredColonistCount 临时压到「尚存可用自由殖民者」人数，
    ///   读完立刻还原字段，不改存档里的任务配置。requireAllColonistsOnMap 分支本身会随
    ///   FreeColonistsCount 下降，不受影响。
    /// </summary>
    [HarmonyPatch(typeof(CompShuttle), "get_AllRequiredThingsLoaded")]
    internal static class Patch_AllRequiredThingsLoaded
    {
        [HarmonyPrefix]
        private static void Prefix(CompShuttle __instance, out int __state)
        {
            __state = -1;
            if (!CargoPolicy.IsPlayerExpeditionShuttle(__instance) || __instance.requiredColonistCount <= 0)
            {
                return;
            }
            int available = CargoPolicy.AvailableFreeColonists(__instance);
            if (available >= __instance.requiredColonistCount)
            {
                return;
            }
            __state = __instance.requiredColonistCount;
            __instance.requiredColonistCount = available;
        }

        [HarmonyPostfix]
        private static void Postfix(CompShuttle __instance, int __state)
        {
            if (__state >= 0)
            {
                __instance.requiredColonistCount = __state;
            }
        }
    }
}
