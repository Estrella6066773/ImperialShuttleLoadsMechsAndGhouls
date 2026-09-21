using UnityEngine;
using Verse;

namespace ImperialShuttleLoadsMechsAndGhouls
{
    /// <summary>
    /// 本模组的设置窗口。
    ///
    /// 联动关系：
    ///   游戏启动 -> LoadedModManager.CreateModClasses 构造本类 -> GetSettings 读出存档外的设置文件
    ///   游戏里改设置 -> 本类.DoSettingsWindowContents -> Settings.DoWindowContents
    ///   关闭设置窗口 -> Dialog_ModSettings.PreClose -> Mod.WriteSettings 写回设置文件
    ///   补丁读取 -> CargoPolicy.CountsTowardShuttleLimit / LimitEnforced / MassLimitEnforced
    ///               每次现读，改完立即生效，不需要重开游戏
    ///
    /// 注意：
    /// 1. RimWorld 反射查找继承自 Mod、且带「一个 ModContentPack 参数」的构造函数的类型，
    ///    因此本类必须是 public，构造函数签名也必须保持这样。
    /// 2. Settings 是静态字段，供各处补丁读取；补丁会判空，万一取不到就退回原版口径。
    /// </summary>
    public class ImperialShuttleLoadsMod : Mod
    {
        public static ImperialShuttleLoadsSettings Settings;

        public ImperialShuttleLoadsMod(ModContentPack content)
            : base(content)
        {
            Settings = GetSettings<ImperialShuttleLoadsSettings>();
        }

        public override string SettingsCategory()
        {
            return Content.Name;
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings?.DoWindowContents(inRect);
        }
    }

    /// <summary>
    /// 存放在设置文件里的内容，分三组。
    ///
    /// 第一组：装载面板。
    ///   alwaysShowLoadingPanel 决定「任务交给玩家自己装载的穿梭机」要不要始终显示装载面板。
    ///   默认开启，原因是原版会把面板藏起来：面板与按钮都挂在当前船务上，
    ///   穿梭机停在地图上而船务队列已经空掉时（文化 + 王权的「古代遗迹」任务就是这样），
    ///   原版连装载面板都打不开，玩家只能用右键一个一个把单位叫进机舱。
    ///
    /// 第二组：起飞方式。
    ///   manualLaunchQuestShuttles 决定任务穿梭机是「装载齐了就自动飞」还是「等玩家点起飞」。
    ///   默认改为手动，原因是本模组放行了非殖民者登机：原版「任务要的殖民者都进舱」这个条件
    ///   完全不看机械族与食尸鬼有没有进来，于是自动起飞会在随行单位还在往机舱走的时候把船带走。
    ///
    /// 第三组：载重与人数限制，两套开关彼此独立。
    ///   enforceMassLimit：关闭后装载界面不再因为超载拒绝装载（载重条仍显示实际数字，但上限视为无限）。
    ///   enforcePassengerLimit：关闭后装载界面、右键进舱、起飞按钮都不再按人数拦人。
    ///   其余四项只回答：这类单位算不算进任务穿梭机的「人数上限」。
    ///   上限本身来自任务；本模组只改「谁来填这个数字」。
    ///   默认：机械族与亚人「不计入」，奴隶与囚犯「计入」。
    ///
    /// 注意：
    /// 1. 这一组只作用于「玩家殖民者远征」的任务穿梭机。
    /// 2. 人数总开关关闭后，连原版「只允许 N 名殖民者」那条检查也会一并放过；
    ///    任务点名的必需人员与物品仍须齐备，起飞按钮不会替剧情放行。
    /// 3. 返程（穿梭机停在非玩家家园地图上）时，囚犯自动不占名额，便于带回俘虏；
    ///    随行殖民者阵亡后，必需人数按尚存自由殖民者封顶，避免永远飞不走。
    /// 4. 这里不控制「能不能上机」。任务是否允许带奴隶、囚犯、机械族、亚人，
    ///    仍由任务本身与 CompShuttle.IsAllowed 决定。
    /// </summary>
    public class ImperialShuttleLoadsSettings : ModSettings
    {
        public bool alwaysShowLoadingPanel = true;
        public bool manualLaunchQuestShuttles = true;
        public bool enforceMassLimit = true;
        public bool enforcePassengerLimit = true;
        public bool mechsCountTowardLimit;
        public bool subhumansCountTowardLimit;
        public bool slavesCountTowardLimit = true;
        public bool prisonersCountTowardLimit = true;

