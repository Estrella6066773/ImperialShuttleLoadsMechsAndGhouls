using HarmonyLib;
using RimWorld;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 放行机械族与亚人登机：挂钩 CompShuttle.IsAllowed。
    ///
    /// 联动关系（一个单位从「看得见」到「进得去」要过的三道门，本补丁是其中两道）：
    ///   1. 界面列出候选单位：TransporterUtility.AllSendablePawns -> 会问 IsAllowed，
    ///      放行后玩家在装载界面才勾得动它们（可见性另见 LoadDialogSections）；
    ///   2. 右键「进入穿梭机」：CompShuttle.CompFloatMenuOptions -> 问 IsAllowedNow -> 问 IsAllowed，
    ///      放行后才有可点的菜单项，而不是灰色的「不允许」；
    ///   3. 走过去的路上：JobDriver_EnterTransporter 每帧用 FailOn(IsAllowed) 复查，
    ///      放行必须自始至终成立，否则单位走到一半会被打断。
    ///
    /// 原版为什么拒绝：任务脚本生成穿梭机时把 onlyAcceptColonists 设成了 true，
    /// 而 IsColonySubhuman 与 IsColonyMech 都不满足原版那条「殖民者」判定（IsColonist 明确排除了亚人），
    /// 于是这些单位既不出现在界面里，右键也会被拒。
    ///
    /// 注意：
    /// 1. 只在原版返回 false 时才改写，属于「只松不紧」的改动：
    ///    任务要求的人员（IsRequired）、许可穿梭机、玩家自有穿梭机、原版本来放行的殖民者都不受影响。
    /// 2. 放行前要求单位走得动（CanBoardOnFoot），倒地、发狂、无法移动的一律不放行——
    ///    这也保证了「起飞前等装载完成」不会被一个永远走不到的单位卡死。
    /// 3. 不区分属性「只收健康单位」：被放行的单位已经过了同一套健康判定。
    /// </summary>
    [HarmonyPatch(typeof(CompShuttle), nameof(CompShuttle.IsAllowed))]
    internal static class Patch_CompShuttle_IsAllowed
    {
        [HarmonyPostfix]
        private static void Postfix(CompShuttle __instance, Thing t, ref bool __result)
        {
            if (__result)
            {
                return;
            }
            if (!CargoPolicy.IsQuestShuttle(__instance))
            {
                return;
            }
            if (!CargoPolicy.IsOurCargo(t))
            {
                return;
            }
            if (!CargoPolicy.CanBoardOnFoot((Pawn)t))
            {
                return;
            }
            __result = true;
        }
    }
}
