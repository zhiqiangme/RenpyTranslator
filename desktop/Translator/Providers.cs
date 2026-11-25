using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RenpyTranslator;

public record ModelOption(string Id, string Label = "")
{
    public string Display => string.IsNullOrWhiteSpace(Label) || Label == Id ? Id : $"{Id}（{Label}）";
    public override string ToString() => Id;
}

public record Provider(string Id, string Name, string Url, string Model, IReadOnlyList<ModelOption> Models);

public record ProviderCatalog(int SchemaVersion, string UpdatedAt, IReadOnlyList<Provider> Items, string Source, bool FromCache);

public record CatalogRefreshResult(bool Updated, bool Skipped, ProviderCatalog? Catalog, string Message);

public static class Providers
{
    public const int SupportedSchemaVersion = 1;
    // 限制清单体积，防止异常或被篡改的超大响应耗尽内存。
    public const int MaxCatalogBytes = 1024 * 1024;
    // 后台静默刷新冷却时间，避免每次打开软件都发起网络请求。
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    public static readonly Provider Custom = new("custom", "自定义", "", "", []);

    // 主源走 jsDelivr CDN 以改善国内访问体验，失败时自动回退 GitHub 原始文件地址。
    public static readonly string[] RemoteUrls =
    [
        "https://cdn.jsdelivr.net/gh/zhiqiangme/RenpyTranslator@master/providers.json",
        "https://raw.githubusercontent.com/zhiqiangme/RenpyTranslator/master/providers.json"
    ];

    // 仅信任当前仓库对应的静态分发地址前缀。
    private static readonly string[] TrustedPrefixes =
    [
        "https://zhiqiangme.github.io/RenpyTranslator/",
        "https://cdn.jsdelivr.net/gh/zhiqiangme/RenpyTranslator@",
        "https://raw.githubusercontent.com/zhiqiangme/RenpyTranslator/"
    ];

    private static readonly object Sync = new();
    private static ProviderCatalog? current;

    public static ProviderCatalog Current
    {
        get
        {
            lock (Sync)
            {
                current ??= Load(Core.Home, BundledPath);
                return current;
            }
        }
        private set
        {
            lock (Sync) { current = value; }
        }
    }

    public static IReadOnlyList<Provider> All => Current.Items;

    internal static string BundledPath => Path.Combine(Core.Resources, "providers.json");

