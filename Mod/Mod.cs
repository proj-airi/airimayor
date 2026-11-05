using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using System.IO;
using airimayor.Host;

namespace airimayor
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(airimayor)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        private Setting m_Setting;

        public static string InstallDirectory { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info($"Current mod asset at {asset.path}");
                InstallDirectory = Path.GetDirectoryName(asset.path);
            }

            m_Setting = new Setting(this);
            m_Setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(m_Setting));
            GameManager.instance.localizationManager.AddSource("zh-HANS", new LocaleZhHans(m_Setting));
            AssetDatabase.global.LoadSettings(nameof(airimayor), m_Setting, new Setting(this));
            Setting.Instance = m_Setting;
            m_Setting.CaptureAppliedMayor();
            m_Setting.LoadAgents();

            // UIUpdate keeps running while the simulation is paused.
            updateSystem.UpdateAt<ToolQueueSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<CS2MCP.BridgeSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<CS2MCP.BridgeToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<CS2MCP.PointedPlaceToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<AgentUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            AgentSessionHost.Current?.Dispose();
            AgentSessionHost.Current = null;
            Setting.Instance = null;
            if (m_Setting != null)
            {
                m_Setting.UnregisterInOptionsUI();
                m_Setting = null;
            }
        }
    }
}
