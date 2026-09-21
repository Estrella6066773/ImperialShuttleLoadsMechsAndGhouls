using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 补出被原版藏掉的起飞按钮：挂钩 CompShuttle.CompGetGizmosExtra。
    ///
    /// 联动关系：
    ///   玩家选中穿梭机 -> CompShuttle.CompGetGizmosExtra
    ///     -> 原版在这里画的「起飞」按钮来自当前船务：shipParent.curJob.GetJobGizmos()。
    ///        船务队列空掉（curJob 为 null）时这一段整段跳过，穿梭机上什么按钮都没有，
    ///        任务要的货装齐了也只能干等任务脚本自己安排起飞。
    ///   本补丁在同一个方法后面追加一颗自己的按钮，按钮内容由 QuestShuttleControls 判定。
    ///
    /// 与「等待类船务那颗起飞按钮」的分工（两边不会同时出现）：
    ///   curJob 是等待类船务时，按钮由 Patch_ManualLaunch 挂在 ShipJob_Wait.GetJobGizmos 上，
    ///   因为只有那条船务知道目的地与等待规则（去任务地点、回玩家据点两种走法不同）；
    ///   本补丁只在 curJob 为 null 时才补按钮，按下后给任务发一条 ThingAdded 信号，
    ///   请任务按自己的流程重新判断——古代遗迹这类任务的目的地正是由任务脚本算出来的，
    ///   本模组猜不出来，也不该去猜。
    ///
    /// 注意：
    /// 1. 只在原版什么按钮都没有的时候补，绝不顶掉原版或别的模组已经给出的按钮。
    /// 2. 按钮可不可按沿用本模组那套「现在能不能飞」的说明文字（ManualLaunch.ApplyReadinessNote），
    ///    与等待类船务那颗按钮口径完全一致：任务要的殖民者不够、任务要求的人或物没进舱时都是灰的。
    /// 3. 设置项「任务穿梭机改为手动起飞」关闭时本补丁完全不介入。
    /// </summary>
    [HarmonyPatch(typeof(CompShuttle), nameof(CompShuttle.CompGetGizmosExtra))]
    internal static class Patch_CompShuttle_CompGetGizmosExtra
    {
        [HarmonyPostfix]
        private static void Postfix(CompShuttle __instance, ref IEnumerable<Gizmo> __result)
        {
            Command_Action command = QuestShuttleControls.BuildLaunchCommand(__instance);
            if (command == null)
            {
                return;
            }
            __result = Append(__result, command);
        }

        /// <summary>
        /// 追加而不是替换：原版这一段还会给出「自动装载」开关与任务相关按钮，替换会把它们弄丢。
        /// 原方法是个迭代器，包装后的集合在被遍历时才真正执行原版那段代码，顺序与原版一致。
        /// </summary>
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
    }
}