    internal static bool IsTrustedCatalogUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        return TrustedPrefixes.Any(prefix => url.StartsWith(prefix, StringComparison.Ordinal));
    }

    internal static string NormalizeHttpsUrl(string raw, string fieldName)
    {
        var text = raw.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new UserError($"{fieldName} 必须是合规的 HTTPS 地址，不能包含账号、查询参数或片段。");
        return text.TrimEnd('/');
    }

    public static ProviderCatalog Load()
    {
        var catalog = Load(Core.Home, BundledPath);
        Current = catalog;
        return catalog;
    }

    internal static ProviderCatalog Load(string cacheDirectory, string bundledPath)
    {
        var cachePath = Path.Combine(cacheDirectory, "providers.cache.json");
        var hasCached = TryLoadFile(cachePath, ReadMetaSource(cacheDirectory) is { Length: > 0 } cachedSource ? cachedSource : "云端缓存", fromCache: true, out var cached);
        var hasBundled = TryLoadFile(bundledPath, "内置", fromCache: false, out var bundled);
        // 软件升级后内置清单日期可能新于旧的本地云端缓存，此时优先采用内置新清单。
        if (hasCached && hasBundled)
            return string.CompareOrdinal(bundled!.UpdatedAt, cached!.UpdatedAt) > 0 ? bundled : cached!;
        if (hasCached) return cached!;
        if (hasBundled) return bundled!;
        return FallbackCatalog();
    }

    private static bool TryLoadFile(string path, string source, bool fromCache, out ProviderCatalog? catalog)
    {
        catalog = null;
        try
        {
            if (!File.Exists(path)) return false;
            Core.NoLinks(path);
            if (new FileInfo(path).Length > MaxCatalogBytes) return false;
            catalog = Parse(File.ReadAllText(path, Encoding.UTF8), source, fromCache);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static ProviderCatalog Parse(string json, string source, bool fromCache)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxCatalogBytes)
            throw new UserError("模型清单体积超出限制。");

        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw new UserError("模型清单顶层必须是 JSON 对象。"); }
        catch (JsonException ex) { throw new UserError($"模型清单 JSON 格式错误（第 {(ex.LineNumber ?? 0) + 1} 行）。", ex); }

        if (root["schema_version"] is not JsonValue schemaNode || !schemaNode.TryGetValue<int>(out var schemaVersion))
            throw new UserError("模型清单缺少有效的 schema_version。");
        if (schemaVersion != SupportedSchemaVersion)
            throw new UserError($"模型清单结构版本（v{schemaVersion}）与当前管理器不兼容。");

        var updatedAt = Core.ReadString(root, "updated_at").Trim();
        if (updatedAt.Length == 0) throw new UserError("模型清单缺少 updated_at 日期。");

        if (root["providers"] is not JsonArray list || list.Count == 0)
            throw new UserError("模型清单缺少有效的 providers 列表。");

        var items = new List<Provider> { Custom };
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in list)
        {
            if (node is not JsonObject obj) throw new UserError("模型清单中的服务商项必须是 JSON 对象。");
            var id = Core.ReadString(obj, "id").Trim();
            if (id.Length == 0 || id.Any(char.IsWhiteSpace) || id.Equals("custom", StringComparison.OrdinalIgnoreCase))
                throw new UserError($"模型清单包含无效的服务商标识：{id}");
            if (!seenIds.Add(id)) throw new UserError($"模型清单包含重复的服务商标识：{id}");

            var name = Core.ReadString(obj, "name").Trim();
            if (name.Length == 0) throw new UserError($"服务商 {id} 缺少名称。");

            var url = NormalizeHttpsUrl(Core.ReadString(obj, "base_url"), $"服务商 {name} 的 base_url");
            if (!seenUrls.Add(url)) throw new UserError($"模型清单包含重复的服务商地址：{url}");

            var defaultModel = Core.ReadString(obj, "default_model").Trim();
            if (defaultModel.Length == 0) throw new UserError($"服务商 {name} 缺少默认模型。");

            var models = new List<ModelOption>();
            var seenModels = new HashSet<string>(StringComparer.Ordinal);
            if (obj["models"] is JsonArray modelNodes)
            {
                foreach (var modelNode in modelNodes)
                {
                    if (modelNode is not JsonObject modelObj) throw new UserError($"服务商 {name} 的模型项必须是 JSON 对象。");
                    var modelId = Core.ReadString(modelObj, "id").Trim();
                    if (modelId.Length == 0) throw new UserError($"服务商 {name} 包含空的模型标识。");
                    if (!seenModels.Add(modelId)) continue;
                    var label = Core.ReadString(modelObj, "label").Trim();
                    models.Add(new ModelOption(modelId, label));
                }
            }
            if (!seenModels.Contains(defaultModel))
                models.Insert(0, new ModelOption(defaultModel, "推荐"));

            items.Add(new Provider(id, name, url, defaultModel, models));
        }

        return new ProviderCatalog(schemaVersion, updatedAt, items, source, fromCache);
    }

    public static async Task<CatalogRefreshResult> RefreshAsync(bool force, CancellationToken cancellationToken = default)
    {
        var result = await RefreshAsync(Core.Home, BundledPath, RemoteUrls, force, allowUntrustedForTest: false, cancellationToken);
        if (result.Catalog is not null) Current = result.Catalog;
        return result;
    }

    internal static async Task<CatalogRefreshResult> RefreshAsync(
        string cacheDirectory,
        string bundledPath,
        IReadOnlyList<string> urls,
        bool force,
        bool allowUntrustedForTest = false,
        CancellationToken cancellationToken = default)
    {
        var cachePath = Path.Combine(cacheDirectory, "providers.cache.json");
        var metaPath = Path.Combine(cacheDirectory, "providers.meta.json");
        var meta = ReadMeta(metaPath);

        if (!force && File.Exists(cachePath) && meta.CheckedAtUtc is { } lastChecked
            && DateTimeOffset.UtcNow - lastChecked < RefreshInterval)
        {
            return new CatalogRefreshResult(false, true, null, "模型清单在刷新周期内，暂不拉取。");
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RenpyTranslator/" + Core.ManagerVersion);

        string lastError = "无法连接到模型清单源。";
        foreach (var url in urls)
        {
            if (!allowUntrustedForTest && !IsTrustedCatalogUrl(url))
            {
                lastError = "模型清单来源不在受信任白名单内。";
                continue;
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (File.Exists(cachePath) && meta.Source == url && !string.IsNullOrWhiteSpace(meta.ETag))
                    request.Headers.TryAddWithoutValidation("If-None-Match", meta.ETag);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotModified && TryLoadFile(cachePath, url, fromCache: true, out var unchanged))
                {
                    WriteMeta(metaPath, meta.ETag, url, DateTimeOffset.UtcNow);
                    return new CatalogRefreshResult(false, false, unchanged, $"模型清单已是最新（{unchanged!.UpdatedAt}）。");
                }

                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"清单源返回 HTTP {(int)response.StatusCode}。";
                    continue;
                }

                var raw = await ReadBoundedUtf8Async(response.Content, MaxCatalogBytes, cancellationToken);
                var parsed = Parse(raw, url, fromCache: true);

                Directory.CreateDirectory(cacheDirectory);
                Core.NoLinks(cacheDirectory);
                Core.AtomicWrite(cachePath, raw);
                var etag = response.Headers.ETag?.ToString() ?? "";
                WriteMeta(metaPath, etag, url, DateTimeOffset.UtcNow);
                return new CatalogRefreshResult(true, false, parsed, $"模型清单已更新（{parsed.UpdatedAt}）。");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UserError or IOException)
            {
                lastError = Core.Explain(ex);
            }
        }

        if (force) throw new UserError("刷新模型清单失败，将继续使用当前清单。" + lastError);
        return new CatalogRefreshResult(false, false, null, "后台刷新模型清单失败：" + lastError);
    }

    private static async Task<string> ReadBoundedUtf8Async(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var declared && declared > limit)
            throw new UserError("模型清单体积超出限制。");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit) throw new UserError("模型清单体积超出限制。");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private record CacheMeta(string ETag, string Source, DateTimeOffset? CheckedAtUtc);

    private static CacheMeta ReadMeta(string path)
    {
        try
        {
            if (!File.Exists(path)) return new("", "", null);
            Core.NoLinks(path);
            var obj = Core.ReadJson(path);
            var etag = Core.ReadString(obj, "etag");
            var source = Core.ReadString(obj, "source");
            var checkedText = Core.ReadString(obj, "checked_at_utc");
            DateTimeOffset? checkedAt = DateTimeOffset.TryParse(checkedText, out var parsed) ? parsed : null;
            return new(etag, source, checkedAt);
        }
        catch (Exception)
        {
            return new("", "", null);
        }
    }

    private static string ReadMetaSource(string cacheDirectory) => ReadMeta(Path.Combine(cacheDirectory, "providers.meta.json")).Source;

    private static void WriteMeta(string path, string etag, string source, DateTimeOffset checkedAtUtc)
    {
        try
        {
            Core.WriteJson(path, new JsonObject
            {
                ["etag"] = etag,
                ["source"] = source,
                ["checked_at_utc"] = checkedAtUtc.ToString("O")
            });
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // 极端情况下内置 Resources/providers.json 缺失或损坏时的最低限度兜底。
    private static ProviderCatalog FallbackCatalog() => new(
        SupportedSchemaVersion,
        "内置兜底",
        [
            Custom,
            new("anthropic", "Anthropic Claude", "https://api.anthropic.com/v1", "claude-haiku-5-5", [new("claude-haiku-5-5", "推荐")]),
            new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-flash", [new("deepseek-flash", "推荐")]),
            new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-6-luna", [new("gpt-6-luna", "推荐")])
        ],
        "内置兜底",
        false);
}
