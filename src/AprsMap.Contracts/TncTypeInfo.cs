// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>A TNC type a serial source can be switched into KISS with (docs/features/F3.2-built-in-tncs.md, behavior 4).</summary>
/// <param name="Id">What <see cref="SourceSettings.TncType"/> holds, such as <c>th-d72</c>.</param>
/// <param name="Name">Its name, such as <c>Kenwood TH-D72</c>.</param>
public sealed record TncTypeInfo(string Id, string Name);
