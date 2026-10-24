using System;
using QQAntiRecall.Core;

namespace QQAntiRecall.App.ViewModels;

/// <summary>
/// Presents one versioned QQNT.dll integrity self-check target.
/// </summary>
public sealed class QqntIntegrityTargetItemViewModel : ViewModelBase
{
    /// <summary>
    /// Creates an integrity row from an immutable core scan result.
    /// </summary>
    /// <param name="target">Verified integrity target result to display.</param>
    public QqntIntegrityTargetItemViewModel(QqntIntegrityTargetResult target)
    {
        ArgumentNullException.ThrowIfNull(target);

        Version = target.Version;
        FilePath = target.FilePath;
        State = target.State;
        Sha256 = target.Sha256;
        Detail = target.Detail;
        BackupPath = target.BackupPath;
        HasBackup = target.HasBackup;
        StatusLabel = GetStatusLabel(target.State);
    }

    /// <summary>
    /// Gets the QQ version directory that owns this QQNT.dll.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets the absolute QQNT.dll path that was scanned.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the verified integrity self-check state.
    /// </summary>
    public QqntIntegrityState State { get; }

    /// <summary>
    /// Gets the SHA-256 digest of the scanned file.
    /// </summary>
    public string Sha256 { get; }

    /// <summary>
    /// Gets the core service's target-specific explanation.
    /// </summary>
    public string Detail { get; }

    /// <summary>
    /// Gets the original-byte backup path stored next to the DLL.
    /// </summary>
    public string BackupPath { get; }

    /// <summary>
    /// Gets whether an original-byte backup is available for restore.
    /// </summary>
    public bool HasBackup { get; }

    /// <summary>
    /// Gets a concise localized label for the integrity state.
    /// </summary>
    public string StatusLabel { get; }

    /// <summary>
    /// Gets the localized state text expected by the target-row badge.
    /// </summary>
    public string StateLabel => StatusLabel;

    /// <summary>
    /// Gets the localized backup availability text.
    /// </summary>
    public string BackupLabel => HasBackup ? "可还原" : "无备份";

    /// <summary>
    /// Gets a compact digest prefix for the metadata display.
    /// </summary>
    public string ShortHash => Sha256.Length <= 16 ? Sha256 : $"{Sha256[..16]}...";

    /// <summary>
    /// Gets whether this target still terminates QQ after the delayed check.
    /// </summary>
    public bool IsReady => State == QqntIntegrityState.ReadyToPatch;

    /// <summary>
    /// Gets whether this target can no longer terminate QQ because of the patch.
    /// </summary>
    public bool IsPatched => State is QqntIntegrityState.Patched or QqntIntegrityState.SkipPatched;

    /// <summary>
    /// Gets whether this target needs attention instead of a normal apply action.
    /// </summary>
    public bool IsProblem => State is QqntIntegrityState.Unsupported or QqntIntegrityState.Unreadable;

    /// <summary>
    /// Maps the core integrity state to concise Chinese UI text.
    /// </summary>
    /// <param name="state">Verified integrity state to localize.</param>
    /// <returns>The text displayed in the integrity status badge.</returns>
    private static string GetStatusLabel(QqntIntegrityState state) => state switch
    {
        QqntIntegrityState.ReadyToPatch => "检测有效",
        QqntIntegrityState.Patched => "已关闭",
        QqntIntegrityState.SkipPatched => "已跳过",
        QqntIntegrityState.Unsupported => "暂不支持",
        QqntIntegrityState.Unreadable => "读取失败",
        _ => "未知状态",
    };
}
