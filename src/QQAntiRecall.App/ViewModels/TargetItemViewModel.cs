using System;
using System.Collections.Generic;
using System.Linq;
using QQAntiRecall.Core;

namespace QQAntiRecall.App.ViewModels;

/// <summary>
/// Presents one verified versioned QQ patch target.
/// </summary>
public sealed class TargetItemViewModel : ViewModelBase
{
    /// <summary>
    /// Creates a target row from an immutable core scan result.
    /// </summary>
    /// <param name="target">Verified target result to display.</param>
    public TargetItemViewModel(TargetScanResult target)
    {
        ArgumentNullException.ThrowIfNull(target);

        Version = target.Version;
        FilePath = target.FilePath;
        State = target.State;
        Sha256 = target.Sha256;
        Detail = target.Detail;
        StatusLabel = GetStatusLabel(target.State);
        SignatureSummary = BuildSignatureSummary(target.Signatures);
    }

    /// <summary>
    /// Gets the QQ version label from versions/config.json.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets the absolute wrapper.node path that was scanned.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the verified aggregate patch state.
    /// </summary>
    public TargetPatchState State { get; }

    /// <summary>
    /// Gets the SHA-256 digest of the scanned file.
    /// </summary>
    public string Sha256 { get; }

    /// <summary>
    /// Gets the core service's target-specific explanation.
    /// </summary>
    public string Detail { get; }

    /// <summary>
    /// Gets a concise localized label for the target state.
    /// </summary>
    public string StatusLabel { get; }

    /// <summary>
    /// Gets the localized state text expected by the target-row badge.
    /// </summary>
    public string StateLabel => StatusLabel;

    /// <summary>
    /// Gets a compact summary of original and patched signature counts.
    /// </summary>
    public string SignatureSummary { get; }

    /// <summary>
    /// Gets a compact digest prefix for the target-row metadata display.
    /// </summary>
    public string ShortHash => Sha256.Length <= 16 ? Sha256 : $"{Sha256[..16]}...";

    /// <summary>
    /// Gets whether this target contains exactly one match for every original signature.
    /// </summary>
    public bool IsReady => State == TargetPatchState.ReadyToInstall;

    /// <summary>
    /// Gets whether this target contains the complete installed patch set.
    /// </summary>
    public bool IsInstalled => State == TargetPatchState.Installed;

    /// <summary>
    /// Gets whether this target needs attention instead of a normal install or restore action.
    /// </summary>
    public bool IsProblem => State is TargetPatchState.Missing or TargetPatchState.Inconsistent or TargetPatchState.Unsupported;

    /// <summary>
    /// Gets whether this target is safely and completely patched.
    /// </summary>
    public bool IsStatusSuccess => State == TargetPatchState.Installed;

    /// <summary>
    /// Gets whether this target is actionable or explicitly unsupported.
    /// </summary>
    public bool IsStatusWarning => State is TargetPatchState.ReadyToInstall or TargetPatchState.Unsupported;

    /// <summary>
    /// Gets whether this target cannot be safely modified in its current state.
    /// </summary>
    public bool IsStatusError => State is TargetPatchState.Missing or TargetPatchState.Inconsistent;

    /// <summary>
    /// Maps the core target state to concise Chinese UI text.
    /// </summary>
    /// <param name="state">Verified aggregate state to localize.</param>
    /// <returns>The text displayed in the target status badge.</returns>
    private static string GetStatusLabel(TargetPatchState state) => state switch
    {
        TargetPatchState.ReadyToInstall => "可安装",
        TargetPatchState.Installed => "已启用",
        TargetPatchState.Missing => "文件缺失",
        TargetPatchState.Inconsistent => "状态异常",
        TargetPatchState.Unsupported => "暂不支持",
        _ => "未知状态",
    };

    /// <summary>
    /// Formats verified signature counts without exposing byte-level implementation details.
    /// </summary>
    /// <param name="signatures">Per-signature original and patched match counts.</param>
    /// <returns>A single-line summary suitable for target metadata.</returns>
    private static string BuildSignatureSummary(IReadOnlyList<PatchSignatureStatus> signatures)
    {
        if (signatures.Count == 0)
        {
            return "未返回特征签名信息";
        }

        return string.Join(
            " · ",
            signatures.Select(signature =>
                $"{signature.Name}: 原始 {signature.OriginalMatchCount} / 已补丁 {signature.PatchedMatchCount}"));
    }
}
