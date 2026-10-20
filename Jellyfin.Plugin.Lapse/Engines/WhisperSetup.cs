// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// Whether LAPSE 2.3.0's --whisper can actually run here. LAPSE decodes the whole film to a
/// wav before it finds out whisper-cli or the model is missing, and caches nothing when it
/// fails, so a broken setup would cost a full decode on every sync. We check first and leave
/// the flag off until it works.
/// </summary>
public static class WhisperSetup
{
    /// <summary>
    /// The settings key for the model file.
    /// </summary>
    public const string ModelKey = "whisperModel";

    /// <summary>
    /// The settings key for where whisper-cli is.
    /// </summary>
    public const string CliKey = "whisperCli";

    /// <summary>
    /// The variable LAPSE reads whisper-cli's location from.
    /// </summary>
    public const string CliVariable = "LAPSE_WHISPER_CLI";

    // the presets LAPSE knows (preset() in its transcribe.cpp). It picks the alignment heads
    // from the file name, anything else it refuses after the decode.
    private static readonly string[] KnownModels =
    {
        "tiny", "tiny.en", "base", "base.en", "small", "small.en", "medium", "medium.en",
        "large-v1", "large-v2", "large-v3", "large-v3-turbo"
    };

    /// <summary>
    /// Checks the saved whisper settings.
    /// </summary>
    public static WhisperCheck Check(EngineParameterValues values)
    {
        var model = values.GetString(ModelKey);
        if (string.IsNullOrWhiteSpace(model))
        {
            return WhisperCheck.Off;
        }

        if (!IsKnownModelName(model))
        {
            return WhisperCheck.Broken(
                $"LAPSE works out which whisper model it has from the file name, and can't tell from {Path.GetFileName(model)}. "
                + "Keep the name whisper.cpp gave it, like ggml-base.en.bin.");
        }

        if (!File.Exists(model))
        {
            return WhisperCheck.Broken($"There's no whisper model at {model}.");
        }

        var cli = values.GetString(CliKey);
        if (!string.IsNullOrWhiteSpace(cli))
        {
            return File.Exists(cli)
                ? WhisperCheck.Ready(cli)
                : WhisperCheck.Broken($"There's no whisper-cli at {cli}.");
        }

        return FindOnPath() is { } found
            ? WhisperCheck.Ready(null, found)
            : WhisperCheck.Broken("whisper-cli isn't on the server's PATH. Set where it is, LAPSE doesn't ship it.");
    }

    /// <summary>
    /// Says whether a model file is named the way whisper.cpp names them (ggml-base.en.bin).
    /// </summary>
    public static bool IsKnownModelName(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("ggml-", StringComparison.Ordinal))
        {
            name = name[5..];
        }

        if (name.EndsWith(".bin", StringComparison.Ordinal))
        {
            name = name[..^4];
        }

        return KnownModels.Contains(name, StringComparer.Ordinal);
    }

    private static string? FindOnPath()
    {
        var name = OperatingSystem.IsWindows() ? "whisper-cli.exe" : "whisper-cli";
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return folders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// What <see cref="WhisperSetup.Check"/> found.
/// </summary>
public sealed class WhisperCheck
{
    private WhisperCheck(bool configured, string? problem, string? cli, string? found)
    {
        Configured = configured;
        Problem = problem;
        Cli = cli;
        Found = found;
    }

    /// <summary>
    /// Gets the answer for no model set.
    /// </summary>
    public static WhisperCheck Off { get; } = new(false, null, null, null);

    /// <summary>
    /// Gets a value indicating whether a model was set at all.
    /// </summary>
    public bool Configured { get; }

    /// <summary>
    /// Gets what's wrong, or null.
    /// </summary>
    public string? Problem { get; }

    /// <summary>
    /// Gets the whisper-cli path to hand LAPSE, or null when it finds it on the PATH itself.
    /// </summary>
    public string? Cli { get; }

    /// <summary>
    /// Gets where whisper-cli was found on the PATH, for showing on the card.
    /// </summary>
    public string? Found { get; }

    /// <summary>
    /// Gets a value indicating whether --whisper should be passed.
    /// </summary>
    public bool Usable => Configured && Problem is null;

    internal static WhisperCheck Broken(string problem) => new(true, problem, null, null);

    internal static WhisperCheck Ready(string? cli, string? found = null) => new(true, null, cli, found ?? cli);
}
