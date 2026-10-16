using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using AgentRuntime;
using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using Game;
using Game.SceneFlow;
using Game.UI;
using UnityEngine.Scripting;

namespace airimayor.Host
{
    /// <summary>
    /// Bridges the agent loop to Gameface: publishes agent state and a live
    /// event stream (deltas, tool cards, status), and receives player commands
    /// (send / interrupt). Registered at UIUpdate so it keeps running while the
    /// simulation is paused.
    /// </summary>
    public sealed partial class AgentUISystem : UISystemBase
    {
        private const string Group = "airimayor";
        private const int MaxEventsPerUpdate = 32;

        private readonly ConcurrentQueue<SessionUpdate> m_Events =
            new ConcurrentQueue<SessionUpdate>();
        private ValueBinding<string> m_StateBinding;
        private ValueBinding<string> m_PlacesBinding;
        private ValueBinding<string> m_PointModeBinding;
        private ValueBinding<string> m_PlaceNoticeBinding;
        private EventBinding<string> m_EventBinding;
        private int m_StateDirty;
        private IAgentSession m_Session;
        private SessionUpdate m_DeferredEvent;
        private int m_Reopen;
        private bool m_AutoStartSent;

        private void OnHeadApplied()
        {
            Interlocked.Exchange(ref m_Reopen, 1);
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            m_StateBinding = new ValueBinding<string>(
                Group,
                "state",
                "{}",
                ValueWriters.Create<string>(),
                System.Collections.Generic.EqualityComparer<string>.Default);
            AddBinding(m_StateBinding);

            m_PlacesBinding = new ValueBinding<string>(
                Group,
                "places",
                "[]",
                ValueWriters.Create<string>(),
                System.Collections.Generic.EqualityComparer<string>.Default);
            AddBinding(m_PlacesBinding);
            m_PointModeBinding = new ValueBinding<string>(
                Group,
                "pointMode",
                "",
                ValueWriters.Create<string>(),
                System.Collections.Generic.EqualityComparer<string>.Default);
            AddBinding(m_PointModeBinding);
            m_PlaceNoticeBinding = new ValueBinding<string>(
                Group,
                "placeNotice",
                "",
                ValueWriters.Create<string>(),
                System.Collections.Generic.EqualityComparer<string>.Default);
            AddBinding(m_PlaceNoticeBinding);

            m_EventBinding = new EventBinding<string>(Group, "event", ValueWriters.Create<string>());
            AddBinding(m_EventBinding);

            AddBinding(new TriggerBinding<string>(
                Group,
                "send",
                OnSend,
                ValueReaders.Create<string>()));
            AddBinding(new TriggerBinding(Group, "interrupt", OnInterrupt));
            AddBinding(new TriggerBinding<string>(
                Group,
                "pointMode",
                OnPointMode,
                ValueReaders.Create<string>()));
            AddBinding(new TriggerBinding<string>(
                Group,
                "removePlace",
                OnRemovePlace,
                ValueReaders.Create<string>()));
            AddBinding(new TriggerBinding<string>(
                Group,
                "framePlace",
                OnFramePlace,
                ValueReaders.Create<string>()));

            Setting.HeadApplied += OnHeadApplied;
            PushState();
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            if (mode != GameMode.Game)
            {
                LeaveGameSession();
                return;
            }

            ClearPointedPlaces();
            CloseSession();
            OpenSession();
            ResetSessionState();
            PushState();
        }

        private void LeaveGameSession()
        {
            ClearPointedPlaces();
            CloseSession();
            ResetSessionState();
            PushState();
        }

        private static bool IsInLoadedCity()
        {
            GameManager manager = GameManager.instance;
            return manager != null &&
                   manager.gameMode == GameMode.Game &&
                   !manager.isGameLoading;
        }

        private void OnSend(string text)
        {
            if (!IsInLoadedCity() || m_Session == null)
            {
                return;
            }
            System.Collections.Generic.List<PointedPlace> places = PointedPlaces().TakeDraft();
            string words = text ?? "";
            if (string.IsNullOrWhiteSpace(words) && places.Count == 0)
            {
                return;
            }
            if (places.Count == 0)
            {
                m_Session.Prompt(words);
                return;
            }
            m_Session.Prompt(
                PointedPlaceText.Compose(words, places),
                words.Trim(),
                PointedPlaceText.ToJson(places));
        }

        private void OnPointMode(string mode)
        {
            if (!IsInLoadedCity())
            {
                return;
            }
            PointedPlaces().Begin(mode ?? "");
        }

        private void OnRemovePlace(string id)
        {
            if (!IsInLoadedCity())
            {
                return;
            }
            PointedPlaces().Remove(id);
        }

        private void OnFramePlace(string json)
        {
            if (!IsInLoadedCity() || !PointedPlaceText.TryParse(json, out PointedPlace place))
            {
                return;
            }
            PointedPlaces().Frame(place);
        }

        private CS2MCP.PointedPlaceToolSystem PointedPlaces()
        {
            return World.GetOrCreateSystemManaged<CS2MCP.PointedPlaceToolSystem>();
        }

