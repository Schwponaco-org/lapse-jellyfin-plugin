// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.Lapse.Engines;

/// <summary>
/// What kind of control the dashboard should draw for one engine parameter.
/// </summary>
public enum EngineParameterKind
{
    /// <summary>
    /// A checkbox. The value is "true" or "false".
    /// </summary>
    Boolean,

    /// <summary>
    /// A number box.
    /// </summary>
    Number,

    /// <summary>
    /// A free text box.
    /// </summary>
    Text,

    /// <summary>
    /// A dropdown of fixed choices.
    /// </summary>
    Select
}

/// <summary>
/// One choice in a <see cref="EngineParameterKind.Select"/> parameter.
/// </summary>
public class EngineParameterOption
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EngineParameterOption"/> class.
    /// </summary>
    public EngineParameterOption(string value, string label)
    {
        Value = value;
        Label = label;
    }

    /// <summary>
    /// Gets the stored value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the label shown in the dropdown.
    /// </summary>
    public string Label { get; }
}

/// <summary>
/// One tunable the engine binary actually accepts (read off its source), shown in the card's Advanced section.
/// </summary>
public class EngineParameter
{
    /// <summary>
    /// Gets or sets the settings key. Stable, unlike the flag name which upstream could rename.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the label shown next to the control.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the one line explanation shown under the control.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the flag this drives (shown in the UI). Null for positional arguments.
    /// </summary>
    public string? Flag { get; set; }

    /// <summary>
    /// Gets or sets what sort of control to draw.
    /// </summary>
    public EngineParameterKind Kind { get; set; } = EngineParameterKind.Text;

    /// <summary>
    /// Gets or sets the engine's default as a string. Empty means leave the flag off, which isn't the same as zero.
    /// </summary>
    public string DefaultValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the lowest accepted value, for number parameters.
    /// </summary>
    public double? Minimum { get; set; }

    /// <summary>
    /// Gets or sets the highest accepted value, for number parameters.
    /// </summary>
    public double? Maximum { get; set; }

    /// <summary>
    /// Gets or sets the step for number parameters.
    /// </summary>
    public double Step { get; set; } = 1;

    /// <summary>
    /// Gets the choices, for select parameters.
    /// </summary>
    public List<EngineParameterOption> Options { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether blank means "don't pass the flag" (for things
    /// like audio track where 0 is a real value).
    /// </summary>
    public bool BlankMeansUnset { get; set; }
}

/// <summary>
/// Parameter values for a run: what the admin saved, else the engine default.
/// </summary>
public class EngineParameterValues
{
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, EngineParameter> _parameters;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineParameterValues"/> class.
    /// </summary>
    public EngineParameterValues(IEnumerable<EngineParameter> parameters, IReadOnlyDictionary<string, string?>? saved)
    {
        _parameters = new Dictionary<string, EngineParameter>(StringComparer.OrdinalIgnoreCase);
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters)
        {
            _parameters[parameter.Key] = parameter;
        }

        if (saved is null)
        {
            return;
        }

        foreach (var pair in saved)
        {
            if (pair.Value is not null)
            {
                _values[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>
    /// Gets an empty set, for when there's no plugin instance (tests, early startup).
    /// </summary>
    public static EngineParameterValues Empty { get; } = new(Array.Empty<EngineParameter>(), null);

    /// <summary>
    /// Gets the raw string for a parameter, falling back to the engine's default.
    /// </summary>
    public string GetString(string key)
    {
        if (_values.TryGetValue(key, out var saved) && !string.IsNullOrWhiteSpace(saved))
        {
            return saved.Trim();
        }

        // a saved blank on a blank-means-unset parameter means "off", not "use the default"
        if (_values.ContainsKey(key) && _parameters.TryGetValue(key, out var blankable) && blankable.BlankMeansUnset)
        {
            return string.Empty;
        }

        return _parameters.TryGetValue(key, out var parameter) ? parameter.DefaultValue : string.Empty;
    }

    /// <summary>
    /// Says whether a parameter is worth putting on the command line at all.
    ///
    /// Values equal to the engine default are never passed, leaving the flag off does the
    /// same thing and a default we got wrong can't break anything. (alass' help says
    /// "default: auto" for encodings, but 2.0.0 panics if you actually pass "auto".)
    /// </summary>
    public bool ShouldPass(string key)
    {
        var value = GetString(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parameter = _parameters.TryGetValue(key, out var found) ? found : null;
        return parameter is null || !string.Equals(value, parameter.DefaultValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a boolean parameter.
    /// </summary>
    public bool GetBool(string key)
    {
        var value = GetString(key);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets a number parameter, or null when it isn't set to anything usable.
    /// </summary>
    public double? GetNumber(string key)
    {
        var value = GetString(key);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Adds flag + number if it's worth passing (see <see cref="ShouldPass"/>).
    /// </summary>
    public void AddNumber(List<string> args, string key, string flag)
    {
        if (ShouldPass(key) && GetNumber(key) is { } value)
        {
            args.Add(flag);
            args.Add(Format(value));
        }
    }

    /// <summary>
    /// Adds flag + value if it's worth passing (see <see cref="ShouldPass"/>).
    /// </summary>
    public void AddText(List<string> args, string key, string flag)
    {
        if (ShouldPass(key))
        {
            args.Add(flag);
            args.Add(GetString(key));
        }
    }

    /// <summary>
    /// Puts a flag on its own on a command line, when the parameter is ticked.
    /// </summary>
    public void AddSwitch(List<string> args, string key, string flag)
    {
        if (GetBool(key))
        {
            args.Add(flag);
        }
    }

    /// <summary>
    /// Formats a number for a command line: invariant, no ".0" on integers.
    /// </summary>
    public static string Format(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.0000001
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
