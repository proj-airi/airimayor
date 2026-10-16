using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentRuntime;

namespace airimayor.Host
{
    /// <summary>
    /// City tools behind <see cref="IAgentTools"/>. Permission filtering
    /// lives here; the runtime never names city tools.
    /// </summary>
    internal sealed class Cs2AgentTools : IAgentTools
    {
        private static readonly HashSet<string> s_Construction =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "place_building",
                "build_network",
                "zone",
                "set_road_features",
                "replace_road_type",
                "expand_operational_area",
                "set_facility_upgrade",
                "add_transit_line",
                "remove_transit_line",
            };

        private static readonly HashSet<string> s_Treasury =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "set_budget", "set_policy", "buy_tiles",
            };

        private static readonly HashSet<string> s_VisionTools =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "screenshot", "get_camera", "set_camera", "map_image",
            };

        public IReadOnlyList<AgentToolDeclaration> List(bool visionAvailable)
        {
            var listed = new List<AgentToolDeclaration>();
            foreach (ToolDefinition tool in ToolCatalog.Tools)
            {
                if (!IsAllowed(tool.Name, visionAvailable))
                {
                    continue;
                }
                listed.Add(new AgentToolDeclaration
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    ParametersJson = tool.Parameters.GetRawText(),
                });
            }
            return listed;
        }

        public async Task<AgentToolResult> InvokeAsync(
            string name,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            ToolDefinition tool = ToolCatalog.Find(name);
            if (tool == null || !IsAllowed(name, Setting.StaticVisionTools))
            {
                return new AgentToolResult
                {
                    Success = false,
                    Text = JsonSerializer.Serialize(new { error = "unknown tool: " + name }),
                };
            }
            if (string.Equals(name, "panel", StringComparison.Ordinal))
            {
                PanelResult panel = await PanelCommand.RunAsync(argumentsJson, cancellationToken);
                return new AgentToolResult
                {
                    Success = panel.Ok,
                    Text = panel.Text,
                };
            }
            ToolInvocationResult invoked = await AgentToolBridge.InvokeAsync(
                tool, argumentsJson, cancellationToken);
            byte[] image = invoked.ImagePng;
            return new AgentToolResult
            {
                Success = invoked.Success,
                Text = invoked.Text,
                ImagePng = image,
                ImageCaption = ImageCaption(name),
                PreviewJpeg = invoked.PreviewBytes,
                Applied = invoked.Applied,
                QueuedMs = invoked.QueuedMs,
            };
        }

        private static string ImageCaption(string name)
        {
            if (string.Equals(name, "map_image", StringComparison.Ordinal))
            {
                return "Map overview returned by the map_image tool.";
            }
            if (string.Equals(name, "screenshot", StringComparison.Ordinal))
            {
                return "Screenshot returned by the screenshot tool.";
            }
            return null;
        }

        private static bool IsAllowed(string name, bool visionAvailable)
        {
            if (s_Construction.Contains(name))
            {
                return Setting.StaticAllowConstruction;
            }
            if (s_Treasury.Contains(name))
            {
                return Setting.StaticAllowTreasury;
            }
            if (!visionAvailable && s_VisionTools.Contains(name))
            {
                return false;
            }
            if (name == "purchase_development_node")
            {
                return Setting.StaticAllowProgressionPurchases;
            }
            if (name == "set_simulation")
            {
                return Setting.StaticAllowClock;
            }
            if (name == "demolish")
            {
                return Setting.StaticAllowDemolition;
            }
            if (name == "save_game")
            {
                return Setting.StaticAllowSave;
            }
            if (name == "debug_zone_blocks")
            {
                return Setting.StaticAllowDiagnostics;
            }
            if (name == "panel")
            {
                return Setting.StaticAllowPanel;
            }
            return true;
        }
    }
}