        private void ClearPointedPlaces()
        {
            PointedPlaces().ClearDraft();
        }

        private void PublishPointedPlaces()
        {
            CS2MCP.PointedPlaceToolSystem places = PointedPlaces();
            string draft = places.DraftJson();
            if (m_PlacesBinding.value != draft)
            {
                m_PlacesBinding.Update(draft);
            }
            string mode = places.Mode ?? "";
            if (m_PointModeBinding.value != mode)
            {
                m_PointModeBinding.Update(mode);
            }
            string notice = places.Notice ?? "";
            if (m_PlaceNoticeBinding.value != notice)
            {
                m_PlaceNoticeBinding.Update(notice);
            }
        }

        private void OnInterrupt()
        {
            if (!IsInLoadedCity())
            {
                return;
            }
            m_Session?.Cancel();
        }

        private void OpenSession()
        {
            m_Session = AgentSessionHost.Current = AgentHead.Open(CityName());
            m_Session.Updated += OnAgentEvent;
            Mod.log.Info("agent session opened " + m_Session.Record.SessionId);
        }

        private string CityName()
        {
            return World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>().cityName;
        }

        private void ResetSessionState()
        {
            while (m_Events.TryDequeue(out _)) { }
            m_DeferredEvent = null;
            m_AutoStartSent = false;
            Interlocked.Exchange(ref m_StateDirty, 1);
        }

        private void CloseSession()
        {
            if (m_Session == null)
            {
                return;
            }
            m_Session.Updated -= OnAgentEvent;
            Mod.log.Info("agent session closed " + m_Session.Record.SessionId);
            if (ReferenceEquals(AgentSessionHost.Current, m_Session))
            {
                AgentSessionHost.Current = null;
            }
            m_Session.Dispose();
            m_Session = null;
        }

        private void OnAgentEvent(SessionUpdate agentEvent)
        {
            if (agentEvent == null)
            {
                return;
            }
            m_Events.Enqueue(agentEvent);
            if (NeedsStateSnapshot(agentEvent))
            {
                Interlocked.Exchange(ref m_StateDirty, 1);
            }
        }

        private static bool NeedsStateSnapshot(SessionUpdate agentEvent)
        {
            if (agentEvent.Kind == "user" ||
                agentEvent.Kind == "error" ||
                agentEvent.Kind == "turn" ||
                agentEvent.Kind == "compact" ||
                agentEvent.Kind == "tool" ||
                agentEvent.Kind == "delta" ||
                agentEvent.Kind == "plan")
            {
                return true;
            }
            if (agentEvent.Kind != "status")
            {
                return false;
            }
            return agentEvent.Status == AgentStatus.Idle ||
                   agentEvent.Status == AgentStatus.Interrupted ||
                   agentEvent.Status == AgentStatus.Error;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            base.OnUpdate();
            TryAutoStart();
            if (Interlocked.Exchange(ref m_Reopen, 0) == 1 && IsInLoadedCity())
            {
                CloseSession();
                OpenSession();
                ResetSessionState();
            }
            int processed = 0;
            while (processed < MaxEventsPerUpdate && TryDequeueForUi(out SessionUpdate agentEvent))
            {
                m_EventBinding.Trigger(agentEvent.ToJsonString());
                processed++;
            }
            if (Interlocked.Exchange(ref m_StateDirty, 0) == 1)
            {
                PushState();
            }
            PublishPointedPlaces();
        }

        private void TryAutoStart()
        {
            GameManager manager = GameManager.instance;
            if (manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                m_AutoStartSent = false;
                return;
            }
            if (m_AutoStartSent || !Setting.StaticAutoStart)
            {
                return;
            }
            if (m_Session == null)
            {
                return;
            }
            m_AutoStartSent = true;
            m_Session.Prompt(Setting.StaticStartupPrompt);
        }

        private bool TryDequeueForUi(out SessionUpdate agentEvent)
        {
            if (m_DeferredEvent != null)
            {
                agentEvent = m_DeferredEvent;
                m_DeferredEvent = null;
            }
            else if (!m_Events.TryDequeue(out agentEvent))
            {
                return false;
            }

            if (agentEvent.Kind != "delta")
            {
                return true;
            }

            var text = new StringBuilder(agentEvent.Text ?? "");
            while (m_Events.TryDequeue(out SessionUpdate next))
            {
                if (next.Kind != "delta")
                {
                    m_DeferredEvent = next;
                    break;
                }
                text.Append(next.Text ?? "");
            }

            agentEvent = new SessionUpdate
            {
                Kind = "delta",
                Text = text.ToString(),
            };
            return true;
        }

        private void PushState()
        {
            string json = m_Session == null ? "{}" : m_Session.ChatStateJson();
            if (m_StateBinding.value != json)
            {
                m_StateBinding.Update(json);
            }
        }

        [Preserve]
        protected override void OnDestroy()
        {
            Setting.HeadApplied -= OnHeadApplied;
            CloseSession();
            base.OnDestroy();
        }
    }
}
