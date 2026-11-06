using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using airimayor.Agent;
using airimayor.Host;
using Colossal;
using Colossal.IO.AssetDatabase;
using Colossal.Json;
using Game.Modding;
using Game.Settings;
using Game.UI.Widgets;

namespace airimayor
{
    [FileLocation(nameof(airimayor))]
    [SettingsUITabOrder(kConnectionSection, kPermissionsSection)]
    [SettingsUIGroupOrder(kMayorGroup, kBuiltinGroup, kExternalGroup, kAgentGroup)]
    [SettingsUIShowGroupName(kMayorGroup, kBuiltinGroup, kExternalGroup, kAgentGroup)]
    public class Setting : ModSetting
    {
        public const string kConnectionSection = "Connection";
        public const string kPermissionsSection = "Permissions";
        public const string kMayorGroup = "Mayor";
        public const string kBuiltinGroup = "Builtin";
        public const string kExternalGroup = "External";
        public const string kAgentGroup = "Agent";

        public static Setting Instance { get; set; }

        public Setting(IMod mod) : base(mod) { }

        private ProviderConnections m_Connections = new ProviderConnections();
        private string m_Provider = "";
        private MayorSelection m_Applied;

        [SettingsUIHidden]
        public string ProviderConnectionsJson
        {
            get => m_Connections.ToJson();
            set { m_Connections = ProviderConnections.FromJson(value); }
        }

