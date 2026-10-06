using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Casco.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.SiteRuntime;

public class StorageOptions
{
    /// <summary>"Local" (files under App:DataPath/uploads) or "R2" (Cloudflare R2).</summary>
    public string Provider { get; set; } = "Local";
    public R2Options R2 { get; set; } = new();

    public bool UseR2 => Provider.Equals("R2", StringComparison.OrdinalIgnoreCase);
}

public class R2Options
{
    /// <summary>https://ACCOUNT_ID.r2.cloudflarestorage.com</summary>
    public string ServiceUrl { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string BucketName { get; set; } = "";
    /// <summary>Lets several apps share one bucket.</summary>
    public string KeyPrefix { get; set; } = "casco";
    /// <summary>
    /// Public CDN address of the bucket (R2 custom domain, e.g. https://cdn.casco.studio).
    /// When set, visitors load files from Cloudflare directly. When empty, the API serves them.
    /// </summary>
    public string PublicUrl { get; set; } = "";
}

public record StoredObject(string Name, DateTime LastModified);

/// <summary>Flat key/value blob store for uploaded images. Keys look like "{projectId:N}/{file}".</summary>
public interface IUploadStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default);
    /// <summary>Objects directly under <paramref name="prefix"/>; names are relative to it.</summary>
    Task<List<StoredObject>> ListAsync(string prefix, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task DeletePrefixAsync(string prefix, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);
    /// <summary>CDN URL visitors should use for this key, or null when the API has to serve the file itself.</summary>
    string? PublicObjectUrl(string key);
}

public class LocalUploadStorage(IOptions<AppOptions> options, ILogger<LocalUploadStorage>? logger = null) : IUploadStorage
{
    private readonly string _root = Path.GetFullPath(Path.Combine(options.Value.DataPath, "uploads"));

    private string FullPath(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key));
        return full.StartsWith(_root + Path.DirectorySeparatorChar) ? full : throw new ArgumentException("Invalid key", nameof(key));
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        var full = FullPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var target = File.Create(full);
        await content.CopyToAsync(target, ct);
    }

    public Task<List<StoredObject>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var dir = new DirectoryInfo(FullPath(prefix.TrimEnd('/') + "/."));
        return Task.FromResult(dir.Exists
            ? dir.EnumerateFiles().Select(f => new StoredObject(f.Name, f.CreationTimeUtc)).ToList()
            : []);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(FullPath(key)));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(FullPath(key));
        return Task.CompletedTask;
    }

    public Task DeletePrefixAsync(string prefix, CancellationToken ct = default) =>
        DirectoryCleanup.DeleteAsync(FullPath(prefix.TrimEnd('/') + "/."), logger, ct);

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var full = FullPath(key);
        return Task.FromResult<Stream?>(File.Exists(full)
            ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true)
            : null);
    }

    public string? PublicObjectUrl(string key) => null;
}

public sealed class R2UploadStorage : IUploadStorage, IDisposable
{
    private readonly AmazonS3Client _s3;
    private readonly string _bucket;
    private readonly string _root;
    private readonly string? _publicBase;

    public R2UploadStorage(IOptions<StorageOptions> options)
    {
        var o = options.Value.R2;
        if (string.IsNullOrWhiteSpace(o.ServiceUrl) || string.IsNullOrWhiteSpace(o.AccessKey) || string.IsNullOrWhiteSpace(o.SecretKey) || string.IsNullOrWhiteSpace(o.BucketName))
            throw new InvalidOperationException("Storage:R2 needs ServiceUrl, AccessKey, SecretKey and BucketName.");
        _bucket = o.BucketName;
        _root = string.IsNullOrWhiteSpace(o.KeyPrefix) ? "uploads/" : $"{o.KeyPrefix.Trim('/')}/uploads/";
        _publicBase = string.IsNullOrWhiteSpace(o.PublicUrl) ? null : o.PublicUrl.TrimEnd('/');
        // R2 rejects the SDK's default flexible checksums.
        _s3 = new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), new AmazonS3Config
        {
            ServiceURL = o.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default) =>
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = _root + key,
            InputStream = content,
            ContentType = contentType,
            // Immutable file names (a guid), so the CDN can keep them for a year.
            Headers = { CacheControl = "public, max-age=31536000, immutable" },
            AutoCloseStream = false,
            // R2 does not support streaming SigV4.
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true
        }, ct);

    public async Task<List<StoredObject>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var full = _root + prefix.TrimEnd('/') + "/";
        var result = new List<StoredObject>();
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = full, Delimiter = "/" };
        do
        {
            var page = await _s3.ListObjectsV2Async(request, ct);
            foreach (var o in page.S3Objects ?? [])
                result.Add(new StoredObject(o.Key[full.Length..], o.LastModified ?? DateTime.MinValue));
            request.ContinuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (request.ContinuationToken is not null);
        return result;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_bucket, _root + key, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await _s3.DeleteObjectAsync(_bucket, _root + key, ct);

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        var full = _root + prefix.TrimEnd('/') + "/";
        var names = new List<string>();
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = full };
        do
        {
            var page = await _s3.ListObjectsV2Async(request, ct);
            foreach (var o in page.S3Objects ?? [])
                if (o.Key.StartsWith(full, StringComparison.Ordinal) && o.Key.Length > full.Length)
                    names.Add(o.Key[full.Length..]);
            request.ContinuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (request.ContinuationToken is not null);

        var folder = prefix.TrimEnd('/') + "/";
        await Parallel.ForEachAsync(names, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (name, token) => await DeleteAsync(folder + name, token));
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await _s3.GetObjectAsync(_bucket, _root + key, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public string? PublicObjectUrl(string key) => CdnObjectUrl(_publicBase, _root, key);

    /// <summary>https://cdn.example/{root}{key}, or null when no CDN domain is configured.</summary>
    public static string? CdnObjectUrl(string? publicBase, string root, string key) =>
        string.IsNullOrWhiteSpace(publicBase) ? null : $"{publicBase.TrimEnd('/')}/{root}{key.TrimStart('/')}";

    public void Dispose() => _s3.Dispose();
}
