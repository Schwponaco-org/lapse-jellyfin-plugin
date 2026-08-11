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
    /// <param name="value">The value stored in the config.</param>
    /// <param name="label">What the dropdown shows.</param>
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
/// One tunable an engine's binary actually accepts. Everything here comes from reading
/// the engine's own source rather than from what the plugin wishes it supported, so the
/// Advanced section of a card lists exactly the switches that engine has and nothing else.
/// </summary>
public class EngineParameter
{
    /// <summary>
    /// Gets or sets the key this is stored under in the engine's settings. Stable, since
    /// it's what a saved config keys off - not the flag name, which upstream could rename.
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
    /// Gets or sets the command line flag this drives, shown in the UI so it's obvious
    /// which switch is being set. Null for parameters that map to a positional argument.
    /// </summary>
    public string? Flag { get; set; }

    /// <summary>
    /// Gets or sets what sort of control to draw.
    /// </summary>
    public EngineParameterKind Kind { get; set; } = EngineParameterKind.Text;

    /// <summary>
    /// Gets or sets the engine's own default, as a string. An empty value means "leave the
    /// flag off entirely and let the engine decide", which is not the same as zero.
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
    /// Gets or sets a value indicating whether leaving this blank means "don't pass the
    /// flag". Used for optional switches like an explicit audio track, where zero is a
    /// real value and "unset" has to be distinguishable from it.
    /// </summary>
    public bool BlankMeansUnset { get; set; }
}

/// <summary>
/// The values an engine run should use for its parameters: whatever the admin saved,
/// falling back to the engine's own documented default. Engines read their arguments
/// through this so the fallback is in one place.
/// </summary>
public class EngineParameterValues
{
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, EngineParameter> _parameters;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineParameterValues"/> class.
    /// </summary>
    /// <param name="parameters">The engine's parameter definitions.</param>
    /// <param name="saved">What the admin saved, keyed by parameter key.</param>
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
    /// Gets an empty set, so an engine can still build a command line when the plugin
    /// instance isn't available (unit tests, or very early startup).
    /// </summary>
    public static EngineParameterValues Empty { get; } = new(Array.Empty<EngineParameter>(), null);

    /// <summary>
    /// Gets the raw string for a parameter, falling back to the engine's default.
    /// </summary>
    /// <param name="key">The parameter key.</param>
    /// <returns>The value, or an empty string when neither the admin nor the engine set one.</returns>
    public string GetString(string key)
    {
        if (_values.TryGetValue(key, out var saved) && !string.IsNullOrWhiteSpace(saved))
        {
            return saved.Trim();
        }

        // A saved blank on a "blank means unset" parameter is a deliberate choice to leave
        // the flag off, so it must not fall through to the default.
        if (_values.ContainsKey(key) && _parameters.TryGetValue(key, out var blankable) && blankable.BlankMeansUnset)
        {
            return string.Empty;
        }

        return _parameters.TryGetValue(key, out var parameter) ? parameter.DefaultValue : string.Empty;
    }

    /// <summary>
    /// Says whether a parameter is worth putting on the command line at all.
    ///
    /// A value that still equals the engine's own default is never passed. Leaving the
    /// flag off produces exactly the same behaviour by definition, and it means a default
    /// this plugin got wrong can't reach the engine unless somebody deliberately typed it.
    /// That is not hypothetical: alass's help prints "default: auto" for its encoding
    /// arguments, but the 2.0.0 binary rejects the literal string "auto" as an unknown
    /// encoding label and panics. Passing a documented default is at best redundant.
    /// </summary>
    /// <param name="key">The parameter key.</param>
    /// <returns>True if the flag should be passed.</returns>
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
    /// <param name="key">The parameter key.</param>
    /// <returns>True when the value reads as true.</returns>
    public bool GetBool(string key)
    {
        var value = GetString(key);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets a number parameter, or null when it isn't set to anything usable.
    /// </summary>
    /// <param name="key">The parameter key.</param>
    /// <returns>The number, or null.</returns>
    public double? GetNumber(string key)
    {
        var value = GetString(key);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Gets an integer parameter, or null when it isn't set to anything usable.
    /// </summary>
    /// <param name="key">The parameter key.</param>
    /// <returns>The integer, or null.</returns>
    public int? GetInt(string key)
    {
        var number = GetNumber(key);
        return number.HasValue ? (int)Math.Round(number.Value) : null;
    }

    /// <summary>
    /// Formats a number the way a command line wants it: invariant, and without a
    /// trailing ".0" on something that is really an integer.
    /// </summary>
    /// <param name="value">The number.</param>
    /// <returns>The text to pass on the command line.</returns>
    public static string Format(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.0000001
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
