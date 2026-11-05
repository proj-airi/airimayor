using System.Collections.Generic;
using Colossal;

namespace airimayor
{
    public class LocaleZhHans : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleZhHans(Setting setting) { m_Setting = setting; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "AIRI Mayor" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "主要" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kConnectionGroup), "连接" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kAgentGroup), "权限" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Head)), "代理" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Head)), "内置使用服务端地址和 API 密钥。外部代理来自 ACP Registry，首次使用时准备。登录在游戏外完成；部分代理需要 Node.js/npm 或 uv。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FetchAgents)), "刷新代理" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.FetchAgents)), "刷新可用代理及版本，不改变当前选择，也不应用设置。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Endpoint)), "服务端地址" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Endpoint)), "Responses API 基础地址。应用设置后会以此连接开启新聊天。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApiKey)), "API 密钥" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApiKey)), "仅保存在设置中。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FetchModels)), "获取模型" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.FetchModels)), "从服务端地址加载模型列表；当前模型为空或不在列表中时选中第一个。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Model)), "模型" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Model)), "从服务端加载的模型中选择。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestConnection)), "测试连接" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TestConnection)), "按城里一轮对话的同样方式发一次最短回复。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestAcp)), "测试 ACP" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TestAcp)), "拉起当前代理，请它用一句话回答，然后结束。" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowConstruction)), "修建" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowConstruction)), "放置建筑、道路、分区和线路，以及更换路型。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowDemolition)), "拆除" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowDemolition)), "允许代理直接推平建筑和路段。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowTreasury)), "财政与政策" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowTreasury)), "税收、费用、服务预算、贷款、条例，以及购买地图区块。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowProgressionPurchases)), "发展点" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowProgressionPurchases)), "允许代理在发展树上花费已赚取的发展点。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowClock)), "推进时间" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowClock)), "允许代理推动城市时钟。读取时钟始终可用。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.VisionTools)), "视觉工具" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.VisionTools)), "截图、地图图像和镜头。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowSave)), "允许存档" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowSave)), "允许代理应你的要求保存城市。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowDiagnostics)), "诊断工具" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowDiagnostics)), "用于排查问题的分区格子诊断。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowPanel)), "界面操控" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowPanel)), "勾选后，代理可以按面板上的按钮、填文本框，包括城市设置、模组列表和删除存档。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestPanel)), "测试界面" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TestPanel)), "检查界面操控是否可用。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.WindowTokens)), "窗口 token 数" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.WindowTokens)), "所选模型的上下文窗口。应用设置后开启新聊天。" },

                { ChatLocale.Id("Title"), "AIRI Mayor" },
                { ChatLocale.Id("Composer.Loading"), "正在加载城市…" },
                { ChatLocale.Id("Composer.Ready"), "给 AIRI 留言…" },
                { ChatLocale.Id("Composer.Send"), "发送" },
                { ChatLocale.Id("Composer.Stop"), "停止" },
                { ChatLocale.Id("Composer.SelectPlace"), "选择地点或建筑" },
                { ChatLocale.Id("Composer.PickHint"), "在城市里点建筑、道路或地面来标记。" },
                { ChatLocale.Id("Place.Remove"), "移除" },
                { ChatLocale.Id("Place.Full"), "一次最多标 8 个地点。" },
                { ChatLocale.Id("Place.MissingBuilding"), "找不到这座建筑。" },
                { ChatLocale.Id("Place.MissingRoad"), "找不到这条路。" },
                { ChatLocale.Id("Place.MissingLine"), "找不到这条管线。" },
                { ChatLocale.Id("Place.Busy"), "城市正在建造，稍后再点一次。" },
                { ChatLocale.Id("Empty"), "让 AIRI 建造、分区或修复城市服务。" },
                { ChatLocale.Id("Thinking"), "思考中" },
                { ChatLocale.Id("Role.You"), "你" },
                { ChatLocale.Id("Role.Mayor"), "AIRI" },
                { ChatLocale.Id("Role.Error"), "错误" },
                { ChatLocale.Id("Tool.Running"), "运行中" },
                { ChatLocale.Id("Tool.Done"), "完成" },
                { ChatLocale.Id("Tool.Error"), "错误" },
                { ChatLocale.Id("Tool.Interrupted"), "已中断" },
                { ChatLocale.Id("Tool.Arguments"), "参数" },
                { ChatLocale.Id("Tool.Result"), "结果" },
                { ChatLocale.Id("Tool.NoResult"), "暂无结果。" },
                { ChatLocale.Id("Status.Idle"), "空闲" },
                { ChatLocale.Id("Status.Thinking"), "思考中" },
                { ChatLocale.Id("Status.Working"), "执行中" },
                { ChatLocale.Id("Status.Interrupted"), "已中断" },
                { ChatLocale.Id("Status.Error"), "错误" },
                { ChatLocale.Id("Status.Queued"), "条排队中" },
                { ChatLocale.Id("Plan.None"), "没有当前计划" },
            };
        }

        public void Unload() { }
    }
}
