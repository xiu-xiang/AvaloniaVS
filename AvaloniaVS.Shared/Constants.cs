using System;

namespace AvaloniaVS;

internal static class Constants
{
    public const string PackageGuidString = "5CFBE9C1-7536-4CC8-8CC1-E91B32D16FA1";
    public static readonly Guid PackageGuid = new (PackageGuidString);
    public const string PackageName = "AxamlCraft";
    public const string axaml = nameof(axaml);
    public const string xaml = nameof(xaml);
    public const string paml = nameof(paml);

    public const string AvaloviaFactoryEditorGuidString = @"3780BD45-AE20-4E60-B2C4-0A8A791CE117";
    public static readonly Guid AvaloviaFactoryEditorGuid = new (AvaloviaFactoryEditorGuidString);

    public const string AvaloniaCapability = nameof(Avalonia);
}
