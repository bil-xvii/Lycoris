using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Monolith.Core.Updates;

/// <summary>Why an update file was refused. The message is Russian, ready for the user.</summary>
public sealed class UpdateRefusedException(string message) : Exception(message);

/// <summary>Limits and hosts of one update download. Tests use smaller sizes.</summary>
public sealed record UpdatePolicy
{
    public long MinSize { get; init; } = 40L << 20;
    public long MaxSize { get; init; } = 300L << 20;

    /// <summary>Exact host names a download (and every redirect hop) may use.</summary>
    public IReadOnlyList<string> Hosts { get; init; } =
        ["api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>Release assets must be served from this repository: https://github.com/bil-xvii/Lycoris-releases/releases/download/…</summary>
    public string AssetPathPrefix { get; init; } = "/" + UpdateService.Repository + "/releases/download/";
}

/// <summary>Pure checks of what was downloaded: link, size, hash, PE header, signer. Unit-tested.</summary>
public static partial class UpdateVerifier
{
    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();

    public static bool IsSha256Hex(string? s) => s is { Length: 64 } && Sha256Regex().IsMatch(s);

    /// <summary>HTTPS, default port, no credentials, host on the list.</summary>
    public static bool IsAllowedHop(Uri? url, UpdatePolicy policy) =>
        url is { IsAbsoluteUri: true } &&
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo) &&
        policy.Hosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>The asset link written in the release: github.com + the releases repository's download path.</summary>
    public static bool IsAssetUrl(string? text, UpdatePolicy policy, string fileName) =>
        Uri.TryCreate(text, UriKind.Absolute, out var u) &&
        IsAllowedHop(u, policy) &&
        u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        u.AbsolutePath.StartsWith(policy.AssetPathPrefix, StringComparison.Ordinal) &&
        u.AbsolutePath.EndsWith("/" + fileName, StringComparison.Ordinal) &&
        !u.AbsolutePath.Contains("..", StringComparison.Ordinal);

    /// <summary>First 64-hex token of a "Lycoris.exe.sha256" file ("hash  Lycoris.exe" or just "hash"), lower case; null if none.</summary>
    public static string? ParseShaFile(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var first = text.Trim().Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return IsSha256Hex(first) ? first!.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The one expected hash. The .sha256 asset and GitHub's own "digest" both count when present; if both are present they
    /// must agree; with neither the update is refused (nothing to check the file against).
    /// </summary>
    public static string ExpectedHash(string? fromShaFile, string? fromDigest)
    {
        var a = IsSha256Hex(fromShaFile) ? fromShaFile!.ToLowerInvariant() : null;
        var b = IsSha256Hex(fromDigest) ? fromDigest!.ToLowerInvariant() : null;
        if (a is null && b is null)
            throw new UpdateRefusedException(Loc.T("У релиза нет контрольной суммы — обновление отменено."));
        if (a is not null && b is not null && a != b)
            throw new UpdateRefusedException(Loc.T("Контрольные суммы релиза не совпадают между собой — обновление отменено."));
        return a ?? b!;
    }

    public static void CheckSize(long size, UpdatePolicy policy)
    {
        if (size < policy.MinSize || size > policy.MaxSize)
            throw new UpdateRefusedException(Loc.Tf("Размер файла обновления ({0}) выходит за допустимые пределы — обновление отменено.", SystemProfile.FormatBytes(size)));
    }

    /// <summary>MZ + PE\0\0 + machine x64 + PE32+ optional header. Reads only the first KB.</summary>
    public static bool IsPe64(ReadOnlySpan<byte> head)
    {
        if (head.Length < 0x40 || head[0] != 'M' || head[1] != 'Z') return false;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(head[0x3C..]);
        if (pe < 0x40 || pe > head.Length - 26) return false;
        if (head[pe] != 'P' || head[pe + 1] != 'E' || head[pe + 2] != 0 || head[pe + 3] != 0) return false;
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(head[(pe + 4)..]);
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(head[(pe + 24)..]);
        return machine == 0x8664 && magic == 0x20B;
    }

    public static bool FileIsPe64(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buf = new byte[4096];
            var n = f.Read(buf, 0, buf.Length);
            return IsPe64(buf.AsSpan(0, n));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static async Task<string> Sha256Async(Stream s, CancellationToken ct)
    {
        s.Position = 0;
        var hash = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return await Sha256Async(f, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Signature policy. Neither file signed → fine (the SHA-256 is the integrity check). The running exe signed → the new one
    /// must carry a signature of the same signer (organization) and must not be broken (a signature that no longer matches the
    /// file is always refused); a "Valid" signature is demanded when the current one is "Valid". The new one signed while the
    /// current is not → it must be intact.
    /// </summary>
    public static string? CheckSigners(SignatureInfo current, SignatureInfo candidate)
    {
        var broken = candidate.Status.Equals("HashMismatch", StringComparison.OrdinalIgnoreCase);
        if (broken)
            return Loc.T("Цифровая подпись нового файла повреждена — обновление отменено.");
        if (!current.HasSigner)
        {
            if (candidate.HasSigner && !candidate.IsValid && !candidate.Status.Equals("UnknownError", StringComparison.OrdinalIgnoreCase) &&
                !candidate.Status.Equals("NotTrusted", StringComparison.OrdinalIgnoreCase))
                return Loc.Tf("Цифровая подпись нового файла не подтверждена ({0}) — обновление отменено.", candidate.Status);
            return null;
        }
        if (!candidate.HasSigner)
            return Loc.T("Текущая копия подписана, а новый файл — нет. Обновление отменено.");
        if (!VendorDownloads.SignerMatches(candidate.Org, current.Org) && !VendorDownloads.SignerMatches(current.Org, candidate.Org))
            return Loc.Tf("Новый файл подписан другим издателем («{0}», а не «{1}») — обновление отменено.", candidate.Org, current.Org);
        if (current.IsValid && !candidate.IsValid)
            return Loc.Tf("Цифровая подпись нового файла не подтверждена ({0}) — обновление отменено.", candidate.Status);
        return null;
    }
}
