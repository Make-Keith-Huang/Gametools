using System.IO;
using System.Text.Json;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.Services;

public sealed record RegionDraft(
    ProfileId? ProfileId,
    IReadOnlyList<CaptureRegion> Regions,
    DateTimeOffset UpdatedAtUtc);

public interface IRegionDraftStore
{
    Task<RegionDraft?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(RegionDraft draft, CancellationToken cancellationToken);
}

public sealed class JsonRegionDraftStore : IRegionDraftStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;

    public JsonRegionDraftStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<RegionDraft?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var draft = await JsonSerializer.DeserializeAsync<RegionDraft>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        return draft is null ? null : Validate(draft);
    }

    public async Task SaveAsync(RegionDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var validated = Validate(draft);
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Draft path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    validated,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static RegionDraft Validate(RegionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft.Regions);
        if (draft.Regions is not { Count: >= 1 and <= 8 })
        {
            throw new InvalidDataException("A region draft must contain between one and eight regions.");
        }

        if (draft.Regions.Select(region => region.Id).Distinct().Count() != draft.Regions.Count ||
            draft.Regions.Select(region => region.SortOrder).Distinct().Count() != draft.Regions.Count)
        {
            throw new InvalidDataException("Region IDs and sort positions must be unique.");
        }

        var ordered = draft.Regions.OrderBy(region => region.SortOrder).ToArray();
        return draft with { Regions = ordered };
    }
}

public static class RegionDraftPaths
{
    public static string GetDefaultPath()
    {
        return GameLingoAI.Infrastructure.Configuration.AppDataPaths.RegionDraft;
    }
}
