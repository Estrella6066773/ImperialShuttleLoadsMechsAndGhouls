using System.Reflection;
using HarmonyLib;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 模组入口：游戏启动完成静态构造时统一打补丁。
    ///
    /// 联动关系：本类只负责在启动阶段调用 Harmony.PatchAll，把本程序集里所有带
    /// [HarmonyPatch] 的类一次性挂到原版方法上；具体改了什么、为什么改，都写在各个补丁类自己的说明里。
    ///
    /// 注意：
    /// 1. 用 [StaticConstructorOnStartup] 而不是模组类（Mod 子类），是因为补丁必须在
    ///    任何游戏界面出现之前挂好，否则第一次打开装载界面时可能读不到补丁。
    /// 2. HarmonyId 里带 local. 前缀，表示这是本地模组；与同作者其它模组的 HarmonyId 不同，
    ///    卸载本模组不会连带撤销别的模组的补丁。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ModEntry
    {
        public const string HarmonyId = "local.imperialShuttleLoadsMechsAndGhouls";

        static ModEntry()
        {
            new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
        }
    }
}