        [SettingsUISection(kConnectionSection, kMayorGroup)]
        [SettingsUIDropdown(typeof(Setting), nameof(GetMayorItems))]
        public string MayorSource { get; set; } = AgentHead.BuiltIn;

        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideBuiltinRequest))]
        [SettingsUIDropdown(typeof(Setting), nameof(GetProviderItems))]
        [SettingsUIValueVersion(typeof(Setting), nameof(ModelPresetVersion))]
        public string Provider
        {
            get => m_Provider;
            set
            {
                m_Provider = value ?? "";
                ModelPresetVersion++;
            }
        }

        [Exclude]
        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideConnectionRequest))]
        [SettingsUITextInput]
        public string ProviderApiKey
        {
            get => Connection.ApiKey;
            set { Edit(Connection.With(apiKey: value.Trim(), credentialOrigin: "")); }
        }

        [Exclude]
        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideConnectionRequest))]
        [SettingsUIDropdown(typeof(Setting), nameof(GetModelPresetItems))]
        [SettingsUIValueVersion(typeof(Setting), nameof(ModelPresetVersion))]
        public string ProviderModel
        {
            get => Connection.Model;
            set { Edit(Connection.With(model: value)); }
        }

        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideBuiltinRequest))]
        [SettingsUIButton]
        public bool FetchModels
        {
            set { if (value) StartFetchModels(); }
        }

        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideConnectionRequest))]
        [SettingsUIButton]
        public bool TestConnection
        {
            set { if (value) StartHeadTest(); }
        }

        [Exclude]
        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideConnectionRequest))]
        public bool ConnectionDetails { get; set; }

        [Exclude]
        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideConnectionDetails))]
        [SettingsUITextInput]
        public string ModelId
        {
            get => Connection.Model;
            set { Edit(Connection.With(model: value.Trim())); }
        }

        [Exclude]
        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideConnectionDetails))]
        [SettingsUITextInput]
        public string ProviderEndpoint
        {
            get => Connection.Endpoint;
            set { Edit(Connection.With(endpoint: value.Trim())); }
        }

        [Exclude]
        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideConnectionDetails))]
        [SettingsUITextInput]
        public string ModelsEndpoint
        {
            get => Connection.ModelsEndpoint;
            set { Edit(Connection.With(modelsEndpoint: value.Trim())); }
        }

        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideConnectionDetails))]
        [SettingsUIButton]
        public bool CheckModels
        {
            set { if (value) StartCheckModels(); }
        }

        [Exclude]
        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideConnectionDetails))]
        [SettingsUIDropdown(typeof(Setting), nameof(GetProtocolItems))]
        [SettingsUIValueVersion(typeof(Setting), nameof(ModelPresetVersion))]
        public string Protocol
        {
            get => Connection.Protocol.ToString();
            set { Edit(Connection.With(protocol: (ModelProtocol)Enum.Parse(typeof(ModelProtocol), value))); }
        }

        [Exclude]
        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideConnectionDetails))]
        public bool AutomaticContext
        {
            get => Connection.WindowTokens == 0;
            set { Edit(Connection.With(windowTokens: value ? 0 : Math.Min(2_000_000, ModelWindow))); }
        }

        [Exclude]
        [SettingsUISection(kConnectionSection, kBuiltinGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideContextBudget))]
        [SettingsUISlider(min = 32_000, max = 2_000_000, step = 1_000)]
        public int ContextTokens
        {
            get => Connection.WindowTokens == 0 ? ModelWindow : Connection.WindowTokens;
            set { Edit(Connection.With(windowTokens: value)); }
        }

        [SettingsUISection(kConnectionSection, kExternalGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideAcpRequest))]
        [SettingsUIDropdown(typeof(Setting), nameof(GetExternalAgentItems))]
        [SettingsUIValueVersion(typeof(Setting), nameof(AgentListVersion))]
        public string ExternalAgent { get; set; } = "";

        [SettingsUISection(kConnectionSection, kExternalGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideAcpRequest))]
        [SettingsUIButton]
        public bool FetchAgents
        {
            set { if (value) StartFetchAgents(true); }
        }

        [SettingsUISection(kConnectionSection, kExternalGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideAcpRequest))]
        [SettingsUIButton]
        public bool TestAcp
        {
            set { if (value) StartHeadTest(); }
        }

        public bool HideBuiltinRequest() => MayorSource != AgentHead.BuiltIn;
        public bool HideAcpRequest() => MayorSource != AgentHead.External;
        public bool HideConnectionRequest() => HideBuiltinRequest() || string.IsNullOrEmpty(Provider);
        public bool HideConnectionDetails() => HideConnectionRequest() || !ConnectionDetails;
        public bool HideContextBudget() => HideConnectionDetails() || AutomaticContext;
        private ProviderConnection Connection => m_Connections.Read(Provider);
        private int ModelWindow => AgentHead.Catalog.Current.Find(Provider)?.Models.FirstOrDefault(model => model.Id == Connection.Model)?.ContextTokens ?? 128_000;

        private void Edit(ProviderConnection connection)
        {
            if (!string.IsNullOrEmpty(connection.ApiKey) && string.IsNullOrEmpty(connection.CredentialOrigin))
            {
                try
                {
                    ResolvedModelConnection resolved = AgentHead.Catalog.Current.Resolve(connection);
                    connection = connection.With(credentialOrigin: new Uri(resolved.Route.Endpoint).GetLeftPart(UriPartial.Authority));
                }
                catch (InvalidOperationException) { } // A draft can have its key entered before its model or address.
            }
            m_Connections.Set(connection);
            ModelPresetVersion++;
        }

        internal MayorSelection CaptureMayor() => new MayorSelection(MayorSource, ExternalAgent, Connection, AgentHead.Catalog.Current);
        internal void CaptureAppliedMayor() => m_Applied = CaptureMayor();
        internal static MayorSelection AppliedMayor => Instance?.m_Applied ??
            new MayorSelection(AgentHead.BuiltIn, "", new ProviderConnections().Read(""), ModelCatalogSnapshot.Empty);

        // ---- Agent ---------------------------------------

        [SettingsUISection(kPermissionsSection, kAgentGroup)]
        public bool AllowConstruction { get; set; } = true;

        [SettingsUISection(kPermissionsSection, kAgentGroup)]
        public bool AllowDemolition { get; set; } = true;

        [SettingsUISection(kPermissionsSection, kAgentGroup)]
        public bool AllowTreasury { get; set; } = true;

        [SettingsUISection(kPermissionsSection, kAgentGroup)]
        public bool AllowProgressionPurchases { get; set; } = true;

        [SettingsUISection(kPermissionsSection, kAgentGroup)]
        public bool AllowClock { get; set; } = true;

        [SettingsUISection(kPermissionsSection, kAgentGroup)]
        public bool VisionTools { get; set; } = true;

        [SettingsUISection(kPermissionsSection, kAgentGroup)]
        public bool AllowSave { get; set; } = false;

        [SettingsUISection(kPermissionsSection, kAgentGroup)]
        public bool AllowDiagnostics { get; set; } = false;

        [SettingsUISection(kPermissionsSection, kAgentGroup)]
        public bool AllowPanel { get; set; } = false;

        [SettingsUISection(kPermissionsSection, kAgentGroup)]
        [SettingsUIButton]
        public bool TestPanel
        {
            set { if (value) { StartPanelTest(); } }
        }

        // ---- Static facade ---------------------------------

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

        public static event Action HeadApplied;

        public override void Apply()
        {
            CaptureAppliedMayor();
            base.Apply();
            HeadApplied?.Invoke();
        }

        // ---- Model list (dynamic dropdown source) --------

        [Exclude, SettingsUIHidden]
        public int ModelPresetVersion { get; set; }
        [Exclude, SettingsUIHidden]
        public int AgentListVersion { get; set; }
        private int m_FetchingAgents;
        private int m_FetchingProviders;

        private static bool Chinese => Game.SceneFlow.GameManager.instance?.localizationManager?.activeLocaleId
            ?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;

        public static DropdownItem<string>[] GetMayorItems() => new[]
        {
            new DropdownItem<string> { value = AgentHead.BuiltIn, displayName = Chinese ? "内建市长" : "Built-in mayor" },
            new DropdownItem<string> { value = AgentHead.External, displayName = Chinese ? "外部代理" : "External agent" },
        };

        public static DropdownItem<string>[] GetProviderItems()
        {
            var items = new List<DropdownItem<string>>
            {
                new DropdownItem<string> { value = "", displayName = Chinese ? "选择供应商" : "Select a provider" },
            };
            items.AddRange(AgentHead.Catalog.Current.Providers.Select(provider => new DropdownItem<string>
            {
                value = provider.Id,
                displayName = provider.Name,
            }));
            items.Add(new DropdownItem<string> { value = ModelProviders.Compatible,
                displayName = Chinese ? "自定义 OpenAI 兼容服务" : "Custom OpenAI-compatible service" });
            string current = Instance?.Provider;
            if (!string.IsNullOrEmpty(current) && !items.Any(item => item.value == current))
                items.Add(new DropdownItem<string> { value = current, displayName = current + (Chinese ? "（不可用）" : " (unavailable)") });
            return items.ToArray();
        }

        public static DropdownItem<string>[] GetProtocolItems()
        {
            Setting instance = Instance;
            ProviderModel model = AgentHead.Catalog.Current.Find(instance?.Provider)?.Models.FirstOrDefault(item => item.Id == instance?.ProviderModel);
            IEnumerable<ModelProtocol> protocols = model == null
                ? new[] { ModelProtocol.Responses, ModelProtocol.ChatCompletions, ModelProtocol.AnthropicMessages }
                : model.Routes.Select(route => route.Protocol);
            if (instance?.Provider == ModelProviders.Compatible)
                protocols = new[] { ModelProtocol.Responses, ModelProtocol.ChatCompletions };
            if (instance?.Provider != ModelProviders.Compatible)
                protocols = new[] { ModelProtocol.Automatic }.Concat(protocols).Distinct();
            if (instance != null)
                protocols = protocols.Concat(new[] { instance.Connection.Protocol }).Distinct();
            return protocols.Select(protocol => new DropdownItem<string>
            {
                value = protocol.ToString(),
                displayName = protocol == ModelProtocol.Automatic ? (Chinese ? "自动" : "Automatic") :
                    protocol == ModelProtocol.ChatCompletions ? "Chat Completions" :
                    protocol == ModelProtocol.AnthropicMessages ? "Anthropic Messages" : "Responses",
            }).ToArray();
        }

        public static DropdownItem<string>[] GetExternalAgentItems()
        {
            var items = new List<DropdownItem<string>>
            {
                new DropdownItem<string>
                {
                    value = "",
                    displayName = Chinese ? "选择代理" : "Select an agent",
                },
            };
            items.AddRange(AgentHead.Registry.Agents.Select(agent => new DropdownItem<string>
            {
                value = agent.Id,
                displayName = agent.Name,
            }));
            string current = Instance?.ExternalAgent;
            if (!string.IsNullOrEmpty(current) && !items.Any(item => item.value == current))
            {
                items.Add(new DropdownItem<string>
                {
                    value = current,
                    displayName = current + (Chinese ? "（不可用）" : " (unavailable)"),
                });
            }
            return items.ToArray();
        }

        internal void LoadAgents()
        {
            try
            {
                AgentHead.Registry.Load();
                AgentListVersion++;
            }
            catch (Exception e)
            {
                AgentTimeline.Warn("acp", e.Message);
            }
            StartFetchAgents(false);
        }

        internal void LoadProviders()
        {
            try { AgentHead.Catalog.Load(); }
            catch (Exception e) { AgentTimeline.Warn("models", e.Message); }
            ModelPresetVersion++;
            StartFetchModels(false);
        }

        private void StartFetchAgents(bool notify)
        {
            if (Interlocked.Exchange(ref m_FetchingAgents, 1) != 0)
                return;
            SynchronizationContext context = SynchronizationContext.Current;
            _ = Task.Run(async () =>
            {
                string message;
                try
                {
                    await AgentHead.Registry.RefreshAsync();
                    AgentListVersion++;
                    message = "Loaded " + AgentHead.Registry.Agents.Count + " agents.";
                }
                catch (Exception e)
                {
                    message = "Could not refresh agents: " + e.Message;
                    AgentTimeline.Warn("acp", message);
                }
                finally
                {
                    Interlocked.Exchange(ref m_FetchingAgents, 0);
                }
                if (notify)
                    Report(context, message);
            });
        }

        public static DropdownItem<string>[] GetModelPresetItems()
        {
            Setting instance = Instance;
            var items = new List<DropdownItem<string>>
            {
                new DropdownItem<string> { value = "", displayName = Chinese ? "选择模型" : "Select a model" },
            };
            if (instance == null)
                return items.ToArray();
            IReadOnlyList<ProviderModel> live = instance.m_Connections.Models(instance.Provider);
            IEnumerable<ProviderModel> models = instance.Provider == ModelProviders.Compatible
                ? live : AgentHead.Catalog.Current.Find(instance.Provider)?.Models ?? Array.Empty<ProviderModel>();
            if (live.Count > 0 && instance.Provider != ModelProviders.Compatible)
            {
                var available = new HashSet<string>(live.Select(model => model.Id), StringComparer.Ordinal);
                models = models.Where(model => available.Contains(model.Id));
            }
            items.AddRange(models.Select(model => new DropdownItem<string>
            {
                value = model.Id,
                displayName = model.Name == model.Id ? model.Id : model.Name + " (" + model.Id + ")",
            }));
            string current = instance.ProviderModel;
            if (!string.IsNullOrEmpty(current) && !items.Any(item => item.value == current))
            {
                items.Add(new DropdownItem<string>
                {
                    value = current,
                    displayName = current,
                });
            }
            return items.ToArray();
        }

        private void StartFetchModels(bool notify = true)
        {
            if (Interlocked.Exchange(ref m_FetchingProviders, 1) != 0) return;
            SynchronizationContext context = SynchronizationContext.Current;
            _ = Task.Run(async () =>
            {
                string message;
                try
                {
                    await AgentHead.Catalog.RefreshAsync();
                    message = "Loaded " + AgentHead.Catalog.Current.Providers.Count + " supported providers from Models.dev.";
                }
                catch (Exception e)
                {
                    message = "Could not refresh the model catalog: " + e.Message;
                    AgentTimeline.Warn("models", message);
                }
                finally { Interlocked.Exchange(ref m_FetchingProviders, 0); }
                OnSettingsThread(context, () =>
                {
                    if (!ReferenceEquals(Instance, this)) return;
                    ModelPresetVersion++;
                    if (notify) SettingsNotice.Show(message);
                });
            });
        }

        private void StartCheckModels()
        {
            ProviderConnections profiles = m_Connections;
            ProviderConnection connection = Connection;
            SynchronizationContext context = SynchronizationContext.Current;
            _ = Task.Run(async () =>
            {
                string message;
                try
                {
                    ProviderConnection request = connection;
                    if (connection.ProviderId != ModelProviders.Compatible)
                    {
                        ResolvedModelConnection resolved = AgentHead.Catalog.Current.Resolve(connection);
                        request = connection.With(endpoint: resolved.Route.Endpoint, protocol: resolved.Route.Protocol);
                    }
                    IReadOnlyList<ProviderModel> models = await ModelProviders.LoadModelsAsync(request);
                    if (!ReferenceEquals(profiles, m_Connections) || !profiles.AcceptModels(connection, models))
                        return;
                    message = "The server listed " + models.Count + " models. Your selection has been preserved.";
                }
                catch (Exception e)
                {
                    message = SafeError(e, connection.ApiKey);
                }
                OnSettingsThread(context, () =>
                {
                    if (!ReferenceEquals(Instance, this)) return;
                    ModelPresetVersion++;
                    SettingsNotice.Show(message);
                });
            });
        }

        public override void SetDefaults()
        {
            MayorSource = AgentHead.BuiltIn;
            ExternalAgent = "";
            m_Connections = new ProviderConnections();
            Provider = "";
            AllowConstruction = true;
            AllowProgressionPurchases = true;
            AllowDemolition = true;
            AllowTreasury = true;
            AllowClock = true;
            AllowSave = false;
            AllowDiagnostics = false;
            AllowPanel = false;
            VisionTools = true;
        }

        private void StartHeadTest()
        {
            MayorSelection selection = CaptureMayor();
            SynchronizationContext context = SynchronizationContext.Current;
            _ = Task.Run(async () =>
            {
                string message;
                try
                {
                    using (IAgentSession session = AgentHead.Open(null, selection))
                    {
                        SessionTurnResult turn = await SessionTurn.RunAsync(
                            session,
                            "Reply in one sentence.");
                        message = Notice(turn);
                    }
                }
                catch (Exception e)
                {
                    message = SafeError(e, selection.Connection.ApiKey);
                }
                Report(context, string.IsNullOrEmpty(selection.Connection.ApiKey) ? message : message.Replace(selection.Connection.ApiKey, "[redacted]"));
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
            OnSettingsThread(context, () => SettingsNotice.Show(message));
        }

        private static string SafeError(Exception error, string apiKey)
        {
            string message = error.Message ?? "";
            if (!string.IsNullOrEmpty(apiKey)) message = message.Replace(apiKey, "[redacted]");
            return SessionRecord.RedactSecrets(message);
        }

        private static void OnSettingsThread(SynchronizationContext context, Action action)
        {
            if (context == null)
            {
                action();
                return;
            }
            context.Post(_ => action(), null);
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
                { m_Setting.GetOptionTabLocaleID(Setting.kConnectionSection), "Mayor" },
                { m_Setting.GetOptionTabLocaleID(Setting.kPermissionsSection), "City permissions" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kMayorGroup), "Mayor source" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kBuiltinGroup), "Built-in mayor" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kExternalGroup), "External agent" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kAgentGroup), "Permissions" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MayorSource)), "Mayor source" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.MayorSource)), "Choose the built-in mayor or an external agent. Apply settings to open a new chat." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Provider)), "Provider" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Provider)), "Choose from the supported providers in Models.dev. Each provider keeps its own key, model and connection settings locally. Apply to open a chat with this connection." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ExternalAgent)), "Agent" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ExternalAgent)), "Choose from the ACP Registry. Models and sign-in are managed by the agent outside the game. First use prepares the selected version; Node.js/npm or uv may be needed." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FetchAgents)), "Refresh agents" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.FetchAgents)), "Refresh the available agents and versions without changing your selection or applying settings." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ProviderEndpoint)), "Server address override" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ProviderEndpoint)), "Leave empty to use the selected model's address from the catalog. Custom services need their base URL, usually including /v1. Changing the host clears this connection's key." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ModelsEndpoint)), "Availability list address" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ModelsEndpoint)), "Used by Check server models. Leave empty to append /models to the selected server; Go uses its separate list address." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CheckModels)), "Check server models" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CheckModels)), "Ask this server which models are available. For a catalog provider, select a model first. Custom services need an address. Updates this connection's list without changing the selection or applying settings." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ProviderApiKey)), "API key" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ProviderApiKey)), "Saved only in this provider's settings. Custom services may leave this empty when authentication is not required." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FetchModels)), "Refresh model catalog" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.FetchModels)), "Refresh providers and models from Models.dev. Sends no API key. Keeps your selection and running chat; a failed refresh keeps the cached catalog." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ProviderModel)), "Model" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ProviderModel)), "Choose a model from this connection's list. You can also enter a model ID in advanced connection settings." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ConnectionDetails)), "Advanced connection settings" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ConnectionDetails)), "Enter a model ID, change addresses or protocol, and choose the context budget." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ModelId)), "Model ID" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ModelId)), "Enter the provider's model ID directly; refreshing the list preserves this selection." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Protocol)), "API protocol" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Protocol)), "Automatic uses the selected model's catalog entry. Choose a protocol explicitly for a custom service or a manually configured model." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestConnection)), "Test connection" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TestConnection)), "Test the currently edited connection and model with one short reply. Does not apply settings or reopen the city's chat." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestAcp)), "Test ACP" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TestAcp)), "Starts the selected agent, asks for one sentence, and stops it." },

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
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AutomaticContext)), "Use model context window" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AutomaticContext)), "Use the selected model's context window from the catalog. Custom services default to 128,000 tokens. Turn off to choose a smaller budget." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ContextTokens)), "Context budget" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ContextTokens)), "Choose a context budget within this model's window. Each provider remembers your choice." },

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
                { ChatLocale.Id("Plan.None"), "No active plan" },
            };
        }

        public void Unload() { }
    }
}
