using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentRuntime;
using airimayor.Host;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI.Widgets;

namespace airimayor
{
    public enum ApiKind
    {
        ChatCompletions,
        Responses,
    }

    [FileLocation(nameof(airimayor))]
    [SettingsUIGroupOrder(kConnectionGroup, kAgentGroup, kBehaviorGroup)]
    [SettingsUIShowGroupName(kConnectionGroup, kAgentGroup, kBehaviorGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kConnectionGroup = "Connection";
        public const string kAgentGroup = "Agent";
        public const string kBehaviorGroup = "Behavior";

        public static Setting Instance { get; set; }

        public Setting(IMod mod) : base(mod) { }

        // ---- Connection -----------------------------------

        [SettingsUISection(kSection, kConnectionGroup)]
        [SettingsUIDropdown(typeof(Setting), nameof(GetHeadItems))]
        public string Head { get; set; } = AgentChoices.BuiltIn;

        [SettingsUISection(kSection, kConnectionGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideBuiltinRequest))]
        [SettingsUITextInput]
        public string Endpoint { get; set; } = "https://api.openai.com/v1";

        [SettingsUISection(kSection, kConnectionGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideBuiltinRequest))]
        [SettingsUITextInput]
        public string ApiKey { get; set; } = "";

        [SettingsUISection(kSection, kConnectionGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideBuiltinRequest))]
        [SettingsUIButton]
        public bool FetchModels
        {
            set { if (value) { StartFetchModels(); } }
        }

        [SettingsUISection(kSection, kConnectionGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideBuiltinRequest))]
        [SettingsUIDropdown(typeof(Setting), nameof(GetModelPresetItems))]
        [SettingsUIValueVersion(typeof(Setting), nameof(ModelPresetVersion))]
        public string Model { get; set; } = "";

        [SettingsUISection(kSection, kConnectionGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideBuiltinRequest))]
        public ApiKind Api { get; set; } = ApiKind.ChatCompletions;

        [SettingsUISection(kSection, kConnectionGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideBuiltinRequest))]
        [SettingsUISlider(min = 128_000, max = 1_000_000, step = 1_000)]
        public int WindowTokens { get; set; } = 128_000;

        [SettingsUISection(kSection, kConnectionGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideBuiltinRequest))]
        [SettingsUIButton]
        public bool TestConnection
        {
            set { if (value) { StartHeadTest(); } }
        }

        [SettingsUISection(kSection, kConnectionGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideAcpRequest))]
        [SettingsUIButton]
        public bool TestAcp
        {
            set { if (value) { StartHeadTest(); } }
        }

        public bool HideBuiltinRequest() => !AgentChoices.Find(Head).IsBuiltIn;

        public bool HideAcpRequest() => AgentChoices.Find(Head).IsBuiltIn;

        // ---- Behavior ------------------------------------

        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool AutoStart { get; set; } = false;

        [SettingsUISection(kSection, kBehaviorGroup)]
        public bool Continuous { get; set; } = false;

        // ---- Agent ---------------------------------------

        [SettingsUISection(kSection, kAgentGroup)]
        public bool AllowConstruction { get; set; } = true;

        [SettingsUISection(kSection, kAgentGroup)]
        public bool AllowDemolition { get; set; } = true;

        [SettingsUISection(kSection, kAgentGroup)]
        public bool AllowTreasury { get; set; } = true;

        [SettingsUISection(kSection, kAgentGroup)]
        public bool AllowProgressionPurchases { get; set; } = true;

        [SettingsUISection(kSection, kAgentGroup)]
        public bool AllowClock { get; set; } = true;

        [SettingsUISection(kSection, kAgentGroup)]
        public bool VisionTools { get; set; } = true;

        [SettingsUISection(kSection, kAgentGroup)]
        public bool AllowSave { get; set; } = false;

        [SettingsUISection(kSection, kAgentGroup)]
        public bool AllowDiagnostics { get; set; } = false;

        [SettingsUISection(kSection, kAgentGroup)]
        public bool AllowPanel { get; set; } = false;

        [SettingsUISection(kSection, kAgentGroup)]
        [SettingsUIButton]
        public bool TestPanel
        {
            set { if (value) { StartPanelTest(); } }
        }

        // ---- Static facade ---------------------------------

        public static string StaticEndpoint => Instance?.Endpoint ?? "https://api.openai.com/v1";
        public static string StaticModel => Instance?.Model ?? "";

        private const string StartupPrompt = "Run the city continuously: fix growth-blocking problems first, then expand with demand; act, don't just report.";

        public static string StaticStartupPrompt => StartupPrompt;

        public static bool StaticAutoStart => Instance?.AutoStart ?? false;
        public static bool StaticContinuous => Instance?.Continuous ?? false;
        public static bool StaticAllowConstruction => Instance?.AllowConstruction ?? true;
        public static bool StaticAllowProgressionPurchases =>
            Instance?.AllowProgressionPurchases ?? true;
        public static bool StaticAllowDemolition => Instance?.AllowDemolition ?? true;
        public static bool StaticAllowTreasury => Instance?.AllowTreasury ?? true;
        public static bool StaticAllowClock => Instance?.AllowClock ?? true;
        public static bool StaticAllowSave => Instance?.AllowSave ?? false;
        public static bool StaticAllowDiagnostics => Instance?.AllowDiagnostics ?? false;
        public static bool StaticAllowPanel => Instance?.AllowPanel ?? false;
        public static bool StaticVisionTools => Instance?.VisionTools ?? true;
        public static ApiKind StaticApiKind =>
            Instance?.Api ?? ApiKind.ChatCompletions;
        public static string StaticApiKey => Instance?.ApiKey ?? "";
        public static long StaticWindowTokens => Instance?.WindowTokens ?? 128_000;
        public static string StaticHead => Instance?.Head ?? AgentChoices.BuiltIn;

        public static event Action HeadApplied;

        public override void Apply()
        {
            base.Apply();
            HeadApplied?.Invoke();
        }

        // ---- Model list (dynamic dropdown source) --------

        private static readonly object s_ModelPresetLock = new object();
        private static List<string> s_ModelPresets = new List<string>();

        public int ModelPresetVersion { get; set; }

        public static DropdownItem<string>[] GetHeadItems()
        {
            string locale = Game.SceneFlow.GameManager.instance?.localizationManager?.activeLocaleId;
            bool chinese = !string.IsNullOrEmpty(locale) && locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            var items = new DropdownItem<string>[AgentChoices.All.Length];
            for (int i = 0; i < AgentChoices.All.Length; i++)
            {
                AgentChoice choice = AgentChoices.All[i];
                items[i] = new DropdownItem<string>
                {
                    value = choice.Id,
                    displayName = chinese ? choice.Chinese : choice.English,
                };
            }
            return items;
        }

        public static DropdownItem<string>[] GetModelPresetItems()
        {
            lock (s_ModelPresetLock)
            {
                IEnumerable<string> items = s_ModelPresets;
                string current = Instance?.Model;
                if (!items.Any() && !string.IsNullOrEmpty(current))
                {
                    items = new[] { current };
                }
                else if (!string.IsNullOrEmpty(current) && !items.Contains(current))
                {
                    items = new[] { current }.Concat(items);
                }
                return items
                    .Select(id => new DropdownItem<string> { value = id, displayName = id })
                    .ToArray();
            }
        }

        private void StartFetchModels()
        {
            string endpoint = Endpoint;
            string apiKey = ApiKey;
            Task.Run(async () =>
            {
                ModelCatalog.FetchResult result = await ModelCatalog.FetchAsync(endpoint, apiKey);
                if (result.Models.Count == 0)
                {
                    AgentTimeline.Warn("model", "fetch-models: " + result.Error);
                    return;
                }
                lock (s_ModelPresetLock)
                {
                    s_ModelPresets = result.Models;
                }
                Setting instance = Instance;
                if (instance != null)
                {
                    if (string.IsNullOrEmpty(instance.Model) || !result.Models.Contains(instance.Model))
                    {
                        instance.Model = result.Models[0];
                    }
                    instance.ModelPresetVersion++;
                    instance.ApplyAndSave();
                }
                AgentTimeline.Info("model", $"fetch-models: loaded {result.Models.Count} models.");
            });
        }

        public override void SetDefaults()
        {
            Head = AgentChoices.BuiltIn;
            Endpoint = "https://api.openai.com/v1";
            ApiKey = "";
            Model = "";
            AutoStart = false;
            Continuous = false;
            AllowConstruction = true;
            AllowProgressionPurchases = true;
            AllowDemolition = true;
            AllowTreasury = true;
            AllowClock = true;
            AllowSave = false;
            AllowDiagnostics = false;
            AllowPanel = false;
            VisionTools = true;
            Api = ApiKind.ChatCompletions;
            WindowTokens = 128_000;
        }

        private void StartHeadTest()
        {
            SynchronizationContext context = SynchronizationContext.Current;
            _ = Task.Run(async () =>
            {
                string message;
                try
                {
                    using (IAgentSession session = AgentHead.Open(null))
                    {
                        SessionTurnResult turn = await SessionTurn.RunAsync(
                            session,
                            "Reply in one sentence.",
                            null,
                            CancellationToken.None);
                        message = Notice(turn);
                    }
                }
                catch (Exception e)
                {
                    message = e.Message ?? "";
                }
                Report(context, message);
            });
        }

        private static string Notice(SessionTurnResult turn)
        {
            if (turn.Failed)
            {
                return turn.Error ?? "";
            }
            string reply = (turn.Reply ?? "").Trim();
            if (reply.Length == 0)
            {
                return "No reply.";
            }
            return "Connected. " + (reply.Length <= 500 ? reply : reply.Substring(0, 500));
        }

        private void StartPanelTest()
        {
            SynchronizationContext context = SynchronizationContext.Current;
            _ = Task.Run(() => Report(context, PanelCommand.ProbePort()));
        }

        private static void Report(SynchronizationContext context, string message)
        {
            if (context == null)
            {
                SettingsNotice.Show(message);
                return;
            }
            context.Post(_ => SettingsNotice.Show(message), null);
        }
    }

    public static class ChatLocale
    {
        public static string Id(string name) => $"airimayor.Chat.{name}";
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting) { m_Setting = setting; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "AIRI Mayor" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kConnectionGroup), "Connection" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kAgentGroup), "Permissions" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kBehaviorGroup), "Behavior" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Head)), "Agent" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Head)), "Built-in uses the endpoint and API key. OpenCode launches opencode. Codex launches the installed codex-acp adapter. Sign in outside the game." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Endpoint)), "Endpoint" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Endpoint)), "OpenAI-compatible API base URL." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApiKey)), "API key" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApiKey)), "Stored in settings only." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FetchModels)), "Fetch models" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.FetchModels)), "Load the model list from the endpoint; selects the first model when the current one is empty or missing." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Model)), "Model" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Model)), "Pick a model loaded from the endpoint." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestConnection)), "Test connection" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TestConnection)), "Sends one short reply the same way a turn in the city does." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestAcp)), "Test ACP" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TestAcp)), "Starts the selected agent, asks for one sentence, and stops it." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AutoStart)), "Auto-start" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AutoStart)), "Start a turn on city load." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Continuous)), "Continue" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Continuous)), "Keep the agent running." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowConstruction)), "Construction" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowConstruction)), "Place buildings, roads, zones, and transit lines, and replace a road's type." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowDemolition)), "Demolition" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowDemolition)), "Let the agent bulldoze buildings and road segments directly." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowTreasury)), "Treasury and policy" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowTreasury)), "Taxes, fees, service budgets, loans, ordinances, and buying map tiles." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowProgressionPurchases)), "Development points" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowProgressionPurchases)), "Let the agent spend earned Development Points on the Development Tree." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowClock)), "Advance time" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowClock)), "Let the agent run the city clock. Reading the clock stays available." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.VisionTools)), "Visual tools" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.VisionTools)), "Screenshots, the map image, and the camera." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowSave)), "Allow saving" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowSave)), "Let the agent save the city at your request." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowDiagnostics)), "Diagnostic tools" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowDiagnostics)), "Zone-block diagnostics for troubleshooting." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AllowPanel)), "Panel control" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AllowPanel)), "When on, the agent can press panel buttons and fill text boxes, including city settings, the mod list, and deleting a save." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestPanel)), "Test panels" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TestPanel)), "Check that panel control works." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Api)), "API kind" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Api)), "Chat Completions or Responses: the loop's request shape." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.WindowTokens)), "Window tokens" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.WindowTokens)), "How many tokens the loop treats as the window." },

                { m_Setting.GetEnumValueLocaleID(ApiKind.ChatCompletions), "Chat Completions" },
                { m_Setting.GetEnumValueLocaleID(ApiKind.Responses), "Responses" },

                { ChatLocale.Id("Title"), "AIRI Mayor" },
                { ChatLocale.Id("Composer.Loading"), "Loading city…" },
                { ChatLocale.Id("Composer.Ready"), "Message AIRI…" },
                { ChatLocale.Id("Composer.Send"), "Send" },
                { ChatLocale.Id("Composer.Stop"), "Stop" },
                { ChatLocale.Id("Composer.SelectPlace"), "Select place or building" },
                { ChatLocale.Id("Composer.PickHint"), "Click a building, road, or the ground in the city." },
                { ChatLocale.Id("Place.Remove"), "Remove" },
                { ChatLocale.Id("Place.Full"), "You can point at 8 places at a time." },
                { ChatLocale.Id("Place.MissingBuilding"), "That building is gone." },
                { ChatLocale.Id("Place.MissingRoad"), "That road is gone." },
                { ChatLocale.Id("Place.MissingLine"), "That line is gone." },
                { ChatLocale.Id("Place.Busy"), "The city is busy building; point again in a moment." },
                { ChatLocale.Id("Empty"), "Ask AIRI to build, zone, or fix city services." },
                { ChatLocale.Id("Thinking"), "Thinking" },
                { ChatLocale.Id("Role.You"), "You" },
                { ChatLocale.Id("Role.Mayor"), "AIRI" },
                { ChatLocale.Id("Role.Error"), "Error" },
                { ChatLocale.Id("Tool.Running"), "Running" },
                { ChatLocale.Id("Tool.Done"), "Done" },
                { ChatLocale.Id("Tool.Error"), "Error" },
                { ChatLocale.Id("Tool.Interrupted"), "Interrupted" },
                { ChatLocale.Id("Tool.Arguments"), "Arguments" },
                { ChatLocale.Id("Tool.Result"), "Result" },
                { ChatLocale.Id("Tool.NoResult"), "No result yet." },
                { ChatLocale.Id("Status.Idle"), "Idle" },
                { ChatLocale.Id("Status.Thinking"), "Thinking" },
                { ChatLocale.Id("Status.Working"), "Working" },
                { ChatLocale.Id("Status.Interrupted"), "Interrupted" },
                { ChatLocale.Id("Status.Error"), "Error" },
                { ChatLocale.Id("Status.Queued"), "queued" },
                { ChatLocale.Id("Status.Vision"), "vision" },
                { ChatLocale.Id("Plan.None"), "No active plan" },
            };
        }

        public void Unload() { }
    }
}
