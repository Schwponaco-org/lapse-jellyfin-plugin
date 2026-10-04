// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System.Xml.Serialization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Lapse.Tests;

/// <summary>
/// Just enough of a Jellyfin server for the plugin to start in: a folder to keep its
/// configuration in and a serializer to write it with. Plugin.Instance is a static, so
/// every test in the run shares the one plugin and resets what it changes.
/// </summary>
public static class TestHost
{
    private static readonly object Gate = new();

    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "lapse-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public static Plugin Plugin
    {
        get
        {
            lock (Gate)
            {
                if (Plugin.Instance is null)
                {
                    Directory.CreateDirectory(Root);
                    _ = new Plugin(new Paths(Root), new Xml(), NullLogger<Plugin>.Instance);
                }

                return Plugin.Instance!;
            }
        }
    }

    /// <summary>
    /// Gets a fresh folder for one test's files.
    /// </summary>
    /// <returns>The folder.</returns>
    public static string NewFolder()
    {
        var folder = Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed class Paths : IApplicationPaths
    {
        public Paths(string root)
        {
            ProgramDataPath = root;
        }

        public string ProgramDataPath { get; }

        public string WebPath => Path.Combine(ProgramDataPath, "web");

        public string ProgramSystemPath => ProgramDataPath;

        public string DataPath => Path.Combine(ProgramDataPath, "data");

        public string ImageCachePath => Path.Combine(ProgramDataPath, "cache", "images");

        public string PluginsPath => Path.Combine(ProgramDataPath, "plugins");

        public string PluginConfigurationsPath => Path.Combine(PluginsPath, "configurations");

        public string LogDirectoryPath => Path.Combine(ProgramDataPath, "log");

        public string ConfigurationDirectoryPath => Path.Combine(ProgramDataPath, "config");

        public string SystemConfigurationFilePath => Path.Combine(ConfigurationDirectoryPath, "system.xml");

        public string CachePath => Path.Combine(ProgramDataPath, "cache");

        public string TempDirectory => Path.GetTempPath();

        public string VirtualDataPath => DataPath;

        public string TrickplayPath => Path.Combine(DataPath, "trickplay");

        public string BackupPath => Path.Combine(DataPath, "backups");

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }

        public void MakeSanityCheckOrThrow()
        {
        }
    }

    private sealed class Xml : IXmlSerializer
    {
        public object? DeserializeFromStream(Type type, Stream stream) => new XmlSerializer(type).Deserialize(stream);

        public void SerializeToStream(object obj, Stream stream) => new XmlSerializer(obj.GetType()).Serialize(stream, obj);

        public void SerializeToFile(object obj, string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var stream = File.Create(file);
            SerializeToStream(obj, stream);
        }

        public object? DeserializeFromFile(Type type, string file)
        {
            using var stream = File.OpenRead(file);
            return DeserializeFromStream(type, stream);
        }

        public object? DeserializeFromBytes(Type type, byte[] buffer)
        {
            using var stream = new MemoryStream(buffer);
            return DeserializeFromStream(type, stream);
        }
    }
}
