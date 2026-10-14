using System;

namespace VsConan;

/// <summary>Must match the symbols in VsConanPackage.vsct.</summary>
internal static class PackageGuids
{
    public const string PackageString = "b0936d89-2ca9-4bff-b436-e9b263f971a2";
    public const string CommandSetString = "7567d0e1-08e2-4f9e-b037-d72b4de5e320";

    public static readonly Guid CommandSet = new(CommandSetString);
}

internal static class PackageIds
{
    public const int InstallDependenciesCommand = 0x0100;
    public const int SelectProfilesCommand = 0x0101;
    public const int ShowStatusCommand = 0x0102;
}
