using Clio.XmlEngine;
using ff14bot.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ff14bot.NeoProfiles.Tags;

/// <summary>
/// Enables or disables the specified plugins.
/// </summary>
[XmlElement("SetPlugins")]
public class SetPluginsTag : AbstractTaskTag
{
    /// <summary>
    /// Gets or sets comma separated list of plugins to change state of.
    /// </summary>
    [XmlAttribute("PluginNames")]
    public string PluginNames { get; set; }

    /// <summary>
    /// Gets or sets comma separated list of new plugin states.
    /// </summary>
    [XmlAttribute("IsEnabled")]
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc/>
    protected override Task<bool> RunAsync()
    {
        IEnumerable<PluginContainer> plugins = PluginManager.Plugins.Where(p => PluginNames.Split(',').Contains(p.Plugin.Name, StringComparer.OrdinalIgnoreCase));

        foreach (PluginContainer p in plugins)
        {
            p.Enabled = IsEnabled;
        }

        return Task.FromResult(false);
    }
}
