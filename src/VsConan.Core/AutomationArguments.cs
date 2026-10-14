using System;
using System.Collections.Generic;
using System.Linq;

namespace VsConan.Core;

/// <summary>
/// Arguments passed to a VsConan command by automation, e.g.
/// <c>DTE.ExecuteCommand("Conan.SelectProfiles", "default debug /build:default /id:42")</c>.
/// Switches start with '/'; any other word (comma- or space-separated) is a host profile name.
/// </summary>
public sealed class AutomationArguments
{
    /// <summary>Echoed in the command result so the caller can tell its request's result apart.</summary>
    public string? RequestId { get; private set; }

    /// <summary><c>/force</c>: install even if nothing has changed.</summary>
    public bool Force { get; private set; }

    /// <summary><c>/build:&lt;profile&gt;</c>: the build profile (<c>-pr:b</c>).</summary>
    public string? BuildProfile { get; private set; }

    public List<string> HostProfiles { get; } = new();

    /// <summary>Problems with the arguments, such as unknown switches.</summary>
    public List<string> Errors { get; } = new();

    public static AutomationArguments Parse(string? text)
    {
        var arguments = new AutomationArguments();
        var tokens = (text ?? string.Empty).Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (!token.StartsWith("/", StringComparison.Ordinal))
            {
                arguments.HostProfiles.Add(token);
                continue;
            }

            var colon = token.IndexOf(':');
            var name = (colon < 0 ? token.Substring(1) : token.Substring(1, colon - 1)).ToLowerInvariant();
            var value = colon < 0 ? null : token.Substring(colon + 1);

            switch (name)
            {
                case "id" when !string.IsNullOrEmpty(value):
                    arguments.RequestId = value;
                    break;
                case "build" when !string.IsNullOrEmpty(value):
                    arguments.BuildProfile = value;
                    break;
                case "force" when value is null:
                    arguments.Force = true;
                    break;
                default:
                    arguments.Errors.Add($"Unknown or malformed switch '{token}'. Expected /id:<token>, /force or /build:<profile>.");
                    break;
            }
        }

        return arguments;
    }
}
