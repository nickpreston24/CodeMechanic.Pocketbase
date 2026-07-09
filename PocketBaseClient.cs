using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace CodeMechanic.Pocketbase;

public sealed class PocketBaseClient
{
    private readonly HttpClient http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };


    public PocketBaseClient(HttpClient http)
    {
        this.http = http;
    }

    public async Task AuthenticateAdmin(
        string email,
        string password)
    {
        var result = await PostAsync<AuthResult>(
            "api/admins/auth-with-password",
            new
            {
                identity = email,
                password
            });

        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                result.token);
    }


    public async Task<PagedResult<T>> GetCollection<T>(
        string collection,
        int page = 1,
        int perPage = 500) where T : new()
    {
        var url =
            $"api/collections/{collection}/records?page={page}&perPage={perPage}";

        var result =
            await http.GetFromJsonAsync<PagedResult<JsonElement>>(url, JsonOptions)
            ?? new();

        return new PagedResult<T>
        {
            page = result.page,
            perPage = result.perPage,
            totalItems = result.totalItems,
            totalPages = result.totalPages,

            items = result.items
                .Select(x => x.MapTo<T>())
                .ToList()
        };
    }

    public async Task<T?> GetBy<T>(
        string collection,
        string field,
        string value)
        where T : new()
    {
        var result = await Query(
            collection,
            $"{field}='{value}'");

        var item = result.items.FirstOrDefault();

        return item.ValueKind == JsonValueKind.Undefined
            ? default
            : item.MapTo<T>();
    }

    public async Task<T> Create<T>(
        string collection,
        T record)
    {
        return await PostAsync<T>(
            $"api/collections/{collection}/records",
            record);
    }


    public async Task<T> Update<T>(
        string collection,
        string id,
        T record)
    {
        using var response =
            await http.PatchAsJsonAsync(
                $"api/collections/{collection}/records/{id}",
                record,
                JsonOptions);

        response.EnsureSuccessStatusCode();

        return await response.Content
                   .ReadFromJsonAsync<T>(JsonOptions)
               ?? throw new InvalidOperationException(
                   "PocketBase returned empty response");
    }


    public async Task Delete(
        string collection,
        string id)
    {
        using var response =
            await http.DeleteAsync(
                $"api/collections/{collection}/records/{id}");

        response.EnsureSuccessStatusCode();
    }


    private async Task<T> PostAsync<T>(
        string url,
        object body)
    {
        using var response =
            await http.PostAsJsonAsync(
                url,
                body,
                JsonOptions);

        response.EnsureSuccessStatusCode();

        return await response.Content
                   .ReadFromJsonAsync<T>(JsonOptions)
               ?? throw new InvalidOperationException(
                   "PocketBase returned empty response");
    }


    public async Task<T> UpsertBy<T>(
        string collection,
        string field,
        string value,
        T record)
        where T : new()
    {
        var existing =
            await Query(
                collection,
                $"{field}='{value}'");


        if (existing.items.Count == 0)
        {
            return await Create(
                collection,
                record);
        }


        var id =
            existing.items[0]
                .GetProperty("id")
                .GetString();


        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException(
                "PocketBase record missing id");


        return await Update(
            collection,
            id,
            record);
    }

    // public async Task<T?> Upsert<T>(
    //     string collection,
    //     string hash,
    //     T record)
    // {
    //     try
    //     {
    //         return await Create(collection, record);
    //     }
    //     catch (HttpRequestException)
    //     {
    //         // lookup existing by hash
    //         // then PATCH
    //     }
    // }


    public async Task<PagedResult<JsonElement>> Query(
        string collection,
        string filter)
    {
        var url =
            $"api/collections/{collection}/records" +
            $"?filter={Uri.EscapeDataString(filter)}";

        return await http.GetFromJsonAsync<PagedResult<JsonElement>>(
                   url,
                   JsonOptions)
               ?? new();
    }
}

public sealed class PagedResult<T>
{
    public int page { get; init; }
    public int perPage { get; init; }
    public int totalItems { get; init; }
    public int totalPages { get; init; }

    public List<T> items { get; init; } = [];
}

public sealed record AuthResult(
    string token);

public static class PocketBaseMapper
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]>
        Cache = new();


    public static T MapTo<T>(
        this JsonElement element)
        where T : new()
    {
        var instance = new T();

        var properties =
            Cache.GetOrAdd(
                typeof(T),
                t => t.GetProperties(
                    BindingFlags.Public |
                    BindingFlags.Instance));


        foreach (var prop in properties)
        {
            if (!element.TryGetProperty(
                    prop.Name,
                    out var value))
                continue;


            if (value.ValueKind == JsonValueKind.Null)
                continue;


            try
            {
                var converted =
                    JsonSerializer.Deserialize(
                        value.GetRawText(),
                        prop.PropertyType,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                prop.SetValue(
                    instance,
                    converted);
            }
            catch
                // (Exception ex)
            {
                // Console.WriteLine(ex.ToString());
                Console.WriteLine("not all fields were mapped.");
                // intentionally ignore unmappable fields
                // PocketBase often has metadata fields
            }
        }

        return instance;
    }
}