        /// <summary>是否有任何一类单位被排除在上限之外。全为「计入」时等于原版，补丁可以完全不介入。</summary>
        public bool AnythingLeftOutOfLimit =>
            !mechsCountTowardLimit || !subhumansCountTowardLimit || !slavesCountTowardLimit || !prisonersCountTowardLimit;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref alwaysShowLoadingPanel, "alwaysShowLoadingPanel", defaultValue: true);
            Scribe_Values.Look(ref manualLaunchQuestShuttles, "manualLaunchQuestShuttles", defaultValue: true);
            Scribe_Values.Look(ref enforceMassLimit, "enforceMassLimit", defaultValue: true);
            Scribe_Values.Look(ref enforcePassengerLimit, "enforcePassengerLimit", defaultValue: true);
            Scribe_Values.Look(ref mechsCountTowardLimit, "mechsCountTowardLimit", defaultValue: false);
            Scribe_Values.Look(ref subhumansCountTowardLimit, "subhumansCountTowardLimit", defaultValue: false);
            Scribe_Values.Look(ref slavesCountTowardLimit, "slavesCountTowardLimit", defaultValue: true);
            Scribe_Values.Look(ref prisonersCountTowardLimit, "prisonersCountTowardLimit", defaultValue: true);
        }

        public void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            Text.Font = GameFont.Medium;
            listing.Label("ShuttleCargoLaunchSection".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(6f);
            listing.Label("ShuttleCargoLaunchDesc".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("ShuttleCargoLoadingPanel".Translate(), ref alwaysShowLoadingPanel, "ShuttleCargoLoadingPanelTip".Translate());
            listing.CheckboxLabeled("ShuttleCargoManualLaunch".Translate(), ref manualLaunchQuestShuttles, "ShuttleCargoManualLaunchTip".Translate());
            listing.Gap(18f);
            Text.Font = GameFont.Medium;
            listing.Label("ShuttleCargoLimitSection".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(6f);
            listing.Label("ShuttleCargoLimitDesc".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("ShuttleCargoMassLimitEnabled".Translate(), ref enforceMassLimit, "ShuttleCargoMassLimitEnabledTip".Translate());
            listing.CheckboxLabeled("ShuttleCargoLimitEnabled".Translate(), ref enforcePassengerLimit, "ShuttleCargoLimitEnabledTip".Translate());
            if (enforcePassengerLimit)
            {
                listing.CheckboxLabeled("ShuttleCargoLimitMechs".Translate(), ref mechsCountTowardLimit, "ShuttleCargoLimitMechsTip".Translate());
                listing.CheckboxLabeled("ShuttleCargoLimitSubhumans".Translate(), ref subhumansCountTowardLimit, "ShuttleCargoLimitSubhumansTip".Translate());
                listing.CheckboxLabeled("ShuttleCargoLimitSlaves".Translate(), ref slavesCountTowardLimit, "ShuttleCargoLimitSlavesTip".Translate());
                listing.CheckboxLabeled("ShuttleCargoLimitPrisoners".Translate(), ref prisonersCountTowardLimit, "ShuttleCargoLimitPrisonersTip".Translate());
            }
            else
            {
                // 总开关关闭时把四行收起来，免得玩家以为勾了没反应；四项目前的取值仍留在设置文件里，
                // 重新勾上总开关就照原样继续生效。
                listing.Label("ShuttleCargoLimitOffNote".Translate());
            }
            listing.GapLine();
            listing.Label("ShuttleCargoLimitNote".Translate());
            listing.Gap(12f);
            if (listing.ButtonText("ShuttleCargoReset".Translate()))
            {
                RestoreDefaults();
            }
            listing.End();
        }

        private void RestoreDefaults()
        {
            alwaysShowLoadingPanel = true;
            manualLaunchQuestShuttles = true;
            enforceMassLimit = true;
            enforcePassengerLimit = true;
            mechsCountTowardLimit = false;
            subhumansCountTowardLimit = false;
            slavesCountTowardLimit = true;
            prisonersCountTowardLimit = true;
        }
    }
}
