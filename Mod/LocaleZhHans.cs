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
                { m_Setting.GetOptionTabLocaleID(Setting.kConnectionSection), "市长" },
                { m_Setting.GetOptionTabLocaleID(Setting.kPermissionsSection), "城市权限" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kMayorGroup), "市长来源" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kBuiltinGroup), "内建市长" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kExternalGroup), "外部代理" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kAgentGroup), "权限" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MayorSource)), "市长来源" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.MayorSource)), "选择内建市长或外部代理。应用设置后开启新聊天。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Provider)), "供应商" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Provider)), "从 Models.dev 目录中选择受支持的供应商。密钥、模型选择和连接设置分别保存在本地，应用后以此连接开启聊天。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ExternalAgent)), "代理" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ExternalAgent)), "从 ACP Registry 选择。模型和登录由代理在游戏外管理。首次使用时准备对应版本；部分代理需要 Node.js/npm 或 uv。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FetchAgents)), "刷新代理" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.FetchAgents)), "刷新可用代理及版本，不改变当前选择，也不应用设置。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ProviderEndpoint)), "覆盖服务端地址" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ProviderEndpoint)), "留空时使用目录中所选模型的地址。自定义服务需填写基础地址，通常包含 /v1。更换主机会清空此连接的密钥。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ModelsEndpoint)), "可用模型列表地址" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ModelsEndpoint)), "用于“检查服务端模型”。留空时在所选服务端地址后加 /models；Go 使用其独立的列表地址。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CheckModels)), "检查服务端模型" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CheckModels)), "向服务端查询可用模型。目录供应商需先选模型，自定义服务需先填地址。只更新此连接的列表，不改变选择或应用设置。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ProviderApiKey)), "API 密钥" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ProviderApiKey)), "仅保存在此供应商的设置中。自定义服务不需要认证时可留空。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FetchModels)), "刷新模型目录" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.FetchModels)), "从 Models.dev 刷新供应商和模型，不发送 API 密钥。保留当前选择和聊天，刷新失败时继续使用缓存目录。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ProviderModel)), "模型" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ProviderModel)), "从此连接的列表选择模型，也可在高级连接设置中手填模型 ID。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ConnectionDetails)), "高级连接设置" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ConnectionDetails)), "手填模型 ID、更改地址和协议，并选择上下文预算。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ModelId)), "模型 ID" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ModelId)), "直接填写供应商的模型 ID，刷新列表时保留此选择。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Protocol)), "API 协议" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Protocol)), "自动时使用所选模型的目录信息。自定义服务或手动配置的模型需明确选择协议。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestConnection)), "测试连接" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TestConnection)), "使用正在编辑的连接和模型测试一句话回复，不应用设置，也不重开城里的聊天。" },
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
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AutomaticContext)), "使用模型上下文窗口" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AutomaticContext)), "使用目录中所选模型的上下文窗口，自定义服务默认 128,000 token。关闭后可自行选择较小预算。" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ContextTokens)), "上下文预算" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ContextTokens)), "在模型窗口范围内选择上下文预算，每个供应商会记住你的选择。" },

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
